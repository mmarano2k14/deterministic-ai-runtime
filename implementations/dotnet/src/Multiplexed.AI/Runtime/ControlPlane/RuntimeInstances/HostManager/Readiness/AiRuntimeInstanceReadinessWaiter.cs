using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Readiness;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Registry;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Readiness
{
    /// <summary>
    /// Provides a provider-agnostic readiness waiter for runtime instances created through scale-out.
    /// </summary>
    /// <remarks>
    /// This waiter validates runtime instance visibility, capacity, and optional transport reachability
    /// before a scale-out request can be fulfilled.
    ///
    /// It does not dispatch runs, mutate execution state, or bypass runtime queues.
    ///
    /// IMPORTANT:
    /// - Readiness is evaluated using the execution context carried by the scale-out request.
    /// - This is required for dedicated tenant runtime instances because registry and capacity stores are tenant-visible.
    /// - Transport readiness is optional and transport-aware. HTTP is only one supported transport path.
    /// </remarks>
    public sealed class AiRuntimeInstanceReadinessWaiter : IAiRuntimeInstanceReadinessWaiter
    {
        private const string HttpTransportName = "http";

        private const string GrpcTransportName = "grpc";

        private const string DefaultCommandEndpointPath = "/runtime-instance/commands";

        private static readonly HttpClient TransportProbeHttpClient = new();

        private readonly IAiRuntimeInstanceRegistry runtimeInstanceRegistry;
        private readonly IAiRuntimeInstanceCapacityStore runtimeInstanceCapacityStore;
        private readonly IConnectionMultiplexer? redis;
        private readonly IOptions<AiRuntimeInstanceRegistrationOptions>? registrationOptions;
        private readonly IAiControlPlaneIdResolver? controlPlaneIdResolver;
        private readonly IAiRuntimeInstanceVisibilityEvaluator? visibilityEvaluator;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeInstanceReadinessWaiter"/> class.
        /// </summary>
        /// <param name="runtimeInstanceRegistry">The runtime instance registry.</param>
        /// <param name="runtimeInstanceCapacityStore">The runtime instance capacity store.</param>
        public AiRuntimeInstanceReadinessWaiter(
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            IAiRuntimeInstanceCapacityStore runtimeInstanceCapacityStore)
        {
            this.runtimeInstanceRegistry = runtimeInstanceRegistry ?? throw new ArgumentNullException(nameof(runtimeInstanceRegistry));
            this.runtimeInstanceCapacityStore = runtimeInstanceCapacityStore ?? throw new ArgumentNullException(nameof(runtimeInstanceCapacityStore));
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeInstanceReadinessWaiter"/> class.
        /// </summary>
        /// <param name="runtimeInstanceRegistry">The runtime instance registry.</param>
        /// <param name="runtimeInstanceCapacityStore">The runtime instance capacity store.</param>
        /// <param name="redis">The Redis connection multiplexer.</param>
        /// <param name="registrationOptions">The runtime instance registration options.</param>
        /// <param name="controlPlaneIdResolver">The control-plane id resolver.</param>
        /// <param name="visibilityEvaluator">The runtime instance visibility evaluator.</param>
        public AiRuntimeInstanceReadinessWaiter(
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            IAiRuntimeInstanceCapacityStore runtimeInstanceCapacityStore,
            IConnectionMultiplexer redis,
            IOptions<AiRuntimeInstanceRegistrationOptions> registrationOptions,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            IAiRuntimeInstanceVisibilityEvaluator visibilityEvaluator)
            : this(runtimeInstanceRegistry, runtimeInstanceCapacityStore)
        {
            this.redis = redis ?? throw new ArgumentNullException(nameof(redis));
            this.registrationOptions = registrationOptions ?? throw new ArgumentNullException(nameof(registrationOptions));
            this.controlPlaneIdResolver = controlPlaneIdResolver ?? throw new ArgumentNullException(nameof(controlPlaneIdResolver));
            this.visibilityEvaluator = visibilityEvaluator ?? throw new ArgumentNullException(nameof(visibilityEvaluator));
        }

        /// <inheritdoc />
        public async Task<AiRuntimeInstanceReadinessResult> WaitUntilReadyAsync(
            AiRuntimeInstanceReadinessRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var timeout = request.Timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : request.Timeout;
            var pollInterval = request.PollInterval <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(250) : request.PollInterval;
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            string? lastFailureReason = null;

            try
            {
                while (DateTimeOffset.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var checkResult = await this.CheckReadinessOnceAsync(request, cancellationToken).ConfigureAwait(false);

                    if (checkResult.Success)
                    {
                        return checkResult;
                    }

                    lastFailureReason = checkResult.FailureReason;

                    var remaining = deadline - DateTimeOffset.UtcNow;

                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    var delay = remaining < pollInterval ? remaining : pollInterval;

                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                }

                return CreateFailure(request, lastFailureReason ?? "runtime-readiness-timeout", timedOut: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CreateFailure(request, "runtime-readiness-cancelled", timedOut: false);
            }
            catch
            {
                return CreateFailure(request, "runtime-readiness-exception", timedOut: false);
            }
        }

        /// <summary>
        /// Checks runtime readiness once.
        /// </summary>
        /// <param name="request">The readiness request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The readiness result.</returns>
        private async Task<AiRuntimeInstanceReadinessResult> CheckReadinessOnceAsync(
            AiRuntimeInstanceReadinessRequest request,
            CancellationToken cancellationToken)
        {
            var stores = this.CreateRequestScopedStores(request);

            var snapshot = await stores.Registry.GetAsync(request.RuntimeInstanceId, cancellationToken).ConfigureAwait(false);

            if (snapshot is null)
            {
                return CreateFailure(request, "runtime-readiness-registry-missing", timedOut: false);
            }

            if (!string.IsNullOrWhiteSpace(request.ControlPlaneId) &&
                !string.Equals(snapshot.ControlPlaneId, request.ControlPlaneId, StringComparison.OrdinalIgnoreCase))
            {
                return CreateFailure(request, "runtime-readiness-control-plane-mismatch", timedOut: false);
            }

            var capacity = await stores.CapacityStore.GetAsync(request.RuntimeInstanceId, cancellationToken).ConfigureAwait(false);

            if (capacity is null)
            {
                return CreateFailure(request, "runtime-readiness-capacity-missing", timedOut: false);
            }

            if (snapshot.Status != AiRuntimeInstanceStatus.Ready)
            {
                return CreateFailure(request, "runtime-readiness-not-ready", timedOut: false);
            }

            if (!snapshot.CanAcceptRun)
            {
                return CreateFailure(request, "runtime-readiness-cannot-accept-run", timedOut: false);
            }

            if (snapshot.AvailableRunSlots is <= 0)
            {
                return CreateFailure(request, "runtime-readiness-capacity-unavailable", timedOut: false);
            }

            var transportReadinessResult =
                await CheckTransportReadinessAsync(
                        request,
                        snapshot.Metadata,
                        capacity.Metadata,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!transportReadinessResult.Success)
            {
                return transportReadinessResult;
            }

            return transportReadinessResult;
        }

        /// <summary>
        /// Checks transport readiness when the request requires transport endpoint readiness.
        /// </summary>
        /// <param name="request">The readiness request.</param>
        /// <param name="snapshotMetadata">The runtime instance snapshot metadata.</param>
        /// <param name="capacityMetadata">The runtime instance capacity metadata.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The readiness result.</returns>
        private static async Task<AiRuntimeInstanceReadinessResult> CheckTransportReadinessAsync(
            AiRuntimeInstanceReadinessRequest request,
            IReadOnlyDictionary<string, string>? snapshotMetadata,
            IReadOnlyDictionary<string, string>? capacityMetadata,
            CancellationToken cancellationToken)
        {
            var transportEndpoint =
                ResolveTransportEndpoint(
                    request,
                    snapshotMetadata,
                    capacityMetadata);

            if (!request.RequireTransportEndpoint)
            {
                return CreateSuccess(request, transportEndpoint);
            }

            if (string.IsNullOrWhiteSpace(transportEndpoint))
            {
                return CreateFailure(request, "runtime-readiness-transport-endpoint-missing", timedOut: false);
            }

            if (!Uri.TryCreate(transportEndpoint, UriKind.Absolute, out var endpointUri))
            {
                return CreateFailure(request, "runtime-readiness-transport-endpoint-invalid", timedOut: false);
            }

            if (IsHttpTransport(request.TransportName, endpointUri))
            {
                return await CheckHttpTransportReadinessAsync(request, endpointUri, transportEndpoint, cancellationToken).ConfigureAwait(false);
            }

            if (IsGrpcTransport(request.TransportName))
            {
                return await CheckTcpTransportReadinessAsync(request, endpointUri, transportEndpoint, cancellationToken).ConfigureAwait(false);
            }

            return await CheckGenericTransportReadinessAsync(request, endpointUri, transportEndpoint, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Checks whether an HTTP transport endpoint exposes the runtime command endpoint.
        /// </summary>
        /// <param name="request">The readiness request.</param>
        /// <param name="endpointUri">The base endpoint URI.</param>
        /// <param name="transportEndpoint">The original transport endpoint.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The readiness result.</returns>
        private static async Task<AiRuntimeInstanceReadinessResult> CheckHttpTransportReadinessAsync(
            AiRuntimeInstanceReadinessRequest request,
            Uri endpointUri,
            string transportEndpoint,
            CancellationToken cancellationToken)
        {
            try
            {
                using var timeoutCancellationTokenSource =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                timeoutCancellationTokenSource.CancelAfter(GetSingleProbeTimeout(request));

                var commandEndpoint =
                    ResolveHttpCommandReadinessEndpoint(endpointUri);

                using var message =
                    new HttpRequestMessage(HttpMethod.Get, commandEndpoint);

                using var response =
                    await TransportProbeHttpClient
                        .SendAsync(
                            message,
                            HttpCompletionOption.ResponseHeadersRead,
                            timeoutCancellationTokenSource.Token)
                        .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return CreateFailure(request, "runtime-readiness-command-endpoint-missing", timedOut: false);
                }

                return CreateSuccess(request, transportEndpoint);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return CreateFailure(request, "runtime-readiness-transport-timeout", timedOut: false);
            }
            catch (HttpRequestException)
            {
                return CreateFailure(request, "runtime-readiness-transport-unreachable", timedOut: false);
            }
            catch (InvalidOperationException)
            {
                return CreateFailure(request, "runtime-readiness-transport-invalid", timedOut: false);
            }
        }

        /// <summary>
        /// Checks whether a TCP-based transport endpoint accepts a socket connection.
        /// </summary>
        /// <param name="request">The readiness request.</param>
        /// <param name="endpointUri">The endpoint URI.</param>
        /// <param name="transportEndpoint">The original transport endpoint.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The readiness result.</returns>
        private static async Task<AiRuntimeInstanceReadinessResult> CheckTcpTransportReadinessAsync(
            AiRuntimeInstanceReadinessRequest request,
            Uri endpointUri,
            string transportEndpoint,
            CancellationToken cancellationToken)
        {
            var port = ResolvePort(endpointUri);

            if (string.IsNullOrWhiteSpace(endpointUri.Host) || port <= 0)
            {
                return CreateFailure(request, "runtime-readiness-transport-endpoint-invalid", timedOut: false);
            }

            try
            {
                using var timeoutCancellationTokenSource =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                timeoutCancellationTokenSource.CancelAfter(GetSingleProbeTimeout(request));

                using var tcpClient = new TcpClient();

                await tcpClient
                    .ConnectAsync(
                        endpointUri.Host,
                        port,
                        timeoutCancellationTokenSource.Token)
                    .ConfigureAwait(false);

                return CreateSuccess(request, transportEndpoint);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return CreateFailure(request, "runtime-readiness-transport-timeout", timedOut: false);
            }
            catch (SocketException)
            {
                return CreateFailure(request, "runtime-readiness-transport-unreachable", timedOut: false);
            }
        }

        /// <summary>
        /// Checks whether a generic transport endpoint is reachable using the safest available probe.
        /// </summary>
        /// <param name="request">The readiness request.</param>
        /// <param name="endpointUri">The endpoint URI.</param>
        /// <param name="transportEndpoint">The original transport endpoint.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The readiness result.</returns>
        private static async Task<AiRuntimeInstanceReadinessResult> CheckGenericTransportReadinessAsync(
            AiRuntimeInstanceReadinessRequest request,
            Uri endpointUri,
            string transportEndpoint,
            CancellationToken cancellationToken)
        {
            if (IsHttpTransport(request.TransportName, endpointUri))
            {
                return await CheckHttpTransportReadinessAsync(request, endpointUri, transportEndpoint, cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(endpointUri.Host) && ResolvePort(endpointUri) > 0)
            {
                return await CheckTcpTransportReadinessAsync(request, endpointUri, transportEndpoint, cancellationToken).ConfigureAwait(false);
            }

            return CreateSuccess(request, transportEndpoint);
        }

        /// <summary>
        /// Creates request-scoped registry and capacity stores when Redis dependencies are available.
        /// </summary>
        /// <param name="request">The readiness request.</param>
        /// <returns>The registry and capacity store to use for readiness checks.</returns>
        private (IAiRuntimeInstanceRegistry Registry, IAiRuntimeInstanceCapacityStore CapacityStore) CreateRequestScopedStores(
            AiRuntimeInstanceReadinessRequest request)
        {
            if (request.ExecutionContextSnapshot is null ||
                this.redis is null ||
                this.registrationOptions is null ||
                this.controlPlaneIdResolver is null ||
                this.visibilityEvaluator is null)
            {
                return (this.runtimeInstanceRegistry, this.runtimeInstanceCapacityStore);
            }

            var executionContextProvider = new FixedExecutionContextSnapshotProvider(request.ExecutionContextSnapshot);

            return (
                new RedisAiRuntimeInstanceRegistry(
                    this.redis,
                    this.registrationOptions,
                    this.controlPlaneIdResolver,
                    this.visibilityEvaluator,
                    executionContextProvider),
                new RedisAiRuntimeInstanceCapacityStore(
                    this.redis,
                    this.registrationOptions,
                    this.controlPlaneIdResolver,
                    this.visibilityEvaluator,
                    executionContextProvider));
        }

        /// <summary>
        /// Resolves the transport endpoint from the request or runtime metadata.
        /// </summary>
        /// <param name="request">The readiness request.</param>
        /// <param name="snapshotMetadata">The runtime instance snapshot metadata.</param>
        /// <param name="capacityMetadata">The runtime instance capacity metadata.</param>
        /// <returns>The resolved transport endpoint, or <see langword="null" /> when missing.</returns>
        private static string? ResolveTransportEndpoint(
            AiRuntimeInstanceReadinessRequest request,
            IReadOnlyDictionary<string, string>? snapshotMetadata,
            IReadOnlyDictionary<string, string>? capacityMetadata)
        {
            if (!string.IsNullOrWhiteSpace(request.TransportEndpoint))
            {
                return request.TransportEndpoint;
            }

            return GetMetadataValue(snapshotMetadata, AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint) ??
                   GetMetadataValue(capacityMetadata, AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint) ??
                   GetMetadataValue(snapshotMetadata, "transport.endpoint") ??
                   GetMetadataValue(capacityMetadata, "transport.endpoint");
        }

        /// <summary>
        /// Gets a metadata value using case-insensitive key matching.
        /// </summary>
        /// <param name="metadata">The metadata dictionary.</param>
        /// <param name="key">The metadata key.</param>
        /// <returns>The metadata value, or <see langword="null" /> when missing.</returns>
        private static string? GetMetadataValue(
            IReadOnlyDictionary<string, string>? metadata,
            string key)
        {
            if (metadata is null)
            {
                return null;
            }

            if (metadata.TryGetValue(key, out var value))
            {
                return value;
            }

            foreach (var item in metadata)
            {
                if (string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return item.Value;
                }
            }

            return null;
        }

        /// <summary>
        /// Determines whether the readiness request targets an HTTP-like transport.
        /// </summary>
        /// <param name="transportName">The transport name.</param>
        /// <param name="endpointUri">The endpoint URI.</param>
        /// <returns><see langword="true" /> when the transport is HTTP-like; otherwise, <see langword="false" />.</returns>
        private static bool IsHttpTransport(
            string? transportName,
            Uri endpointUri)
        {
            if (string.Equals(transportName, HttpTransportName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(endpointUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(endpointUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines whether the readiness request targets a gRPC-like transport.
        /// </summary>
        /// <param name="transportName">The transport name.</param>
        /// <returns><see langword="true" /> when the transport is gRPC-like; otherwise, <see langword="false" />.</returns>
        private static bool IsGrpcTransport(
            string? transportName)
        {
            return string.Equals(transportName, GrpcTransportName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves the TCP port for an endpoint URI.
        /// </summary>
        /// <param name="endpointUri">The endpoint URI.</param>
        /// <returns>The resolved port, or zero when no usable port exists.</returns>
        private static int ResolvePort(
            Uri endpointUri)
        {
            if (!endpointUri.IsDefaultPort)
            {
                return endpointUri.Port;
            }

            if (string.Equals(endpointUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                return 80;
            }

            if (string.Equals(endpointUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return 443;
            }

            return endpointUri.Port > 0 ? endpointUri.Port : 0;
        }

        /// <summary>
        /// Resolves the HTTP command readiness endpoint from a base runtime endpoint.
        /// </summary>
        /// <param name="baseEndpoint">The base runtime endpoint.</param>
        /// <returns>The HTTP command endpoint.</returns>
        private static Uri ResolveHttpCommandReadinessEndpoint(
            Uri baseEndpoint)
        {
            return new Uri(
                baseEndpoint.ToString().TrimEnd('/') + DefaultCommandEndpointPath);
        }

        /// <summary>
        /// Resolves the timeout used by a single transport probe attempt.
        /// </summary>
        /// <param name="request">The readiness request.</param>
        /// <returns>The timeout used by one probe attempt.</returns>
        private static TimeSpan GetSingleProbeTimeout(
            AiRuntimeInstanceReadinessRequest request)
        {
            if (request.PollInterval > TimeSpan.Zero)
            {
                return TimeSpan.FromMilliseconds(
                    Math.Max(
                        250,
                        request.PollInterval.TotalMilliseconds));
            }

            return TimeSpan.FromMilliseconds(250);
        }

        /// <summary>
        /// Creates a readiness success result.
        /// </summary>
        /// <param name="request">The readiness request.</param>
        /// <param name="transportEndpoint">The resolved transport endpoint.</param>
        /// <returns>The readiness success result.</returns>
        private static AiRuntimeInstanceReadinessResult CreateSuccess(
            AiRuntimeInstanceReadinessRequest request,
            string? transportEndpoint)
        {
            return new AiRuntimeInstanceReadinessResult
            {
                Success = true,
                ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                RuntimeInstanceId = request.RuntimeInstanceId,
                ProviderName = request.ProviderName,
                TransportName = request.TransportName,
                TransportEndpoint = transportEndpoint
            };
        }

        /// <summary>
        /// Creates a readiness failure result.
        /// </summary>
        /// <param name="request">The readiness request.</param>
        /// <param name="failureReason">The failure reason.</param>
        /// <param name="timedOut">A value indicating whether the readiness wait timed out.</param>
        /// <returns>The readiness failure result.</returns>
        private static AiRuntimeInstanceReadinessResult CreateFailure(
            AiRuntimeInstanceReadinessRequest request,
            string failureReason,
            bool timedOut)
        {
            return new AiRuntimeInstanceReadinessResult
            {
                Success = false,
                ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                RuntimeInstanceId = request.RuntimeInstanceId,
                ProviderName = request.ProviderName,
                TransportName = request.TransportName,
                TransportEndpoint = request.TransportEndpoint,
                FailureReason = failureReason,
                TimedOut = timedOut
            };
        }

        /// <summary>
        /// Provides a fixed execution context snapshot for request-scoped readiness store reads.
        /// </summary>
        private sealed class FixedExecutionContextSnapshotProvider : IExecutionContextSnapshotProvider
        {
            private readonly ExecutionContextSnapshot snapshot;

            /// <summary>
            /// Initializes a new instance of the <see cref="FixedExecutionContextSnapshotProvider"/> class.
            /// </summary>
            /// <param name="snapshot">The fixed execution context snapshot.</param>
            public FixedExecutionContextSnapshotProvider(
                ExecutionContextSnapshot snapshot)
            {
                this.snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            }

            /// <inheritdoc />
            public ExecutionContextSnapshot MapToSnapshot()
            {
                return this.snapshot;
            }
        }
    }
}