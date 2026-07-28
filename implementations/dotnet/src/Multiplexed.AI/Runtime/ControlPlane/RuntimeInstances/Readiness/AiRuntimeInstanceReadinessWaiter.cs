using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Registry;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Readiness
{
    /// <summary>
    /// Provides a provider-agnostic readiness waiter for runtime instances created through scale-out.
    /// </summary>
    /// <remarks>
    /// This waiter validates runtime instance visibility, tenant ownership, capacity, and optional transport reachability
    /// before a scale-out request can be fulfilled.
    ///
    /// It does not dispatch runs, mutate execution state, or bypass runtime queues.
    ///
    /// IMPORTANT:
    /// - Readiness is evaluated using the execution context carried by the scale-out request.
    /// - Dedicated tenant runtime instances are still validated after any exact-id fallback lookup.
    /// - Exact runtime id lookup is tried first.
    /// - A guarded unscoped exact-id lookup is allowed only to compensate for scoped-store visibility lag/mismatch.
    /// - Compatible runtime fallback remains available for Kubernetes/gRPC cases where the final runtime id can differ.
    /// - Transport readiness is optional and transport-aware. HTTP and gRPC are both supported.
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

                    var checkResult =
                        await this.CheckReadinessOnceAsync(
                                request,
                                cancellationToken)
                            .ConfigureAwait(false);

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

                return CreateFailure(
                    request,
                    lastFailureReason ?? "runtime-readiness-timeout",
                    timedOut: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CreateFailure(
                    request,
                    "runtime-readiness-cancelled",
                    timedOut: false);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"[RUNTIME READINESS EXCEPTION] " +
                    $"RuntimeInstanceId='{request.RuntimeInstanceId}', " +
                    $"ControlPlaneId='{request.ControlPlaneId}', " +
                    $"TenantId='{request.ExecutionContextSnapshot?.TenantId}', " +
                    $"TenantGroupId='{request.ExecutionContextSnapshot?.TenantGroupId}', " +
                    $"ExceptionType='{exception.GetType().FullName}', " +
                    $"Message='{exception.Message}', " +
                    $"InnerExceptionType='{exception.InnerException?.GetType().FullName}', " +
                    $"InnerMessage='{exception.InnerException?.Message}'.");

                Console.Error.WriteLine(exception);

                return CreateFailure(
                    request,
                    "runtime-readiness-exception",
                    timedOut: false);
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
            Console.WriteLine(
                $"[READINESS STAGE START] Stage='create-request-scoped-stores', RuntimeInstanceId='{request.RuntimeInstanceId}'.");

            var stores = this.CreateRequestScopedStores(request);

            Console.WriteLine(
                $"[READINESS STAGE END] Stage='create-request-scoped-stores', RuntimeInstanceId='{request.RuntimeInstanceId}'.");

            Console.WriteLine(
                $"[READINESS STAGE START] Stage='scoped-registry-get', RuntimeInstanceId='{request.RuntimeInstanceId}'.");

            var scopedSnapshot =
                await stores.Registry
                    .GetAsync(
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            Console.WriteLine(
                $"[READINESS STAGE END] Stage='scoped-registry-get', RuntimeInstanceId='{request.RuntimeInstanceId}', Found='{scopedSnapshot is not null}'.");

            Console.WriteLine(
                $"[READINESS STAGE START] Stage='scoped-capacity-get', RuntimeInstanceId='{request.RuntimeInstanceId}', RegistryFound='{scopedSnapshot is not null}'.");

            var scopedCapacity =
                scopedSnapshot is null
                    ? null
                    : await stores.CapacityStore
                        .GetAsync(
                            request.RuntimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

            Console.WriteLine(
                $"[READINESS STAGE END] Stage='scoped-capacity-get', RuntimeInstanceId='{request.RuntimeInstanceId}', Found='{scopedCapacity is not null}'.");

            Console.WriteLine(
                $"[READINESS STAGE START] Stage='unscoped-registry-get', RuntimeInstanceId='{request.RuntimeInstanceId}'.");

            var unscopedSnapshot =
                await this.runtimeInstanceRegistry
                    .GetAsync(
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            Console.WriteLine(
                $"[READINESS STAGE END] Stage='unscoped-registry-get', RuntimeInstanceId='{request.RuntimeInstanceId}', Found='{unscopedSnapshot is not null}'.");

            Console.WriteLine(
                $"[READINESS STAGE START] Stage='unscoped-capacity-get', RuntimeInstanceId='{request.RuntimeInstanceId}', RegistryFound='{unscopedSnapshot is not null}'.");

            var unscopedCapacity =
                unscopedSnapshot is null
                    ? null
                    : await this.runtimeInstanceCapacityStore
                        .GetAsync(
                            request.RuntimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

            Console.WriteLine(
                $"[READINESS STAGE END] Stage='unscoped-capacity-get', RuntimeInstanceId='{request.RuntimeInstanceId}', Found='{unscopedCapacity is not null}'.");

            Console.WriteLine(
                $"[READINESS EXACT COMPARISON] " +
                $"RuntimeInstanceId='{request.RuntimeInstanceId}', " +
                $"RequestControlPlaneId='{request.ControlPlaneId}', " +
                $"TenantId='{request.ExecutionContextSnapshot?.TenantId}', " +
                $"ScopedSnapshot='{scopedSnapshot is not null}', " +
                $"ScopedCapacity='{scopedCapacity is not null}', " +
                $"ScopedSnapshotControlPlaneId='{scopedSnapshot?.ControlPlaneId}', " +
                $"ScopedSnapshotTenantId='{scopedSnapshot?.TenantId}', " +
                $"UnscopedSnapshot='{unscopedSnapshot is not null}', " +
                $"UnscopedCapacity='{unscopedCapacity is not null}', " +
                $"UnscopedSnapshotControlPlaneId='{unscopedSnapshot?.ControlPlaneId}', " +
                $"UnscopedSnapshotTenantId='{unscopedSnapshot?.TenantId}'.");

            var exactRuntime =
                await this.TryResolveExactRuntimeAsync(
                        stores.Registry,
                        stores.CapacityStore,
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (exactRuntime is not null)
            {
                return await CheckResolvedRuntimeReadinessAsync(
                        request,
                        exactRuntime.RuntimeInstanceId,
                        exactRuntime.ControlPlaneId,
                        exactRuntime.TenantId,
                        exactRuntime.TenantGroupId,
                        exactRuntime.Status,
                        exactRuntime.CanAcceptRun,
                        exactRuntime.AvailableRunSlots,
                        exactRuntime.SnapshotMetadata,
                        exactRuntime.CapacityMetadata,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var compatibleRuntime =
                await TryResolveCompatibleRuntimeAsync(
                        stores.Registry,
                        stores.CapacityStore,
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (compatibleRuntime is null)
            {
                return CreateFailure(
                    request,
                    "runtime-readiness-compatible-registry-missing",
                    timedOut: false);
            }

            return await CheckResolvedRuntimeReadinessAsync(
                    request,
                    compatibleRuntime.RuntimeInstanceId,
                    compatibleRuntime.ControlPlaneId,
                    compatibleRuntime.TenantId,
                    compatibleRuntime.TenantGroupId,
                    compatibleRuntime.Status,
                    compatibleRuntime.CanAcceptRun,
                    compatibleRuntime.AvailableRunSlots,
                    compatibleRuntime.SnapshotMetadata,
                    compatibleRuntime.CapacityMetadata,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Tries to resolve the exact runtime instance id from scoped stores first, then from the original stores.
        /// </summary>
        /// <param name="registry">The scoped registry.</param>
        /// <param name="capacityStore">The scoped capacity store.</param>
        /// <param name="request">The readiness request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved runtime instance, or <see langword="null" /> when missing.</returns>
        private async Task<CompatibleRuntimeInstance?> TryResolveExactRuntimeAsync(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeInstanceCapacityStore capacityStore,
            AiRuntimeInstanceReadinessRequest request,
            CancellationToken cancellationToken)
        {
            var runtime =
                await TryReadRuntimeAsync(
                        registry,
                        capacityStore,
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (runtime is not null)
            {
                return runtime;
            }

            if (request.ExecutionContextSnapshot is null)
            {
                return null;
            }

            Console.WriteLine(
                $"[RUNTIME READINESS EXACT SCOPED MISS] RuntimeInstanceId='{request.RuntimeInstanceId}', ControlPlaneId='{request.ControlPlaneId}', TenantId='{request.ExecutionContextSnapshot?.TenantId}', TenantGroupId='{request.ExecutionContextSnapshot?.TenantGroupId}'.");

            runtime =
                await TryReadRuntimeAsync(
                        this.runtimeInstanceRegistry,
                        this.runtimeInstanceCapacityStore,
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            Console.WriteLine(
                $"[RUNTIME READINESS EXACT UNSCOPED LOOKUP] RuntimeInstanceId='{request.RuntimeInstanceId}', ControlPlaneId='{request.ControlPlaneId}', Found='{runtime is not null}'.");

            await DumpRegistrySnapshotsAsync(
                this.runtimeInstanceRegistry,
                request,
                "EXACT_UNSCOPED_AFTER_LOOKUP",
                cancellationToken)
            .ConfigureAwait(false);

            return runtime;
        }

        /// <summary>
        /// Reads a runtime instance and its capacity by exact runtime instance id.
        /// </summary>
        /// <param name="registry">The registry.</param>
        /// <param name="capacityStore">The capacity store.</param>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved runtime instance, or <see langword="null" /> when missing.</returns>
        private static async Task<CompatibleRuntimeInstance?> TryReadRuntimeAsync(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeInstanceCapacityStore capacityStore,
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            var snapshot =
                await registry
                    .GetAsync(
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (snapshot is null)
            {
                return null;
            }

            var capacity =
                await capacityStore
                    .GetAsync(
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (capacity is null)
            {
                return null;
            }

            return new CompatibleRuntimeInstance(
                snapshot.RuntimeInstanceId,
                snapshot.ControlPlaneId,
                snapshot.TenantId,
                snapshot.TenantGroupId,
                snapshot.Status.ToString(),
                snapshot.CanAcceptRun,
                snapshot.AvailableRunSlots,
                snapshot.Metadata,
                capacity.Metadata);
        }

        /// <summary>
        /// Checks readiness for a resolved runtime instance.
        /// </summary>
        /// <param name="request">The readiness request.</param>
        /// <param name="runtimeInstanceId">The resolved runtime instance id.</param>
        /// <param name="controlPlaneId">The resolved control-plane id.</param>
        /// <param name="tenantId">The resolved tenant id.</param>
        /// <param name="tenantGroupId">The resolved tenant group id.</param>
        /// <param name="status">The resolved runtime status.</param>
        /// <param name="canAcceptRun">A value indicating whether the runtime can accept a run.</param>
        /// <param name="availableRunSlots">The available run slot count.</param>
        /// <param name="snapshotMetadata">The runtime snapshot metadata.</param>
        /// <param name="capacityMetadata">The runtime capacity metadata.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The readiness result.</returns>
        private static async Task<AiRuntimeInstanceReadinessResult> CheckResolvedRuntimeReadinessAsync(
            AiRuntimeInstanceReadinessRequest request,
            string runtimeInstanceId,
            string? controlPlaneId,
            string? tenantId,
            string? tenantGroupId,
            string? status,
            bool canAcceptRun,
            int? availableRunSlots,
            IReadOnlyDictionary<string, string>? snapshotMetadata,
            IReadOnlyDictionary<string, string>? capacityMetadata,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(request.ControlPlaneId) &&
                !string.Equals(controlPlaneId, request.ControlPlaneId, StringComparison.OrdinalIgnoreCase))
            {
                return CreateFailure(request, "runtime-readiness-control-plane-mismatch", timedOut: false);
            }

            if (!IsTenantMatch(tenantId, request.ExecutionContextSnapshot?.TenantId))
            {
                return CreateFailure(request, "runtime-readiness-tenant-mismatch", timedOut: false);
            }

            if (!IsTenantMatch(tenantGroupId, request.ExecutionContextSnapshot?.TenantGroupId))
            {
                return CreateFailure(request, "runtime-readiness-tenant-group-mismatch", timedOut: false);
            }

            if (!string.Equals(status, AiRuntimeInstanceStatus.Ready.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return CreateFailure(request, "runtime-readiness-not-ready", timedOut: false);
            }

            if (!canAcceptRun)
            {
                return CreateFailure(request, "runtime-readiness-cannot-accept-run", timedOut: false);
            }

            if (availableRunSlots is <= 0)
            {
                return CreateFailure(request, "runtime-readiness-capacity-unavailable", timedOut: false);
            }

            var transportReadinessResult =
                await CheckTransportReadinessAsync(
                        request,
                        snapshotMetadata,
                        capacityMetadata,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!transportReadinessResult.Success)
            {
                return transportReadinessResult;
            }

            return CreateSuccess(
                request,
                runtimeInstanceId,
                transportReadinessResult.TransportEndpoint);
        }

        /// <summary>
        /// Tries to resolve a compatible ready runtime instance when the exact requested id cannot be found.
        /// </summary>
        /// <param name="registry">The registry.</param>
        /// <param name="capacityStore">The capacity store.</param>
        /// <param name="request">The readiness request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The compatible runtime instance, or <see langword="null" /> when none exists.</returns>
        private static async Task<CompatibleRuntimeInstance?> TryResolveCompatibleRuntimeAsync(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeInstanceCapacityStore capacityStore,
            AiRuntimeInstanceReadinessRequest request,
            CancellationToken cancellationToken)
        {
            var snapshots =
                await registry
                    .ListAsync(
                        includeStopped: false,
                        cancellationToken)
                    .ConfigureAwait(false);

            Console.WriteLine(
                $"[RUNTIME READINESS DEBUG] RequestedRuntimeInstanceId='{request.RuntimeInstanceId}', ControlPlaneId='{request.ControlPlaneId}', TenantId='{request.ExecutionContextSnapshot?.TenantId}', TenantGroupId='{request.ExecutionContextSnapshot?.TenantGroupId}', SnapshotCount='{snapshots.Count}', RequireTransportEndpoint='{request.RequireTransportEndpoint}'.");

            foreach (var snapshot in snapshots)
            {
                Console.WriteLine(
                    $"[RUNTIME READINESS SNAPSHOT] " +
                    $"RuntimeInstanceId='{snapshot.RuntimeInstanceId}', " +
                    $"ControlPlaneId='{snapshot.ControlPlaneId}', " +
                    $"TenantId='{snapshot.TenantId}', " +
                    $"TenantGroupId='{snapshot.TenantGroupId}', " +
                    $"Role='{snapshot.Role}', " +
                    $"Status='{snapshot.Status}', " +
                    $"CanAcceptRun='{snapshot.CanAcceptRun}', " +
                    $"AvailableRunSlots='{snapshot.AvailableRunSlots}', " +
                    $"Provider='{GetMetadataValue(snapshot.Metadata, "provider.name") ?? GetMetadataValue(snapshot.Metadata, "provider")}', " +
                    $"Transport='{GetMetadataValue(snapshot.Metadata, "transport.name")}', " +
                    $"TransportEndpoint='{GetMetadataValue(snapshot.Metadata, "transport.endpoint")}', " +
                    $"MetadataRuntimeInstanceId='{GetMetadataValue(snapshot.Metadata, "runtimeInstanceId")}', " +
                    $"MetadataRuntimeInstanceIdAlt='{GetMetadataValue(snapshot.Metadata, "runtime.instance.id")}', " +
                    $"MetadataControlPlaneId='{GetMetadataValue(snapshot.Metadata, "controlPlaneId")}', " +
                    $"MetadataRuntimeControlPlaneId='{GetMetadataValue(snapshot.Metadata, "runtime.controlPlaneId")}'.");
            }

            var requestedTenantId = request.ExecutionContextSnapshot?.TenantId;
            var requestedTenantGroupId = request.ExecutionContextSnapshot?.TenantGroupId;

            foreach (var snapshot in snapshots
                .Where(snapshot => IsCompatibleSnapshot(snapshot, request, requestedTenantId, requestedTenantGroupId))
                .OrderByDescending(snapshot => snapshot.AvailableRunSlots ?? 0)
                .ThenBy(snapshot => snapshot.RunningRunCount)
                .ThenBy(snapshot => snapshot.QueuedRunCount)
                .ThenBy(snapshot => snapshot.RuntimeInstanceId, StringComparer.OrdinalIgnoreCase))
            {
                var capacity =
                    await capacityStore
                        .GetAsync(
                            snapshot.RuntimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (capacity is null)
                {
                    Console.WriteLine(
                        $"[RUNTIME READINESS COMPATIBLE CAPACITY MISSING] RuntimeInstanceId='{snapshot.RuntimeInstanceId}'.");

                    continue;
                }

                if (!IsCompatibleProvider(request, capacity.Metadata) &&
                    !IsCompatibleProvider(request, snapshot.Metadata))
                {
                    Console.WriteLine(
                        $"[RUNTIME READINESS COMPATIBLE PROVIDER MISMATCH] RuntimeInstanceId='{snapshot.RuntimeInstanceId}', RequestedProvider='{request.ProviderName}', RequestedTransport='{request.TransportName}'.");

                    continue;
                }

                Console.WriteLine(
                    $"[RUNTIME READINESS COMPATIBLE SNAPSHOT FOUND] RuntimeInstanceId='{snapshot.RuntimeInstanceId}', ControlPlaneId='{snapshot.ControlPlaneId}', TenantId='{snapshot.TenantId}', TenantGroupId='{snapshot.TenantGroupId}', AvailableRunSlots='{snapshot.AvailableRunSlots}'.");

                return new CompatibleRuntimeInstance(
                    snapshot.RuntimeInstanceId,
                    snapshot.ControlPlaneId,
                    snapshot.TenantId,
                    snapshot.TenantGroupId,
                    snapshot.Status.ToString(),
                    snapshot.CanAcceptRun,
                    snapshot.AvailableRunSlots,
                    snapshot.Metadata,
                    capacity.Metadata);
            }

            return null;
        }

        /// <summary>
        /// Dumps runtime instance snapshots visible from a registry during readiness diagnostics.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="request">The readiness request.</param>
        /// <param name="stage">The diagnostic stage.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        private static async Task DumpRegistrySnapshotsAsync(
            IAiRuntimeInstanceRegistry registry,
            AiRuntimeInstanceReadinessRequest request,
            string stage,
            CancellationToken cancellationToken)
        {
            var snapshots =
                await registry
                    .ListAsync(
                        includeStopped: true,
                        cancellationToken)
                    .ConfigureAwait(false);

            Console.WriteLine(
                $"[RUNTIME READINESS REGISTRY DUMP] " +
                $"Stage='{stage}', " +
                $"RequestedRuntimeInstanceId='{request.RuntimeInstanceId}', " +
                $"ControlPlaneId='{request.ControlPlaneId}', " +
                $"TenantId='{request.ExecutionContextSnapshot?.TenantId}', " +
                $"TenantGroupId='{request.ExecutionContextSnapshot?.TenantGroupId}', " +
                $"SnapshotCount='{snapshots.Count}'.");

            foreach (var snapshot in snapshots)
            {
                Console.WriteLine(
                    $"[RUNTIME READINESS REGISTRY DUMP SNAPSHOT] " +
                    $"Stage='{stage}', " +
                    $"RuntimeInstanceId='{snapshot.RuntimeInstanceId}', " +
                    $"ControlPlaneId='{snapshot.ControlPlaneId}', " +
                    $"TenantId='{snapshot.TenantId}', " +
                    $"TenantGroupId='{snapshot.TenantGroupId}', " +
                    $"Role='{snapshot.Role}', " +
                    $"Status='{snapshot.Status}', " +
                    $"CanAcceptRun='{snapshot.CanAcceptRun}', " +
                    $"AvailableRunSlots='{snapshot.AvailableRunSlots}', " +
                    $"Provider='{GetMetadataValue(snapshot.Metadata, "provider.name") ?? GetMetadataValue(snapshot.Metadata, "provider")}', " +
                    $"Transport='{GetMetadataValue(snapshot.Metadata, "transport.name")}', " +
                    $"TransportEndpoint='{GetMetadataValue(snapshot.Metadata, "transport.endpoint")}'.");
            }
        }

        /// <summary>
        /// Determines whether a runtime instance snapshot is compatible with the readiness request.
        /// </summary>
        /// <param name="snapshot">The runtime instance snapshot.</param>
        /// <param name="request">The readiness request.</param>
        /// <param name="requestedTenantId">The requested tenant id.</param>
        /// <param name="requestedTenantGroupId">The requested tenant group id.</param>
        /// <returns><see langword="true" /> when the snapshot is compatible; otherwise, <see langword="false" />.</returns>
        private static bool IsCompatibleSnapshot(
            AiRuntimeInstanceSnapshot snapshot,
            AiRuntimeInstanceReadinessRequest request,
            string? requestedTenantId,
            string? requestedTenantGroupId)
        {
            if (!string.IsNullOrWhiteSpace(request.ControlPlaneId) &&
                !string.Equals(snapshot.ControlPlaneId, request.ControlPlaneId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!IsTenantMatch(snapshot.TenantId, requestedTenantId))
            {
                return false;
            }

            if (!IsTenantMatch(snapshot.TenantGroupId, requestedTenantGroupId))
            {
                return false;
            }

            if (snapshot.Role != AiRuntimeInstanceRole.Runtime)
            {
                return false;
            }

            if (snapshot.Status != AiRuntimeInstanceStatus.Ready)
            {
                return false;
            }

            if (!snapshot.CanAcceptRun)
            {
                return false;
            }

            if (snapshot.AvailableRunSlots is <= 0)
            {
                return false;
            }

            if (!IsCompatibleProvider(request, snapshot.Metadata))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Determines whether an actual tenant value strictly matches a requested tenant value.
        /// </summary>
        /// <param name="actual">The actual tenant value.</param>
        /// <param name="requested">The requested tenant value.</param>
        /// <returns><see langword="true" /> when the values match; otherwise, <see langword="false" />.</returns>
        private static bool IsTenantMatch(
            string? actual,
            string? requested)
        {
            if (string.IsNullOrWhiteSpace(requested))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(actual))
            {
                return false;
            }

            return string.Equals(
                actual,
                requested,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines whether metadata is compatible with the readiness request provider and transport.
        /// </summary>
        /// <param name="request">The readiness request.</param>
        /// <param name="metadata">The runtime metadata.</param>
        /// <returns><see langword="true" /> when compatible; otherwise, <see langword="false" />.</returns>
        private static bool IsCompatibleProvider(
            AiRuntimeInstanceReadinessRequest request,
            IReadOnlyDictionary<string, string>? metadata)
        {
            if (metadata is null)
            {
                return true;
            }

            var providerName =
                GetMetadataValue(metadata, AiRuntimeInstanceProviderMetadataKeys.ProviderName) ??
                GetMetadataValue(metadata, "provider.name") ??
                GetMetadataValue(metadata, "provider");

            if (!string.IsNullOrWhiteSpace(request.ProviderName) &&
                !string.IsNullOrWhiteSpace(providerName) &&
                !string.Equals(providerName, request.ProviderName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var transportName =
                GetMetadataValue(metadata, AiRuntimeInstanceCommandTransportMetadataKeys.TransportName) ??
                GetMetadataValue(metadata, "transport.name");

            if (!string.IsNullOrWhiteSpace(request.TransportName) &&
                !string.IsNullOrWhiteSpace(transportName) &&
                !string.Equals(transportName, request.TransportName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Creates a readiness success result for a resolved runtime instance.
        /// </summary>
        /// <param name="request">The readiness request.</param>
        /// <param name="runtimeInstanceId">The resolved runtime instance id.</param>
        /// <param name="transportEndpoint">The resolved transport endpoint.</param>
        /// <returns>The readiness success result.</returns>
        private static AiRuntimeInstanceReadinessResult CreateSuccess(
            AiRuntimeInstanceReadinessRequest request,
            string runtimeInstanceId,
            string? transportEndpoint)
        {
            return new AiRuntimeInstanceReadinessResult
            {
                Success = true,
                ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                RuntimeInstanceId = runtimeInstanceId,
                ProviderName = request.ProviderName,
                TransportName = request.TransportName,
                TransportEndpoint = transportEndpoint
            };
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

            if (IsGrpcTransport(request.TransportName))
            {
                return await CheckTcpTransportReadinessAsync(request, endpointUri, transportEndpoint, cancellationToken).ConfigureAwait(false);
            }

            if (IsHttpTransport(request.TransportName, endpointUri))
            {
                return await CheckHttpTransportReadinessAsync(request, endpointUri, transportEndpoint, cancellationToken).ConfigureAwait(false);
            }

            return await CheckGenericTransportReadinessAsync(request, endpointUri, transportEndpoint, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Checks whether an HTTP transport endpoint exposes the runtime command endpoint.
        /// </summary>
        /// <param name="request">The readiness request.</param>
        /// <param name="endpointUri">The endpoint URI.</param>
        /// <param name="transportEndpoint">The transport endpoint.</param>
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
        /// <param name="transportEndpoint">The transport endpoint.</param>
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
        /// <param name="transportEndpoint">The transport endpoint.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The readiness result.</returns>
        private static async Task<AiRuntimeInstanceReadinessResult> CheckGenericTransportReadinessAsync(
            AiRuntimeInstanceReadinessRequest request,
            Uri endpointUri,
            string transportEndpoint,
            CancellationToken cancellationToken)
        {
            if (IsGrpcTransport(request.TransportName))
            {
                return await CheckTcpTransportReadinessAsync(request, endpointUri, transportEndpoint, cancellationToken).ConfigureAwait(false);
            }

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

            var executionContextProvider =
                new FixedExecutionContextSnapshotProvider(
                    request.ExecutionContextSnapshot);

            Console.WriteLine(
                $"[READINESS STORE SELECTION] " +
                $"RequestedRuntimeInstanceId='{request.RuntimeInstanceId}', " +
                $"RequestControlPlaneId='{request.ControlPlaneId}', " +
                $"TenantId='{request.ExecutionContextSnapshot?.TenantId}', " +
                $"TenantGroupId='{request.ExecutionContextSnapshot?.TenantGroupId}', " +
                $"HasExecutionContext='{request.ExecutionContextSnapshot is not null}', " +
                $"HasRedis='{this.redis is not null}', " +
                $"HasRegistrationOptions='{this.registrationOptions is not null}', " +
                $"HasResolver='{this.controlPlaneIdResolver is not null}', " +
                $"UsingCentralResolver='true'.");

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
            ArgumentNullException.ThrowIfNull(endpointUri);

            /*
             * An explicit transport name is authoritative.
             *
             * gRPC endpoints legitimately use http:// and https:// because gRPC
             * runs over HTTP/2. They must not be classified as the HTTP command
             * transport solely from the URI scheme.
             */
            if (!string.IsNullOrWhiteSpace(transportName))
            {
                return string.Equals(
                    transportName,
                    HttpTransportName,
                    StringComparison.OrdinalIgnoreCase);
            }

            /*
             * Scheme inference is only a fallback when no transport name was supplied.
             */
            return
                string.Equals(
                    endpointUri.Scheme,
                    Uri.UriSchemeHttp,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    endpointUri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase);
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

        /// <summary>
        /// Represents a compatible runtime instance resolved from the registry.
        /// </summary>
        /// <param name="RuntimeInstanceId">The runtime instance id.</param>
        /// <param name="ControlPlaneId">The control-plane id.</param>
        /// <param name="TenantId">The tenant id.</param>
        /// <param name="TenantGroupId">The tenant group id.</param>
        /// <param name="Status">The runtime status.</param>
        /// <param name="CanAcceptRun">A value indicating whether the runtime can accept a run.</param>
        /// <param name="AvailableRunSlots">The available run slot count.</param>
        /// <param name="SnapshotMetadata">The snapshot metadata.</param>
        /// <param name="CapacityMetadata">The capacity metadata.</param>
        private sealed record CompatibleRuntimeInstance(
            string RuntimeInstanceId,
            string? ControlPlaneId,
            string? TenantId,
            string? TenantGroupId,
            string? Status,
            bool CanAcceptRun,
            int? AvailableRunSlots,
            IReadOnlyDictionary<string, string>? SnapshotMetadata,
            IReadOnlyDictionary<string, string>? CapacityMetadata);
    }
}