using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.ProcessControl;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway.Transport;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Publisher;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes
{
    /// <summary>
    /// Provides a Kubernetes runtime host creation strategy.
    /// </summary>
    /// <remarks>
    /// This strategy represents Kubernetes as a runtime host lifecycle provider.
    /// It creates Kubernetes-level runtime host resources through <see cref="IAiKubernetesRuntimeHostClient" />,
    /// waits for Kubernetes host readiness, publishes the Kubernetes-backed runtime instance into
    /// the runtime registry and capacity store, and then returns control to the provider-level provisioner.
    /// Runtime command dispatch and runtime-level readiness remain owned by the configured runtime provider,
    /// such as HTTP or gRPC.
    /// </remarks>
    public sealed class KubernetesAiRuntimeHostCreationStrategy : IAiRuntimeHostCreationStrategy, IAiRuntimeHostProcessControl, IDisposable
    {
        private const string DefaultGatewayRoutingHeaderName = "x-ai-runtime-instance-id";
        private static readonly JsonSerializerOptions GatewayGrpcProbeJsonOptions = new(JsonSerializerDefaults.Web);
        private static readonly TimeSpan MaximumGatewayGrpcProbeAttemptTimeout = TimeSpan.FromSeconds(5);

        private readonly AiKubernetesRuntimeHostOptions options;
        private readonly AiKubernetesRuntimePodSpecBuilder podSpecBuilder;
        private readonly IAiKubernetesRuntimeHostClient client;
        private readonly IAiKubernetesRuntimeInstancePublisher runtimeInstancePublisher;
        private readonly IAiRuntimeInstanceReadinessWaiter readinessWaiter;
        private readonly IAiKubernetesRuntimeGatewayManager? gatewayManager;
        private readonly IAiKubernetesGatewayTransportEndpointManager? gatewayTransportEndpointManager;
        private readonly ILogger<KubernetesAiRuntimeHostCreationStrategy> logger;
        private readonly ConcurrentDictionary<string, KubernetesPortForwardRegistration> portForwardProcesses;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> portForwardLifecycleGates;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> runtimeHostLifecycleGates;
        private readonly ConcurrentDictionary<string, AiRuntimeHostStartResult> startedRuntimeHosts;
        private readonly ConcurrentDictionary<string, AiKubernetesRuntimePodSpec> podSpecsByRuntimeInstanceId;

        /// <summary>
        /// Initializes a new instance of the <see cref="KubernetesAiRuntimeHostCreationStrategy"/> class
        /// without Gateway API lifecycle dependencies.
        /// </summary>
        /// <remarks>
        /// This overload preserves compatibility with direct test construction while Gateway mode remains disabled.
        /// Dependency injection selects the Gateway-aware overload when the Gateway services are registered.
        /// </remarks>
        /// <param name="options">The Kubernetes runtime host options.</param>
        /// <param name="podSpecBuilder">The Kubernetes runtime pod specification builder.</param>
        /// <param name="client">The Kubernetes runtime host client.</param>
        /// <param name="runtimeInstancePublisher">The Kubernetes runtime instance publisher.</param>
        /// <param name="readinessWaiter">The runtime instance readiness waiter.</param>
        /// <param name="logger">The logger.</param>
        public KubernetesAiRuntimeHostCreationStrategy(
            IOptions<AiKubernetesRuntimeHostOptions> options,
            AiKubernetesRuntimePodSpecBuilder podSpecBuilder,
            IAiKubernetesRuntimeHostClient client,
            IAiKubernetesRuntimeInstancePublisher runtimeInstancePublisher,
            IAiRuntimeInstanceReadinessWaiter readinessWaiter,
            ILogger<KubernetesAiRuntimeHostCreationStrategy> logger)
            : this(
                options,
                podSpecBuilder,
                client,
                runtimeInstancePublisher,
                readinessWaiter,
                gatewayManager: null,
                gatewayTransportEndpointManager: null,
                logger)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="KubernetesAiRuntimeHostCreationStrategy"/> class.
        /// </summary>
        /// <param name="options">The Kubernetes runtime host options.</param>
        /// <param name="podSpecBuilder">The Kubernetes runtime pod specification builder.</param>
        /// <param name="client">The Kubernetes runtime host client.</param>
        /// <param name="runtimeInstancePublisher">The Kubernetes runtime instance publisher.</param>
        /// <param name="readinessWaiter">The runtime instance readiness waiter.</param>
        /// <param name="gatewayManager">The shared Kubernetes Gateway lifecycle manager.</param>
        /// <param name="gatewayTransportEndpointManager">The shared Gateway transport endpoint manager.</param>
        /// <param name="logger">The logger.</param>
        public KubernetesAiRuntimeHostCreationStrategy(
            IOptions<AiKubernetesRuntimeHostOptions> options,
            AiKubernetesRuntimePodSpecBuilder podSpecBuilder,
            IAiKubernetesRuntimeHostClient client,
            IAiKubernetesRuntimeInstancePublisher runtimeInstancePublisher,
            IAiRuntimeInstanceReadinessWaiter readinessWaiter,
            IAiKubernetesRuntimeGatewayManager? gatewayManager,
            IAiKubernetesGatewayTransportEndpointManager? gatewayTransportEndpointManager,
            ILogger<KubernetesAiRuntimeHostCreationStrategy> logger)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(readinessWaiter);
            this.options = options.Value ?? throw new ArgumentException("Kubernetes runtime host options are required.", nameof(options));
            this.podSpecBuilder = podSpecBuilder ?? throw new ArgumentNullException(nameof(podSpecBuilder));
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.runtimeInstancePublisher = runtimeInstancePublisher ?? throw new ArgumentNullException(nameof(runtimeInstancePublisher));
            this.readinessWaiter = readinessWaiter ?? throw new ArgumentNullException(nameof(readinessWaiter));
            this.gatewayManager = gatewayManager;
            this.gatewayTransportEndpointManager = gatewayTransportEndpointManager;
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.portForwardProcesses = new ConcurrentDictionary<string, KubernetesPortForwardRegistration>(StringComparer.OrdinalIgnoreCase);
            this.portForwardLifecycleGates = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
            this.runtimeHostLifecycleGates = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
            this.startedRuntimeHosts = new ConcurrentDictionary<string, AiRuntimeHostStartResult>(StringComparer.OrdinalIgnoreCase);
            this.podSpecsByRuntimeInstanceId = new ConcurrentDictionary<string, AiKubernetesRuntimePodSpec>(StringComparer.OrdinalIgnoreCase);
        }

        /// <inheritdoc />
        public AiRuntimeHostCreationMode Mode => AiRuntimeHostCreationMode.Kubernetes;

        /// <inheritdoc />
        public async Task<AiRuntimeHostStartResult> StartAsync(
            AiRuntimeHostStartRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            this.logger.LogInformation(
                "KUBERNETES HOST START BEGIN RuntimeInstanceId={RuntimeInstanceId} ControlPlaneId={ControlPlaneId} ProviderName={ProviderName} TransportName={TransportName} TransportEndpoint={TransportEndpoint} ClientMode={ClientMode} RequireRuntimeReadiness={RequireRuntimeReadiness} RuntimeImage={RuntimeImage} Namespace={Namespace}",
                request.RuntimeInstanceId,
                request.ControlPlaneId,
                request.ProviderName,
                request.TransportName,
                request.TransportEndpoint,
                this.options.ClientMode,
                this.options.RequireRuntimeReadiness,
                this.options.RuntimeImage,
                this.options.Namespace);

            if (!this.options.Enabled)
            {
                return this.CreateRejectedWithLog(request, "kubernetes-runtime-host-creation-disabled", false, CreateBaseMetadata());
            }

            if (string.IsNullOrWhiteSpace(this.options.Namespace))
            {
                return this.CreateRejectedWithLog(request, "kubernetes-runtime-namespace-missing", false, CreateBaseMetadata());
            }

            if (string.IsNullOrWhiteSpace(this.options.RuntimeImage))
            {
                return this.CreateRejectedWithLog(request, "kubernetes-runtime-image-missing", false, CreateBaseMetadata());
            }

            if (string.IsNullOrWhiteSpace(this.options.ContainerName))
            {
                return this.CreateRejectedWithLog(request, "kubernetes-runtime-container-name-missing", false, CreateBaseMetadata());
            }

            /*
             * Every host lifecycle for one logical runtime id must converge through
             * the same gate. Gateway and direct port-forward modes are both covered.
             * This prevents duplicate scale-out requests from running readiness and
             * failure cleanup concurrently against the same live Kubernetes host.
             */
            var runtimeHostLifecycleGate =
                this.runtimeHostLifecycleGates.GetOrAdd(
                    request.RuntimeInstanceId,
                    static _ => new SemaphoreSlim(1, 1));

            await runtimeHostLifecycleGate
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                if (this.startedRuntimeHosts.TryGetValue(
                        request.RuntimeInstanceId,
                        out var existingStartedResult))
                {
                    if (!this.podSpecsByRuntimeInstanceId.TryGetValue(
                            request.RuntimeInstanceId,
                            out var existingPodSpec))
                    {
                        this.startedRuntimeHosts.TryRemove(
                            request.RuntimeInstanceId,
                            out _);

                        this.logger.LogWarning(
                            "KUBERNETES HOST START CONVERGENCE CACHE INVALIDATED RuntimeInstanceId={RuntimeInstanceId} Reason=pod-spec-missing",
                            request.RuntimeInstanceId);
                    }
                    else
                    {
                        var existingHostReadiness =
                            await this.client
                                .WaitUntilHostReadyAsync(
                                    existingPodSpec,
                                    cancellationToken)
                                .ConfigureAwait(false);

                        string? existingGatewayProbeFailure = null;

                        if (existingHostReadiness.Success)
                        {
                            existingGatewayProbeFailure =
                                await this.WaitForGatewayGrpcTransportReadinessBeforePublicationAsync(
                                        request,
                                        existingStartedResult.TransportEndpoint,
                                        existingStartedResult.Metadata,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                        }

                        if (existingHostReadiness.Success &&
                            string.IsNullOrWhiteSpace(existingGatewayProbeFailure))
                        {
                            var convergedMetadata =
                                MergeMetadata(
                                    existingStartedResult.Metadata,
                                    request.Metadata);

                            convergedMetadata =
                                MergeMetadata(
                                    convergedMetadata,
                                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                    {
                                        ["kubernetes.creation.converged"] = bool.TrueString,
                                        ["kubernetes.creation.convergence.source"] = "runtime-host-lifecycle-cache"
                                    });

                            var convergedResult =
                                AiRuntimeHostStartResult.Started(
                                    request.ExecutionContextSnapshot,
                                    request.RuntimeInstanceId,
                                    request.ProviderName,
                                    request.TransportName,
                                    existingStartedResult.TransportEndpoint,
                                    convergedMetadata);

                            this.logger.LogInformation(
                                "KUBERNETES HOST START CONVERGED RuntimeInstanceId={RuntimeInstanceId} ControlPlaneId={ControlPlaneId} ProviderName={ProviderName} TransportName={TransportName} TransportEndpoint={TransportEndpoint} Source={Source}",
                                request.RuntimeInstanceId,
                                request.ControlPlaneId,
                                request.ProviderName,
                                request.TransportName,
                                existingStartedResult.TransportEndpoint,
                                "runtime-host-lifecycle-cache");

                            return convergedResult;
                        }

                        this.startedRuntimeHosts.TryRemove(
                            request.RuntimeInstanceId,
                            out _);

                        this.logger.LogWarning(
                            "KUBERNETES HOST START CONVERGENCE CACHE INVALIDATED RuntimeInstanceId={RuntimeInstanceId} PodName={PodName} HostReadinessSuccess={HostReadinessSuccess} HostFailureReason={HostFailureReason} GatewayProbeFailure={GatewayProbeFailure}",
                            request.RuntimeInstanceId,
                            existingPodSpec.PodName,
                            existingHostReadiness.Success,
                            existingHostReadiness.FailureReason ?? "(none)",
                            existingGatewayProbeFailure ?? "(none)");
                    }
                }

                AiKubernetesRuntimePodSpec podSpec;

                try
                {
                    podSpec = this.podSpecBuilder.Build(request);
                    this.podSpecsByRuntimeInstanceId[request.RuntimeInstanceId] = podSpec;
                }
                catch (Exception exception)
                {
                    this.logger.LogWarning(
                        exception,
                        "KUBERNETES HOST POD SPEC BUILD FAILED RuntimeInstanceId={RuntimeInstanceId} Reason={Reason}",
                        request.RuntimeInstanceId,
                        exception.Message);

                    return CreateRejectedResult(request, exception.Message, false, CreateBaseMetadata());
                }

                this.logger.LogInformation(
                    "KUBERNETES HOST POD SPEC BUILT RuntimeInstanceId={RuntimeInstanceId} PodName={PodName} Namespace={Namespace} ContainerName={ContainerName} ContainerPort={ContainerPort} RuntimeImage={RuntimeImage}",
                    request.RuntimeInstanceId,
                    podSpec.PodName,
                    podSpec.Namespace,
                    podSpec.ContainerName,
                    podSpec.ContainerPort,
                    podSpec.RuntimeImage);

                var createResult =
                    await this.client
                        .CreateRuntimeHostAsync(podSpec, cancellationToken)
                        .ConfigureAwait(false);

                this.logger.LogInformation(
                    "KUBERNETES HOST CREATED RuntimeInstanceId={RuntimeInstanceId} Success={Success} PodName={PodName} ServiceName={ServiceName} FailureReason={FailureReason} Retryable={Retryable}",
                    request.RuntimeInstanceId,
                    createResult.Success,
                    createResult.PodName,
                    createResult.ServiceName,
                    createResult.FailureReason ?? "(none)",
                    createResult.Retryable);

                var metadata =
                    MergeMetadata(
                        podSpec.Annotations,
                        createResult.Metadata);

                var ownsRuntimeHostResources =
                    WasRuntimeHostCreatedByCurrentInvocation(metadata);

                if (!createResult.Success)
                {
                    return CreateRejectedResult(
                        request,
                        createResult.FailureReason ?? "kubernetes-runtime-host-create-failed",
                        createResult.Retryable,
                        metadata);
                }

                this.logger.LogInformation(
                    "KUBERNETES HOST READY WAIT BEGIN RuntimeInstanceId={RuntimeInstanceId} PodName={PodName} Namespace={Namespace} Timeout={Timeout} PollInterval={PollInterval}",
                    request.RuntimeInstanceId,
                    podSpec.PodName,
                    podSpec.Namespace,
                    this.options.ReadinessTimeout,
                    this.options.ReadinessPollInterval);

                var hostReadinessResult =
                    await this.client
                        .WaitUntilHostReadyAsync(podSpec, cancellationToken)
                        .ConfigureAwait(false);

                this.logger.LogInformation(
                    "KUBERNETES HOST READY RESULT RuntimeInstanceId={RuntimeInstanceId} Success={Success} PodName={PodName} TimedOut={TimedOut} FailureReason={FailureReason} Retryable={Retryable}",
                    request.RuntimeInstanceId,
                    hostReadinessResult.Success,
                    hostReadinessResult.PodName,
                    hostReadinessResult.TimedOut,
                    hostReadinessResult.FailureReason ?? "(none)",
                    hostReadinessResult.Retryable);

                metadata =
                    MergeMetadata(
                        metadata,
                        hostReadinessResult.Metadata);

                if (!hostReadinessResult.Success)
                {
                    await this.DeleteOnFailureIfOwnedAsync(
                            request.RuntimeInstanceId,
                            podSpec,
                            ownsRuntimeHostResources,
                            hostReadinessResult.FailureReason ?? "kubernetes-runtime-host-readiness-failed",
                            cancellationToken)
                        .ConfigureAwait(false);

                    return CreateRejectedResult(
                        request,
                        hostReadinessResult.FailureReason ?? "kubernetes-runtime-host-readiness-failed",
                        hostReadinessResult.Retryable,
                        metadata);
                }

                if (this.options.UseGatewayTransportEndpoint)
                {
                    try
                    {
                        var gatewayMetadata =
                            await this.ResolveGatewayTransportMetadataAsync(
                                    request,
                                    podSpec,
                                    createResult.ServiceName,
                                    metadata,
                                    cancellationToken)
                                .ConfigureAwait(false);

                        metadata =
                            MergeMetadata(
                                metadata,
                                gatewayMetadata);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        this.logger.LogWarning(
                            exception,
                            "KUBERNETES GATEWAY TRANSPORT RESOLUTION FAILED RuntimeInstanceId={RuntimeInstanceId} ServiceName={ServiceName} ProviderName={ProviderName} TransportName={TransportName} Reason={Reason}",
                            request.RuntimeInstanceId,
                            createResult.ServiceName,
                            request.ProviderName,
                            request.TransportName,
                            exception.Message);

                        await this.DeleteOnFailureIfOwnedAsync(
                                request.RuntimeInstanceId,
                                podSpec,
                                ownsRuntimeHostResources,
                                $"kubernetes-gateway-transport-resolution-failed:{exception.Message}",
                                cancellationToken)
                            .ConfigureAwait(false);

                        return CreateRejectedResult(
                            request,
                            $"kubernetes-gateway-transport-resolution-failed:{exception.Message}",
                            true,
                            metadata);
                    }
                }

                var usePortForwardTransportEndpoint =
                    !this.options.UseGatewayTransportEndpoint &&
                    this.ShouldUsePortForwardTransportEndpoint(request);

                SemaphoreSlim? portForwardLifecycleGate = null;

                if (usePortForwardTransportEndpoint)
                {
                    /*
                     * Several scale-out requests can converge on the same replacement
                     * runtime instance id. Serialize the port-forward/readiness/publication
                     * lifecycle only for that runtime id so one request cannot replace a
                     * tunnel while another request is dispatching through it.
                     */
                    portForwardLifecycleGate =
                        this.portForwardLifecycleGates.GetOrAdd(
                            request.RuntimeInstanceId,
                            static _ => new SemaphoreSlim(1, 1));

                    await portForwardLifecycleGate
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                try
                {
                    KubernetesPortForwardEndpoint? portForwardEndpoint = null;

                    if (usePortForwardTransportEndpoint)
                    {
                        try
                        {
                            portForwardEndpoint =
                                await this.StartPortForwardAsync(
                                        request,
                                        podSpec,
                                        createResult.ServiceName,
                                        metadata,
                                        cancellationToken)
                                    .ConfigureAwait(false);

                            metadata =
                                MergeMetadata(
                                    metadata,
                                    CreatePortForwardTransportEndpointMetadata(
                                        portForwardEndpoint.Endpoint,
                                        portForwardEndpoint.LocalPort,
                                        portForwardEndpoint.ServiceName));
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            this.logger.LogWarning(
                                exception,
                                "KUBERNETES PORT-FORWARD START FAILED RuntimeInstanceId={RuntimeInstanceId} ServiceName={ServiceName} Reason={Reason}",
                                request.RuntimeInstanceId,
                                createResult.ServiceName,
                                exception.Message);

                            await this.DeleteOnFailureIfOwnedAsync(
                                    request.RuntimeInstanceId,
                                    podSpec,
                                    ownsRuntimeHostResources,
                                    $"kubernetes-port-forward-start-failed:{exception.Message}",
                                    cancellationToken)
                                .ConfigureAwait(false);

                            return CreateRejectedResult(
                                request,
                                $"kubernetes-port-forward-start-failed:{exception.Message}",
                                true,
                                metadata);
                        }
                    }

                    var transportEndpoint =
                        ResolveKubernetesTransportEndpoint(
                            request,
                            metadata);

                    ValidateResolvedKubernetesTransportEndpoint(
                        request,
                        transportEndpoint);

                    metadata =
                        MergeMetadata(
                            metadata,
                            CreateTransportEndpointMetadata(transportEndpoint));

                    Console.WriteLine(
                        $"[KUBERNETES TRANSPORT ENDPOINT RESOLVED] RuntimeInstanceId='{request.RuntimeInstanceId}', RequestTransportEndpoint='{request.TransportEndpoint}', ResolvedTransportEndpoint='{transportEndpoint}', Metadata='{string.Join(";", metadata.Select(item => $"{item.Key}={item.Value}"))}'.");

                    this.logger.LogInformation(
                        "KUBERNETES HOST STARTED AFTER POD READINESS RuntimeInstanceId={RuntimeInstanceId} ProviderName={ProviderName} TransportName={TransportName} RequestTransportEndpoint={RequestTransportEndpoint} ResolvedTransportEndpoint={ResolvedTransportEndpoint} RequireRuntimeReadiness={RequireRuntimeReadiness}",
                        request.RuntimeInstanceId,
                        request.ProviderName,
                        request.TransportName,
                        request.TransportEndpoint,
                        transportEndpoint,
                        this.options.RequireRuntimeReadiness);

                    var startedResult =
                        AiRuntimeHostStartResult.Started(
                            request.ExecutionContextSnapshot,
                            request.RuntimeInstanceId,
                            request.ProviderName,
                            request.TransportName,
                            transportEndpoint,
                            metadata);

                    this.logger.LogInformation(
                        "KUBERNETES RUNTIME INSTANCE PUBLICATION BEGIN RuntimeInstanceId={RuntimeInstanceId} ControlPlaneId={ControlPlaneId} ProviderName={ProviderName} TransportName={TransportName} TransportEndpoint={TransportEndpoint}",
                        request.RuntimeInstanceId,
                        request.ControlPlaneId,
                        request.ProviderName,
                        request.TransportName,
                        transportEndpoint);

                    var runtimeReadinessFailure =
                        await this.WaitForRuntimeReadinessBeforePublicationAsync(
                                request,
                                podSpec,
                                transportEndpoint,
                                metadata,
                                cancellationToken)
                            .ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(runtimeReadinessFailure))
                    {
                        /*
                         * A reused port-forward belongs to a runtime already published by
                         * another converging request. A duplicate request must never tear
                         * down that live tunnel or delete the shared Kubernetes host.
                         */
                        if (portForwardEndpoint is null ||
                            !portForwardEndpoint.ReusedExisting)
                        {
                            this.StopPortForward(request.RuntimeInstanceId);

                            await this.DeleteOnFailureIfOwnedAsync(
                                    request.RuntimeInstanceId,
                                    podSpec,
                                    ownsRuntimeHostResources,
                                    runtimeReadinessFailure,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            this.logger.LogWarning(
                                "KUBERNETES RUNTIME READINESS FAILED FOR REUSED PORT-FORWARD RuntimeInstanceId={RuntimeInstanceId} Endpoint={Endpoint} FailureReason={FailureReason}. Existing tunnel and Kubernetes host are preserved.",
                                request.RuntimeInstanceId,
                                portForwardEndpoint.Endpoint,
                                runtimeReadinessFailure);
                        }

                        return CreateRejectedResult(
                            request,
                            runtimeReadinessFailure,
                            true,
                            metadata);
                    }

                    await this.runtimeInstancePublisher
                        .PublishAsync(
                            request,
                            startedResult,
                            cancellationToken)
                        .ConfigureAwait(false);

                    this.logger.LogInformation(
                        "KUBERNETES RUNTIME INSTANCE PUBLICATION COMPLETED RuntimeInstanceId={RuntimeInstanceId} ControlPlaneId={ControlPlaneId} ProviderName={ProviderName} TransportName={TransportName} TransportEndpoint={TransportEndpoint}",
                        request.RuntimeInstanceId,
                        request.ControlPlaneId,
                        request.ProviderName,
                        request.TransportName,
                        transportEndpoint);

                    this.startedRuntimeHosts[request.RuntimeInstanceId] = startedResult;

                    return startedResult;
                }
                finally
                {
                    portForwardLifecycleGate?.Release();
                }
            }
            finally
            {
                runtimeHostLifecycleGate.Release();
            }
        }

        /// <inheritdoc />
        public async Task<bool> KillAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            cancellationToken.ThrowIfCancellationRequested();

            var runtimeHostLifecycleGate =
                this.runtimeHostLifecycleGates.GetOrAdd(
                    runtimeInstanceId,
                    static _ => new SemaphoreSlim(1, 1));

            await runtimeHostLifecycleGate
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                /*
                 * Remove the convergence result before terminating the host so no later
                 * scale-out request can attach to a runtime that is being killed.
                 */
                this.startedRuntimeHosts.TryRemove(runtimeInstanceId, out _);
                this.StopPortForward(runtimeInstanceId);

                if (!this.podSpecsByRuntimeInstanceId.TryRemove(runtimeInstanceId, out var podSpec))
                {
                    this.logger.LogWarning(
                        "Kubernetes runtime host kill requested but no pod spec was registered. RuntimeInstanceId={RuntimeInstanceId}.",
                        runtimeInstanceId);

                    return false;
                }

                this.logger.LogWarning(
                    "Force-terminating Kubernetes runtime host on demand and waiting for exact pod UID disappearance. RuntimeInstanceId={RuntimeInstanceId}, PodName={PodName}, Namespace={Namespace}.",
                    runtimeInstanceId,
                    podSpec.PodName,
                    podSpec.Namespace);

                var result =
                    await this.client
                        .DeleteRuntimeHostAsync(
                            podSpec,
                            cancellationToken)
                        .ConfigureAwait(false);

                this.logger.LogWarning(
                    "Kubernetes runtime host crash termination completed. RuntimeInstanceId={RuntimeInstanceId}, PodName={PodName}, Namespace={Namespace}, Success={Success}, FailureReason={FailureReason}.",
                    runtimeInstanceId,
                    podSpec.PodName,
                    podSpec.Namespace,
                    result.Success,
                    result.FailureReason ?? "(none)");

                if (result.Success)
                {
                    await this.DeleteGatewayRuntimeRouteBestEffortAsync(
                            runtimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                    await this.runtimeInstancePublisher
                        .UnpublishAsync(
                            runtimeInstanceId,
                            "kubernetes-runtime-host-killed",
                            cancellationToken)
                        .ConfigureAwait(false);

                    this.logger.LogWarning(
                        "Kubernetes runtime instance unpublished after runtime host kill. RuntimeInstanceId={RuntimeInstanceId}, PodName={PodName}, Namespace={Namespace}.",
                        runtimeInstanceId,
                        podSpec.PodName,
                        podSpec.Namespace);
                }

                return result.Success;
            }
            finally
            {
                runtimeHostLifecycleGate.Release();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            foreach (var item in this.portForwardProcesses.ToArray())
            {
                this.StopPortForward(item.Key);
            }

            foreach (var gate in this.portForwardLifecycleGates.Values)
            {
                gate.Dispose();
            }

            foreach (var gate in this.runtimeHostLifecycleGates.Values)
            {
                gate.Dispose();
            }

            this.portForwardLifecycleGates.Clear();
            this.runtimeHostLifecycleGates.Clear();
            this.startedRuntimeHosts.Clear();
            this.podSpecsByRuntimeInstanceId.Clear();
        }


        /// <summary>
        /// Ensures the shared Kubernetes Gateway and runtime-specific route, then resolves
        /// the endpoint that the control plane must publish for HTTP or gRPC dispatch.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <param name="podSpec">The runtime pod specification.</param>
        /// <param name="serviceName">The runtime Service name returned by the host client.</param>
        /// <param name="metadata">The current Kubernetes host metadata.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The Gateway route and transport metadata.</returns>
        private async Task<IReadOnlyDictionary<string, string>> ResolveGatewayTransportMetadataAsync(
            AiRuntimeHostStartRequest request,
            AiKubernetesRuntimePodSpec podSpec,
            string? serviceName,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken)
        {
            if (this.gatewayManager is null)
            {
                throw new InvalidOperationException(
                    "kubernetes-gateway-manager-not-registered: Gateway transport mode is enabled, but IAiKubernetesRuntimeGatewayManager is unavailable.");
            }

            if (this.gatewayTransportEndpointManager is null)
            {
                throw new InvalidOperationException(
                    "kubernetes-gateway-transport-manager-not-registered: Gateway transport mode is enabled, but IAiKubernetesGatewayTransportEndpointManager is unavailable.");
            }

            var runtimeServiceName =
                ResolveServiceName(
                    serviceName,
                    metadata);

            if (string.IsNullOrWhiteSpace(runtimeServiceName))
            {
                throw new InvalidOperationException(
                    $"kubernetes-gateway-runtime-service-missing: Runtime '{request.RuntimeInstanceId}' has no Kubernetes Service available for Gateway routing.");
            }

            var gatewayEndpoint =
                await this.gatewayManager
                    .EnsureGatewayAsync(
                        request.ControlPlaneId,
                        cancellationToken)
                    .ConfigureAwait(false);

            AiKubernetesRuntimeRouteResult routeResult;

            if (IsGrpcRuntimeTransport(request))
            {
                routeResult =
                    await this.gatewayManager
                        .EnsureGrpcRouteAsync(
                            request.ControlPlaneId,
                            request.RuntimeInstanceId,
                            runtimeServiceName,
                            podSpec.ContainerPort,
                            cancellationToken)
                        .ConfigureAwait(false);
            }
            else if (IsHttpRuntimeTransport(request))
            {
                routeResult =
                    await this.gatewayManager
                        .EnsureHttpRouteAsync(
                            request.ControlPlaneId,
                            request.RuntimeInstanceId,
                            runtimeServiceName,
                            podSpec.ContainerPort,
                            cancellationToken)
                        .ConfigureAwait(false);
            }
            else
            {
                throw new InvalidOperationException(
                    $"kubernetes-gateway-transport-unsupported: Runtime '{request.RuntimeInstanceId}' uses provider '{request.ProviderName}' and transport '{request.TransportName}'. Only HTTP and gRPC Gateway routes are supported.");
            }

            var transportEndpoint =
                await this.gatewayTransportEndpointManager
                    .ResolveAsync(
                        gatewayEndpoint,
                        cancellationToken)
                    .ConfigureAwait(false);

            this.logger.LogInformation(
                "KUBERNETES GATEWAY TRANSPORT RESOLVED RuntimeInstanceId={RuntimeInstanceId} GatewayName={GatewayName} GatewayServiceName={GatewayServiceName} RouteName={RouteName} RouteKind={RouteKind} RuntimeServiceName={RuntimeServiceName} RoutingHeaderName={RoutingHeaderName} TransportEndpoint={TransportEndpoint} InternalEndpoint={InternalEndpoint} UsesPortForward={UsesPortForward} LocalPort={LocalPort}",
                request.RuntimeInstanceId,
                gatewayEndpoint.GatewayName,
                gatewayEndpoint.ServiceName,
                routeResult.RouteName,
                routeResult.RouteKind,
                routeResult.RuntimeServiceName,
                routeResult.RoutingHeaderName,
                transportEndpoint.Endpoint,
                transportEndpoint.InternalEndpoint,
                transportEndpoint.UsesPortForward,
                transportEndpoint.LocalPort);

            return CreateGatewayTransportEndpointMetadata(
                gatewayEndpoint,
                routeResult,
                transportEndpoint);
        }

        /// <summary>
        /// Determines whether the runtime transport is HTTP.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <returns><see langword="true" /> when the provider or transport is HTTP.</returns>
        private static bool IsHttpRuntimeTransport(
            AiRuntimeHostStartRequest request)
        {
            return string.Equals(
                       request.ProviderName,
                       "http",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       request.TransportName,
                       "http",
                       StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Creates metadata for a runtime exposed through the shared Kubernetes Gateway.
        /// </summary>
        /// <param name="gatewayEndpoint">The shared Gateway endpoint.</param>
        /// <param name="routeResult">The runtime-specific route.</param>
        /// <param name="transportEndpoint">The endpoint reachable by the control plane.</param>
        /// <returns>The Gateway transport metadata.</returns>
        private static IReadOnlyDictionary<string, string> CreateGatewayTransportEndpointMetadata(
            AiKubernetesGatewayEndpoint gatewayEndpoint,
            AiKubernetesRuntimeRouteResult routeResult,
            AiKubernetesGatewayTransportEndpoint transportEndpoint)
        {
            var metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] = transportEndpoint.Endpoint,
                    ["transport.endpoint"] = transportEndpoint.Endpoint,
                    ["transportEndpoint"] = transportEndpoint.Endpoint,
                    ["kubernetes.transport.endpoint.source"] =
                        transportEndpoint.UsesPortForward
                            ? "gateway-port-forward"
                            : "gateway-service",
                    ["kubernetes.gateway.name"] = gatewayEndpoint.GatewayName,
                    ["kubernetes.gateway.namespace"] = gatewayEndpoint.Namespace,
                    ["kubernetes.gateway.class.name"] = gatewayEndpoint.GatewayClassName,
                    ["kubernetes.gateway.listener.name"] = gatewayEndpoint.ListenerName,
                    ["kubernetes.gateway.listener.port"] = gatewayEndpoint.ListenerPort.ToString(),
                    ["kubernetes.gateway.service.name"] = gatewayEndpoint.ServiceName,
                    ["kubernetes.gateway.service.namespace"] = gatewayEndpoint.ServiceNamespace,
                    ["kubernetes.gateway.service.port"] = gatewayEndpoint.ServicePort.ToString(),
                    ["kubernetes.gateway.internalEndpoint"] = gatewayEndpoint.InternalEndpoint,
                    ["kubernetes.gateway.transport.endpoint"] = transportEndpoint.Endpoint,
                    ["kubernetes.gateway.transport.internalEndpoint"] = transportEndpoint.InternalEndpoint,
                    ["kubernetes.gateway.transport.usesPortForward"] = transportEndpoint.UsesPortForward.ToString(),
                    ["kubernetes.gateway.route.name"] = routeResult.RouteName,
                    ["kubernetes.gateway.route.kind"] = routeResult.RouteKind.ToString(),
                    ["kubernetes.runtime.service.name"] = routeResult.RuntimeServiceName,
                    ["kubernetes.runtime.service.port"] = routeResult.BackendPort.ToString(),
                    ["gateway.routing.header"] = routeResult.RoutingHeaderName,
                    ["gateway.routing.value"] = routeResult.RoutingHeaderValue
                };

            if (transportEndpoint.LocalPort is int localPort)
            {
                metadata["kubernetes.gateway.transport.localPort"] =
                    localPort.ToString();
            }

            return metadata;
        }

        /// <summary>
        /// Determines whether the Kubernetes runtime host must expose a local port-forward endpoint.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <returns><see langword="true" /> when a local port-forward endpoint must be used.</returns>
        private bool ShouldUsePortForwardTransportEndpoint(
            AiRuntimeHostStartRequest request)
        {
            if (this.options.UsePortForwardTransportEndpoint)
            {
                return true;
            }

            if (IsGrpcRuntimeTransport(request))
            {
                return true;
            }

            return IsUnsafeLocalhostKubernetesEndpoint(
                request.TransportEndpoint);
        }

        /// <summary>
        /// Resolves the local TCP port used by kubectl port-forward.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <returns>The local TCP port.</returns>
        private int ResolvePortForwardLocalPort(
            AiRuntimeHostStartRequest request)
        {
            if (IsGrpcRuntimeTransport(request))
            {
                return GetFreeTcpPort();
            }

            if (this.options.PortForwardLocalPort > 0 &&
                this.portForwardProcesses.IsEmpty)
            {
                return this.options.PortForwardLocalPort;
            }

            return GetFreeTcpPort();
        }

        /// <summary>
        /// Determines whether the runtime transport is gRPC.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <returns><see langword="true" /> when the provider or transport is gRPC.</returns>
        private static bool IsGrpcRuntimeTransport(
            AiRuntimeHostStartRequest request)
        {
            return string.Equals(
                       request.ProviderName,
                       "grpc",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       request.TransportName,
                       "grpc",
                       StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines whether a Kubernetes transport endpoint is unsafe for multi-pod dispatch.
        /// </summary>
        /// <param name="transportEndpoint">The transport endpoint.</param>
        /// <returns><see langword="true" /> when the endpoint is unsafe.</returns>
        private static bool IsUnsafeLocalhostKubernetesEndpoint(
            string? transportEndpoint)
        {
            if (string.IsNullOrWhiteSpace(transportEndpoint))
            {
                return false;
            }

            return transportEndpoint.Contains(
                       "127.0.0.1:8080",
                       StringComparison.OrdinalIgnoreCase) ||
                   transportEndpoint.Contains(
                       "localhost:8080",
                       StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Validates that the resolved Kubernetes transport endpoint is safe for dispatch.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <param name="transportEndpoint">The resolved transport endpoint.</param>
        private static void ValidateResolvedKubernetesTransportEndpoint(
            AiRuntimeHostStartRequest request,
            string? transportEndpoint)
        {
            if (!IsGrpcRuntimeTransport(request))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(transportEndpoint))
            {
                throw new InvalidOperationException(
                    $"Kubernetes gRPC runtime endpoint was not resolved. RuntimeInstanceId='{request.RuntimeInstanceId}'.");
            }

            if (IsUnsafeLocalhostKubernetesEndpoint(transportEndpoint))
            {
                throw new InvalidOperationException(
                    $"Kubernetes gRPC runtime endpoint is not unique for multi-pod dispatch. RuntimeInstanceId='{request.RuntimeInstanceId}', Endpoint='{transportEndpoint}'. Enable port-forward or publish a unique NodePort/service endpoint.");
            }
        }

        /// <summary>
        /// Waits for the runtime command endpoint to become ready before the Kubernetes-backed runtime instance is published.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <param name="podSpec">The Kubernetes runtime pod specification.</param>
        /// <param name="transportEndpoint">The resolved transport endpoint.</param>
        /// <param name="metadata">The runtime metadata.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A failure reason when readiness fails; otherwise, <see langword="null"/>.</returns>
        private async Task<string?> WaitForRuntimeReadinessBeforePublicationAsync(
            AiRuntimeHostStartRequest request,
            AiKubernetesRuntimePodSpec podSpec,
            string? transportEndpoint,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken)
        {
            if (!this.options.RequireRuntimeReadiness)
            {
                return null;
            }

            var requireTransportEndpoint =
                IsGrpcRuntimeTransport(request) ||
                ShouldRequireTransportEndpoint(metadata);

            this.logger.LogInformation(
                "KUBERNETES RUNTIME READINESS WAIT BEGIN RuntimeInstanceId={RuntimeInstanceId} PodName={PodName} Namespace={Namespace} ProviderName={ProviderName} TransportName={TransportName} TransportEndpoint={TransportEndpoint} RequireTransportEndpoint={RequireTransportEndpoint} Timeout={Timeout} PollInterval={PollInterval}",
                request.RuntimeInstanceId,
                podSpec.PodName,
                podSpec.Namespace,
                request.ProviderName,
                request.TransportName,
                transportEndpoint,
                requireTransportEndpoint,
                this.options.ReadinessTimeout,
                this.options.ReadinessPollInterval);

            var readinessResult =
                await this.readinessWaiter
                    .WaitUntilReadyAsync(
                        new AiRuntimeInstanceReadinessRequest
                        {
                            ControlPlaneId = request.ControlPlaneId,
                            ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                            RuntimeInstanceId = request.RuntimeInstanceId,
                            ProviderName = request.ProviderName,
                            TransportName = request.TransportName,
                            TransportEndpoint = transportEndpoint,
                            RequireTransportEndpoint = requireTransportEndpoint,
                            Timeout = this.options.ReadinessTimeout,
                            PollInterval = this.options.ReadinessPollInterval
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            this.logger.LogInformation(
                "KUBERNETES RUNTIME READINESS WAIT RESULT RuntimeInstanceId={RuntimeInstanceId} PodName={PodName} Namespace={Namespace} Success={Success} TimedOut={TimedOut} FailureReason={FailureReason} TransportEndpoint={TransportEndpoint}",
                request.RuntimeInstanceId,
                podSpec.PodName,
                podSpec.Namespace,
                readinessResult.Success,
                readinessResult.TimedOut,
                readinessResult.FailureReason ?? "(none)",
                readinessResult.TransportEndpoint ?? "(null)");

            if (!readinessResult.Success)
            {
                return readinessResult.FailureReason ?? "kubernetes-runtime-command-readiness-failed";
            }

            return await this
                .WaitForGatewayGrpcTransportReadinessBeforePublicationAsync(
                    request,
                    transportEndpoint,
                    metadata,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies the complete shared-Gateway gRPC route before runtime capacity is published.
        /// </summary>
        /// <remarks>
        /// Kubernetes pod readiness, Service endpoint readiness and Gateway route conditions are
        /// necessary but do not prove that a command sent through the shared Gateway reaches the
        /// exact runtime selected by <c>x-ai-runtime-instance-id</c>. This probe invokes the real
        /// runtime gRPC command service through the resolved shared endpoint and requires a
        /// successful queue-status response from the expected runtime instance.
        /// </remarks>
        /// <param name="request">The runtime host start request.</param>
        /// <param name="transportEndpoint">The resolved shared Gateway endpoint.</param>
        /// <param name="metadata">The runtime and Gateway route metadata.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A failure reason when the route does not become command-routable; otherwise, <see langword="null"/>.</returns>
        private async Task<string?> WaitForGatewayGrpcTransportReadinessBeforePublicationAsync(
            AiRuntimeHostStartRequest request,
            string? transportEndpoint,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken)
        {
            if (!this.options.UseGatewayTransportEndpoint ||
                !IsGrpcRuntimeTransport(request))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(transportEndpoint) ||
                !Uri.TryCreate(transportEndpoint, UriKind.Absolute, out var endpoint))
            {
                return $"kubernetes-gateway-grpc-probe-endpoint-invalid:{transportEndpoint ?? "(null)"}";
            }

            var routingHeaderName =
                ResolveGatewayGrpcProbeRoutingHeaderName(metadata);

            if (!IsValidGrpcMetadataKey(routingHeaderName))
            {
                return $"kubernetes-gateway-grpc-probe-routing-header-invalid:{routingHeaderName}";
            }

            var timeout =
                this.options.ReadinessTimeout > TimeSpan.Zero
                    ? this.options.ReadinessTimeout
                    : TimeSpan.FromSeconds(60);

            var pollInterval =
                this.options.GatewayReadinessPollInterval > TimeSpan.Zero
                    ? this.options.GatewayReadinessPollInterval
                    : this.options.ReadinessPollInterval > TimeSpan.Zero
                        ? this.options.ReadinessPollInterval
                        : TimeSpan.FromMilliseconds(500);

            var correlationId =
                $"kubernetes-gateway-grpc-probe:{request.RuntimeInstanceId}";

            var probeMetadata =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["kubernetes.gateway.grpc.probe"] = "true",
                    ["runtime.instance.id"] = request.RuntimeInstanceId,
                    ["gateway.routing.header"] = routingHeaderName,
                    ["gateway.routing.value"] = request.RuntimeInstanceId
                };

            var commandRequest =
                new AiRuntimeInstanceCommandRequest
                {
                    Operation = AiRuntimeInstanceCommandOperation.GetQueueStatus,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    QueueRequest =
                        new AiRuntimeQueueControlPlaneRequest
                        {
                            Operation = AiRuntimeQueueControlPlaneOperation.GetQueueStatus,
                            RuntimeInstanceId = request.RuntimeInstanceId,
                            CorrelationId = correlationId,
                            RequestedBy = "kubernetes-runtime-host-readiness",
                            Source = "kubernetes-gateway-grpc-probe",
                            Reason = "verify-shared-gateway-route-before-publication",
                            Metadata = probeMetadata
                        },
                    CorrelationId = correlationId,
                    RequestedBy = "kubernetes-runtime-host-readiness",
                    Source = "kubernetes-gateway-grpc-probe",
                    Reason = "verify-shared-gateway-route-before-publication",
                    Metadata = probeMetadata
                };

            var grpcRequest =
                new AiRuntimeInstanceGrpcCommandRequest
                {
                    RequestJson = JsonSerializer.Serialize(
                        commandRequest,
                        GatewayGrpcProbeJsonOptions)
                };

            var routingHeaders =
                new Metadata
                {
                    { routingHeaderName, request.RuntimeInstanceId }
                };

            var stopwatch =
                Stopwatch.StartNew();

            var attempt = 0;
            var lastFailureReason =
                "gateway-route-not-yet-command-routable";

            this.logger.LogInformation(
                "KUBERNETES GATEWAY GRPC PROBE BEGIN RuntimeInstanceId={RuntimeInstanceId} Endpoint={Endpoint} RoutingHeaderName={RoutingHeaderName} Timeout={Timeout} PollInterval={PollInterval}",
                request.RuntimeInstanceId,
                endpoint,
                routingHeaderName,
                timeout,
                pollInterval);

            Console.WriteLine(
                $"[KUBERNETES GATEWAY GRPC PROBE BEGIN] RuntimeInstanceId='{request.RuntimeInstanceId}', Endpoint='{endpoint}', RoutingHeaderName='{routingHeaderName}', Timeout='{timeout}', PollInterval='{pollInterval}'.");

            using var channel =
                GrpcChannel.ForAddress(endpoint);

            var client =
                new AiRuntimeInstanceCommandGrpc.AiRuntimeInstanceCommandGrpcClient(channel);

            while (stopwatch.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempt++;

                var remaining =
                    timeout - stopwatch.Elapsed;

                var attemptTimeout =
                    remaining < MaximumGatewayGrpcProbeAttemptTimeout
                        ? remaining
                        : MaximumGatewayGrpcProbeAttemptTimeout;

                if (attemptTimeout <= TimeSpan.Zero)
                {
                    break;
                }

                using var attemptCancellationTokenSource =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                attemptCancellationTokenSource.CancelAfter(attemptTimeout);

                try
                {
                    var grpcResponse =
                        await client
                            .ExecuteCommandAsync(
                                grpcRequest,
                                headers: routingHeaders,
                                cancellationToken: attemptCancellationTokenSource.Token)
                            .ResponseAsync
                            .ConfigureAwait(false);

                    var validationFailure =
                        ValidateGatewayGrpcProbeResponse(
                            request.RuntimeInstanceId,
                            grpcResponse);

                    if (string.IsNullOrWhiteSpace(validationFailure))
                    {
                        this.logger.LogInformation(
                            "KUBERNETES GATEWAY GRPC PROBE COMPLETED RuntimeInstanceId={RuntimeInstanceId} Endpoint={Endpoint} RoutingHeaderName={RoutingHeaderName} Attempts={Attempts} Elapsed={Elapsed}",
                            request.RuntimeInstanceId,
                            endpoint,
                            routingHeaderName,
                            attempt,
                            stopwatch.Elapsed);

                        Console.WriteLine(
                            $"[KUBERNETES GATEWAY GRPC PROBE COMPLETED] RuntimeInstanceId='{request.RuntimeInstanceId}', Endpoint='{endpoint}', RoutingHeaderName='{routingHeaderName}', Attempts='{attempt}', Elapsed='{stopwatch.Elapsed}'.");

                        return null;
                    }

                    lastFailureReason = validationFailure;
                }
                catch (RpcException exception)
                    when (exception.StatusCode is StatusCode.Unavailable or
                          StatusCode.DeadlineExceeded or
                          StatusCode.Cancelled)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    lastFailureReason =
                        $"grpc-{exception.StatusCode.ToString().ToLowerInvariant()}:{exception.Status.Detail}";
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    lastFailureReason =
                        $"grpc-probe-attempt-timeout:{attemptTimeout}";
                }
                catch (JsonException exception)
                {
                    lastFailureReason =
                        $"grpc-probe-response-json-invalid:{exception.Message}";
                }
                catch (Exception exception)
                    when (exception is not OperationCanceledException)
                {
                    lastFailureReason =
                        $"grpc-probe-failed:{exception.GetType().Name}:{exception.Message}";
                }

                this.logger.LogInformation(
                    "KUBERNETES GATEWAY GRPC PROBE ATTEMPT FAILED RuntimeInstanceId={RuntimeInstanceId} Endpoint={Endpoint} Attempt={Attempt} Elapsed={Elapsed} FailureReason={FailureReason}",
                    request.RuntimeInstanceId,
                    endpoint,
                    attempt,
                    stopwatch.Elapsed,
                    lastFailureReason);

                Console.WriteLine(
                    $"[KUBERNETES GATEWAY GRPC PROBE ATTEMPT FAILED] RuntimeInstanceId='{request.RuntimeInstanceId}', Endpoint='{endpoint}', Attempt='{attempt}', Elapsed='{stopwatch.Elapsed}', FailureReason='{lastFailureReason}'.");

                var delay =
                    timeout - stopwatch.Elapsed < pollInterval
                        ? timeout - stopwatch.Elapsed
                        : pollInterval;

                if (delay > TimeSpan.Zero)
                {
                    await Task
                        .Delay(delay, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            this.logger.LogWarning(
                "KUBERNETES GATEWAY GRPC PROBE TIMED OUT RuntimeInstanceId={RuntimeInstanceId} Endpoint={Endpoint} RoutingHeaderName={RoutingHeaderName} Attempts={Attempts} Elapsed={Elapsed} LastFailureReason={LastFailureReason}",
                request.RuntimeInstanceId,
                endpoint,
                routingHeaderName,
                attempt,
                stopwatch.Elapsed,
                lastFailureReason);

            Console.WriteLine(
                $"[KUBERNETES GATEWAY GRPC PROBE TIMED OUT] RuntimeInstanceId='{request.RuntimeInstanceId}', Endpoint='{endpoint}', RoutingHeaderName='{routingHeaderName}', Attempts='{attempt}', Elapsed='{stopwatch.Elapsed}', LastFailureReason='{lastFailureReason}'.");

            return
                $"kubernetes-gateway-grpc-probe-timeout:{lastFailureReason}";
        }

        /// <summary>
        /// Validates the application-level gRPC probe response and target runtime identity.
        /// </summary>
        private static string? ValidateGatewayGrpcProbeResponse(
            string expectedRuntimeInstanceId,
            AiRuntimeInstanceGrpcCommandResponse? response)
        {
            if (response is null ||
                string.IsNullOrWhiteSpace(response.ResponseJson))
            {
                return "grpc-probe-response-empty";
            }

            var commandResult =
                JsonSerializer.Deserialize<AiRuntimeInstanceCommandResult>(
                    response.ResponseJson,
                    GatewayGrpcProbeJsonOptions);

            if (commandResult is null)
            {
                return "grpc-probe-command-result-null";
            }

            if (!string.Equals(
                    commandResult.RuntimeInstanceId,
                    expectedRuntimeInstanceId,
                    StringComparison.Ordinal))
            {
                return
                    $"grpc-probe-runtime-mismatch:expected={expectedRuntimeInstanceId};actual={commandResult.RuntimeInstanceId ?? "(null)"}";
            }

            if (!commandResult.Success)
            {
                return
                    $"grpc-probe-command-failed:{commandResult.FailureReason ?? commandResult.Message ?? "unknown"}";
            }

            if (commandResult.QueueResult is null)
            {
                return "grpc-probe-queue-result-missing";
            }

            if (!string.Equals(
                    commandResult.QueueResult.RuntimeInstanceId,
                    expectedRuntimeInstanceId,
                    StringComparison.Ordinal))
            {
                return
                    $"grpc-probe-queue-runtime-mismatch:expected={expectedRuntimeInstanceId};actual={commandResult.QueueResult.RuntimeInstanceId ?? "(null)"}";
            }

            if (!commandResult.QueueResult.Success)
            {
                return
                    $"grpc-probe-queue-command-failed:{commandResult.QueueResult.FailureReason ?? commandResult.QueueResult.Message ?? "unknown"}";
            }

            return null;
        }

        /// <summary>
        /// Resolves the exact gRPC metadata header used by the shared Gateway route.
        /// </summary>
        private string ResolveGatewayGrpcProbeRoutingHeaderName(
            IReadOnlyDictionary<string, string> metadata)
        {
            if (TryGetMetadataValue(
                    metadata,
                    "gateway.routing.header",
                    out var metadataHeaderName) &&
                !string.IsNullOrWhiteSpace(metadataHeaderName))
            {
                return metadataHeaderName.Trim().ToLowerInvariant();
            }

            if (!string.IsNullOrWhiteSpace(this.options.GatewayRouteHeaderName))
            {
                return this.options.GatewayRouteHeaderName.Trim().ToLowerInvariant();
            }

            return DefaultGatewayRoutingHeaderName;
        }

        /// <summary>
        /// Determines whether a routing header name can be emitted as gRPC metadata.
        /// </summary>
        private static bool IsValidGrpcMetadataKey(
            string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            foreach (var character in value)
            {
                if ((character >= 'a' && character <= 'z') ||
                    (character >= '0' && character <= '9') ||
                    character is '-' or '_' or '.')
                {
                    continue;
                }

                return false;
            }

            return true;
        }

        /// <summary>
        /// Determines whether runtime readiness must require a transport endpoint.
        /// </summary>
        /// <param name="metadata">The runtime metadata.</param>
        /// <returns><see langword="true"/> when a transport endpoint is required.</returns>
        private static bool ShouldRequireTransportEndpoint(
            IReadOnlyDictionary<string, string> metadata)
        {
            if (TryGetMetadataValue(metadata, "transport.name", out var transportName) &&
                string.Equals(transportName, "grpc", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (TryGetMetadataValue(metadata, "provider.name", out var providerName) &&
                string.Equals(providerName, "grpc", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (TryGetMetadataValue(metadata, "provider", out var providerAlias) &&
                string.Equals(providerAlias, "grpc", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Starts a local kubectl port-forward process for a Kubernetes pod.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <param name="podSpec">The Kubernetes runtime pod specification.</param>
        /// <param name="serviceName">The Kubernetes service name, when available.</param>
        /// <param name="metadata">The Kubernetes metadata.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The local port-forward endpoint.</returns>
        private async Task<KubernetesPortForwardEndpoint> StartPortForwardAsync(
            AiRuntimeHostStartRequest request,
            AiKubernetesRuntimePodSpec podSpec,
            string? serviceName,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(podSpec);
            ArgumentNullException.ThrowIfNull(metadata);

            serviceName =
                ResolveServiceName(
                    serviceName,
                    metadata) ?? string.Empty;

            if (this.portForwardProcesses.TryGetValue(
                    request.RuntimeInstanceId,
                    out var existingRegistration))
            {
                var processRunning =
                    IsProcessRunning(
                        existingRegistration.Process);

                if (processRunning &&
                    string.Equals(
                        existingRegistration.PodName,
                        podSpec.PodName,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        existingRegistration.Namespace,
                        podSpec.Namespace,
                        StringComparison.OrdinalIgnoreCase) &&
                    existingRegistration.RemotePort == podSpec.ContainerPort)
                {
                    this.logger.LogInformation(
                        "KUBERNETES PORT-FORWARD REUSED RuntimeInstanceId={RuntimeInstanceId} PodName={PodName} ServiceName={ServiceName} Namespace={Namespace} LocalPort={LocalPort} RemotePort={RemotePort} Endpoint={Endpoint} ProcessId={ProcessId}",
                        request.RuntimeInstanceId,
                        existingRegistration.PodName,
                        existingRegistration.ServiceName,
                        existingRegistration.Namespace,
                        existingRegistration.LocalPort,
                        existingRegistration.RemotePort,
                        existingRegistration.Endpoint,
                        existingRegistration.ProcessId);

                    Console.WriteLine(
                        $"[KUBERNETES PORT-FORWARD REUSED] RuntimeInstanceId='{request.RuntimeInstanceId}', PodName='{existingRegistration.PodName}', ServiceName='{existingRegistration.ServiceName}', Namespace='{existingRegistration.Namespace}', LocalPort='{existingRegistration.LocalPort}', RemotePort='{existingRegistration.RemotePort}', ProcessId='{existingRegistration.ProcessId}', Endpoint='{existingRegistration.Endpoint}'.");

                    return new KubernetesPortForwardEndpoint(
                        existingRegistration.ServiceName,
                        existingRegistration.LocalPort,
                        existingRegistration.Endpoint,
                        reusedExisting: true);
                }

                this.logger.LogWarning(
                    "KUBERNETES PORT-FORWARD STALE REGISTRATION RuntimeInstanceId={RuntimeInstanceId} ExistingPodName={ExistingPodName} RequestedPodName={RequestedPodName} ExistingNamespace={ExistingNamespace} RequestedNamespace={RequestedNamespace} ExistingRemotePort={ExistingRemotePort} RequestedRemotePort={RequestedRemotePort} ProcessRunning={ProcessRunning}. Replacing stale registration.",
                    request.RuntimeInstanceId,
                    existingRegistration.PodName,
                    podSpec.PodName,
                    existingRegistration.Namespace,
                    podSpec.Namespace,
                    existingRegistration.RemotePort,
                    podSpec.ContainerPort,
                    processRunning);

                this.StopPortForward(
                    request.RuntimeInstanceId);
            }

            var localPort =
                this.ResolvePortForwardLocalPort(request);

            var kubectlPath =
                string.IsNullOrWhiteSpace(this.options.KubectlPath)
                    ? "kubectl"
                    : this.options.KubectlPath;

            var processStartInfo =
                new ProcessStartInfo
                {
                    FileName = kubectlPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

            processStartInfo.ArgumentList.Add("port-forward");
            processStartInfo.ArgumentList.Add("-n");
            processStartInfo.ArgumentList.Add(podSpec.Namespace);
            processStartInfo.ArgumentList.Add($"pod/{podSpec.PodName}");
            processStartInfo.ArgumentList.Add($"{localPort}:{podSpec.ContainerPort}");

            var process =
                new DiagnosticsProcess
                {
                    StartInfo = processStartInfo,
                    EnableRaisingEvents = true
                };

            this.logger.LogInformation(
                "KUBERNETES PORT-FORWARD START BEGIN RuntimeInstanceId={RuntimeInstanceId} PodName={PodName} ServiceName={ServiceName} Namespace={Namespace} LocalPort={LocalPort} RemotePort={RemotePort} KubectlPath={KubectlPath}",
                request.RuntimeInstanceId,
                podSpec.PodName,
                serviceName,
                podSpec.Namespace,
                localPort,
                podSpec.ContainerPort,
                kubectlPath);

            if (!process.Start())
            {
                process.Dispose();

                throw new InvalidOperationException(
                    "kubectl port-forward process did not start.");
            }

            var endpoint =
                $"http://127.0.0.1:{localPort}";

            var registration =
                new KubernetesPortForwardRegistration(
                    process,
                    process.Id,
                    podSpec.PodName,
                    serviceName,
                    podSpec.Namespace,
                    localPort,
                    podSpec.ContainerPort,
                    endpoint);

            if (!this.portForwardProcesses.TryAdd(
                    request.RuntimeInstanceId,
                    registration))
            {
                KillProcess(process);
                process.Dispose();

                throw new InvalidOperationException(
                    $"A port-forward process is already registered for runtime instance '{request.RuntimeInstanceId}'.");
            }

            try
            {
                await WaitForLocalPortOpenAsync(
                        localPort,
                        this.options.PortForwardStartupTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                this.StopPortForward(
                    request.RuntimeInstanceId);

                throw;
            }

            this.logger.LogInformation(
                "KUBERNETES PORT-FORWARD STARTED RuntimeInstanceId={RuntimeInstanceId} PodName={PodName} ServiceName={ServiceName} Namespace={Namespace} LocalPort={LocalPort} RemotePort={RemotePort} Endpoint={Endpoint} ProcessId={ProcessId}",
                request.RuntimeInstanceId,
                podSpec.PodName,
                serviceName,
                podSpec.Namespace,
                localPort,
                podSpec.ContainerPort,
                endpoint,
                process.Id);

            Console.WriteLine(
                $"[KUBERNETES PORT-FORWARD STARTED] RuntimeInstanceId='{request.RuntimeInstanceId}', PodName='{podSpec.PodName}', ServiceName='{serviceName}', Namespace='{podSpec.Namespace}', LocalPort='{localPort}', RemotePort='{podSpec.ContainerPort}', ProcessId='{process.Id}', Endpoint='{endpoint}'.");

            return new KubernetesPortForwardEndpoint(
                serviceName,
                localPort,
                endpoint,
                reusedExisting: false);
        }

        /// <summary>
        /// Stops a local kubectl port-forward process for a runtime instance.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        private void StopPortForward(
            string runtimeInstanceId)
        {
            if (!this.portForwardProcesses.TryRemove(
                    runtimeInstanceId,
                    out var registration))
            {
                return;
            }

            try
            {
                KillProcess(
                    registration.Process);
            }
            finally
            {
                registration.Process.Dispose();
            }
        }

        /// <summary>
        /// Determines whether a kubectl port-forward process is still running.
        /// </summary>
        /// <param name="process">The process.</param>
        /// <returns><see langword="true"/> when the process is alive; otherwise, <see langword="false"/>.</returns>
        private static bool IsProcessRunning(
            DiagnosticsProcess process)
        {
            ArgumentNullException.ThrowIfNull(process);

            try
            {
                return !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// Kills a process if it is still running.
        /// </summary>
        /// <param name="process">The process.</param>
        private static void KillProcess(
            DiagnosticsProcess process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        /// <summary>
        /// Waits until a local TCP port is reachable.
        /// </summary>
        /// <param name="localPort">The local TCP port.</param>
        /// <param name="timeout">The timeout.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The asynchronous operation.</returns>
        private static async Task WaitForLocalPortOpenAsync(
            int localPort,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using var client = new TcpClient();

                    var connectTask =
                        client.ConnectAsync(
                            IPAddress.Loopback,
                            localPort);

                    var completedTask =
                        await Task.WhenAny(
                                connectTask,
                                Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken))
                            .ConfigureAwait(false);

                    if (completedTask == connectTask && client.Connected)
                    {
                        return;
                    }
                }
                catch (SocketException)
                {
                }
                catch (ObjectDisposedException)
                {
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(100),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new TimeoutException($"Local kubectl port-forward endpoint did not become reachable on 127.0.0.1:{localPort} within '{timeout}'.");
        }

        /// <summary>
        /// Gets a free local TCP port.
        /// </summary>
        /// <returns>The free TCP port.</returns>
        private static int GetFreeTcpPort()
        {
            var listener =
                new TcpListener(
                    IPAddress.Loopback,
                    0);

            listener.Start();

            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>
        /// Resolves the Kubernetes service name.
        /// </summary>
        /// <param name="serviceName">The service name returned by the Kubernetes client.</param>
        /// <param name="metadata">The metadata.</param>
        /// <returns>The Kubernetes service name, when available.</returns>
        private static string? ResolveServiceName(
            string? serviceName,
            IReadOnlyDictionary<string, string> metadata)
        {
            if (!string.IsNullOrWhiteSpace(serviceName))
            {
                return serviceName;
            }

            if (TryGetMetadataValue(metadata, "kubernetes.service.name", out var metadataServiceName))
            {
                return metadataServiceName;
            }

            return null;
        }

        /// <summary>
        /// Creates local port-forward transport endpoint metadata.
        /// </summary>
        /// <param name="transportEndpoint">The local transport endpoint.</param>
        /// <param name="localPort">The local port.</param>
        /// <param name="serviceName">The service name.</param>
        /// <returns>The transport endpoint metadata.</returns>
        private static IReadOnlyDictionary<string, string> CreatePortForwardTransportEndpointMetadata(
            string transportEndpoint,
            int localPort,
            string serviceName)
        {
            return new Dictionary<string, string>
            {
                [AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] = transportEndpoint,
                ["transport.endpoint"] = transportEndpoint,
                ["transportEndpoint"] = transportEndpoint,
                ["kubernetes.portForward.endpoint"] = transportEndpoint,
                ["kubernetes.portForward.localPort"] = localPort.ToString(),
                ["kubernetes.portForward.serviceName"] = serviceName,
                ["kubernetes.transport.endpoint.source"] = "port-forward"
            };
        }

        /// <summary>
        /// Creates a rejected runtime host start result while logging the structured reason.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <param name="failureReason">The failure reason.</param>
        /// <param name="retryable">A value indicating whether the failure is retryable.</param>
        /// <param name="metadata">The result metadata.</param>
        /// <returns>The rejected runtime host start result.</returns>
        private AiRuntimeHostStartResult CreateRejectedWithLog(
            AiRuntimeHostStartRequest request,
            string failureReason,
            bool retryable,
            IReadOnlyDictionary<string, string> metadata)
        {
            this.logger.LogWarning(
                "KUBERNETES HOST START REJECTED RuntimeInstanceId={RuntimeInstanceId} Reason={Reason}",
                request.RuntimeInstanceId,
                failureReason);

            return CreateRejectedResult(
                request,
                failureReason,
                retryable,
                metadata);
        }

        /// <summary>
        /// Determines whether this invocation created the Kubernetes pod and Service.
        /// </summary>
        /// <remarks>
        /// The SDK client reports whether the deterministic pod or Service already existed.
        /// A converging invocation that attaches to either existing resource is never allowed
        /// to delete the shared runtime host when its own readiness observation fails.
        /// </remarks>
        /// <param name="metadata">The Kubernetes host creation metadata.</param>
        /// <returns><see langword="true" /> only when the host resources belong to this invocation.</returns>
        private static bool WasRuntimeHostCreatedByCurrentInvocation(
            IReadOnlyDictionary<string, string> metadata)
        {
            ArgumentNullException.ThrowIfNull(metadata);

            if (TryGetBooleanMetadataValue(
                    metadata,
                    "kubernetes.pod.alreadyExists",
                    out var podAlreadyExists) &&
                podAlreadyExists)
            {
                return false;
            }

            if (TryGetBooleanMetadataValue(
                    metadata,
                    "kubernetes.service.alreadyExists",
                    out var serviceAlreadyExists) &&
                serviceAlreadyExists)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Deletes failed resources only when they were created by the current invocation.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <param name="podSpec">The Kubernetes runtime pod specification.</param>
        /// <param name="ownsRuntimeHostResources">Whether this invocation owns the pod and Service.</param>
        /// <param name="failureReason">The failure that triggered cleanup.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The asynchronous operation.</returns>
        private async Task DeleteOnFailureIfOwnedAsync(
            string runtimeInstanceId,
            AiKubernetesRuntimePodSpec podSpec,
            bool ownsRuntimeHostResources,
            string failureReason,
            CancellationToken cancellationToken)
        {
            if (!ownsRuntimeHostResources)
            {
                this.logger.LogWarning(
                    "KUBERNETES HOST DELETE ON FAILURE SKIPPED RuntimeInstanceId={RuntimeInstanceId} PodName={PodName} Namespace={Namespace} FailureReason={FailureReason} Reason=reused-runtime-host-resources HostPreserved=True",
                    runtimeInstanceId,
                    podSpec.PodName,
                    podSpec.Namespace,
                    failureReason);

                return;
            }

            await this.DeleteOnFailureAsync(
                    runtimeInstanceId,
                    podSpec,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Gets a Boolean metadata value using case-insensitive key matching.
        /// </summary>
        /// <param name="metadata">The metadata dictionary.</param>
        /// <param name="key">The metadata key.</param>
        /// <param name="value">The parsed Boolean value.</param>
        /// <returns><see langword="true" /> when a Boolean value is present and valid.</returns>
        private static bool TryGetBooleanMetadataValue(
            IReadOnlyDictionary<string, string> metadata,
            string key,
            out bool value)
        {
            if (TryGetMetadataValue(metadata, key, out var text) &&
                bool.TryParse(text, out value))
            {
                return true;
            }

            value = false;
            return false;
        }

        /// <summary>
        /// Deletes Kubernetes resources after a failed host creation flow when configured to do so.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <param name="podSpec">The Kubernetes runtime pod specification.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The asynchronous operation.</returns>
        private async Task DeleteOnFailureAsync(
            string runtimeInstanceId,
            AiKubernetesRuntimePodSpec podSpec,
            CancellationToken cancellationToken)
        {
            if (!this.options.DeleteResourcesOnFailure)
            {
                return;
            }

            /*
             * A Gateway route can already exist when transport resolution or runtime
             * readiness fails. Remove it before deleting the runtime Service so the
             * shared Gateway never retains a dangling backend reference.
             */
            await this.DeleteGatewayRuntimeRouteBestEffortAsync(
                    runtimeInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);

            this.logger.LogInformation(
                "KUBERNETES HOST DELETE ON FAILURE BEGIN RuntimeInstanceId={RuntimeInstanceId} PodName={PodName} Namespace={Namespace}",
                runtimeInstanceId,
                podSpec.PodName,
                podSpec.Namespace);

            var deleteResult =
                await this.client
                    .DeleteRuntimeHostAsync(
                        podSpec,
                        cancellationToken)
                    .ConfigureAwait(false);

            this.logger.LogInformation(
                "KUBERNETES HOST DELETE ON FAILURE RESULT RuntimeInstanceId={RuntimeInstanceId} PodName={PodName} Namespace={Namespace} Success={Success} FailureReason={FailureReason}",
                runtimeInstanceId,
                podSpec.PodName,
                podSpec.Namespace,
                deleteResult.Success,
                deleteResult.FailureReason ?? "(none)");
        }

        /// <summary>
        /// Deletes the runtime-specific Gateway routes without deleting or restarting
        /// the shared Gateway infrastructure.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The asynchronous operation.</returns>
        private async Task DeleteGatewayRuntimeRouteBestEffortAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            if (!this.options.UseGatewayTransportEndpoint)
            {
                return;
            }

            if (this.gatewayManager is null)
            {
                this.logger.LogWarning(
                    "KUBERNETES RUNTIME ROUTE DELETE SKIPPED RuntimeInstanceId={RuntimeInstanceId} Reason=gateway-manager-not-registered GatewayPreserved=True",
                    runtimeInstanceId);

                return;
            }

            try
            {
                await this.gatewayManager
                    .DeleteRuntimeRouteAsync(
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                /*
                 * Route cleanup must not hide a successful pod/service deletion or
                 * prevent unpublication. The dangling route is observable and can be
                 * retried independently because deletion is idempotent.
                 */
                this.logger.LogWarning(
                    exception,
                    "KUBERNETES RUNTIME ROUTE DELETE FAILED RuntimeInstanceId={RuntimeInstanceId} Reason={Reason} GatewayPreserved=True",
                    runtimeInstanceId,
                    exception.Message);
            }
        }

        /// <summary>
        /// Resolves the transport endpoint that should be published for a Kubernetes-backed runtime.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <param name="metadata">The Kubernetes host metadata.</param>
        /// <returns>The resolved transport endpoint.</returns>
        private static string? ResolveKubernetesTransportEndpoint(
            AiRuntimeHostStartRequest request,
            IReadOnlyDictionary<string, string> metadata)
        {
            if (TryGetMetadataValue(metadata, AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint, out var transportEndpoint))
            {
                return transportEndpoint;
            }

            if (TryGetMetadataValue(metadata, "transport.endpoint", out transportEndpoint))
            {
                return transportEndpoint;
            }

            if (TryGetMetadataValue(metadata, "transportEndpoint", out transportEndpoint))
            {
                return transportEndpoint;
            }

            if (TryGetMetadataValue(metadata, "kubernetes.portForward.endpoint", out transportEndpoint))
            {
                return transportEndpoint;
            }

            if (TryGetMetadataValue(metadata, "kubernetes.nodePort.endpoint", out transportEndpoint))
            {
                return transportEndpoint;
            }

            if (TryGetMetadataValue(metadata, "kubernetes.service.endpoint", out transportEndpoint))
            {
                return transportEndpoint;
            }

            if (TryGetMetadataValue(metadata, "kubernetes.service.url", out transportEndpoint))
            {
                return transportEndpoint;
            }

            return request.TransportEndpoint;
        }

        /// <summary>
        /// Creates transport endpoint metadata using all known aliases.
        /// </summary>
        /// <param name="transportEndpoint">The transport endpoint.</param>
        /// <returns>The transport endpoint metadata.</returns>
        private static IReadOnlyDictionary<string, string> CreateTransportEndpointMetadata(
            string? transportEndpoint)
        {
            if (string.IsNullOrWhiteSpace(transportEndpoint))
            {
                return new Dictionary<string, string>();
            }

            return new Dictionary<string, string>
            {
                [AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] = transportEndpoint,
                ["transport.endpoint"] = transportEndpoint,
                ["transportEndpoint"] = transportEndpoint
            };
        }

        /// <summary>
        /// Gets a metadata value using case-insensitive matching.
        /// </summary>
        /// <param name="metadata">The metadata dictionary.</param>
        /// <param name="key">The metadata key.</param>
        /// <param name="value">The resolved value.</param>
        /// <returns><see langword="true"/> when the value exists.</returns>
        private static bool TryGetMetadataValue(
            IReadOnlyDictionary<string, string> metadata,
            string key,
            out string value)
        {
            if (metadata.TryGetValue(key, out var directValue) && !string.IsNullOrWhiteSpace(directValue))
            {
                value = directValue;
                return true;
            }

            foreach (var item in metadata)
            {
                if (string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(item.Value))
                {
                    value = item.Value;
                    return true;
                }
            }

            value = string.Empty;
            return false;
        }

        /// <summary>
        /// Creates a rejected runtime host start result.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <param name="failureReason">The structured failure reason.</param>
        /// <param name="retryable">A value indicating whether the failure is retryable.</param>
        /// <param name="metadata">The result metadata.</param>
        /// <returns>The rejected runtime host start result.</returns>
        private static AiRuntimeHostStartResult CreateRejectedResult(
            AiRuntimeHostStartRequest request,
            string failureReason,
            bool retryable,
            IReadOnlyDictionary<string, string> metadata)
        {
            return AiRuntimeHostStartResult.Rejected(
                request.ExecutionContextSnapshot,
                request.RuntimeInstanceId,
                request.ProviderName,
                request.TransportName,
                request.TransportEndpoint,
                failureReason,
                retryable,
                metadata);
        }

        /// <summary>
        /// Creates base Kubernetes host lifecycle metadata.
        /// </summary>
        /// <returns>The base metadata.</returns>
        private static IReadOnlyDictionary<string, string> CreateBaseMetadata()
        {
            return new Dictionary<string, string>
            {
                [AiRuntimeHostMetadataKeys.HostProvider] = AiRuntimeHostProviderNames.Kubernetes,
                [AiRuntimeHostMetadataKeys.HostCreationMode] = AiRuntimeHostCreationMode.Kubernetes.ToString(),
                [AiRuntimeHostMetadataKeys.HostCreationStrategy] = nameof(KubernetesAiRuntimeHostCreationStrategy)
            };
        }

        /// <summary>
        /// Merges metadata dictionaries using case-insensitive keys.
        /// </summary>
        /// <param name="first">The first metadata dictionary.</param>
        /// <param name="second">The second metadata dictionary.</param>
        /// <returns>The merged metadata dictionary.</returns>
        private static IReadOnlyDictionary<string, string> MergeMetadata(
            IReadOnlyDictionary<string, string>? first,
            IReadOnlyDictionary<string, string>? second)
        {
            var metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var item in CreateBaseMetadata())
            {
                metadata[item.Key] = item.Value;
            }

            if (first is not null)
            {
                foreach (var item in first)
                {
                    metadata[item.Key] = item.Value;
                }
            }

            if (second is not null)
            {
                foreach (var item in second)
                {
                    metadata[item.Key] = item.Value;
                }
            }

            return metadata;
        }

        /// <summary>
        /// Represents a registered local kubectl port-forward process.
        /// </summary>
        private sealed class KubernetesPortForwardRegistration
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="KubernetesPortForwardRegistration"/> class.
            /// </summary>
            public KubernetesPortForwardRegistration(
                DiagnosticsProcess process,
                int processId,
                string podName,
                string serviceName,
                string @namespace,
                int localPort,
                int remotePort,
                string endpoint)
            {
                Process = process ?? throw new ArgumentNullException(nameof(process));
                ProcessId = processId;
                PodName = podName ?? throw new ArgumentNullException(nameof(podName));
                ServiceName = serviceName ?? throw new ArgumentNullException(nameof(serviceName));
                Namespace = @namespace ?? throw new ArgumentNullException(nameof(@namespace));
                LocalPort = localPort;
                RemotePort = remotePort;
                Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            }

            /// <summary>
            /// Gets the kubectl process.
            /// </summary>
            public DiagnosticsProcess Process { get; }

            /// <summary>
            /// Gets the kubectl process id captured at startup.
            /// </summary>
            public int ProcessId { get; }

            /// <summary>
            /// Gets the Kubernetes pod name.
            /// </summary>
            public string PodName { get; }

            /// <summary>
            /// Gets the Kubernetes service name.
            /// </summary>
            public string ServiceName { get; }

            /// <summary>
            /// Gets the Kubernetes namespace.
            /// </summary>
            public string Namespace { get; }

            /// <summary>
            /// Gets the local forwarded port.
            /// </summary>
            public int LocalPort { get; }

            /// <summary>
            /// Gets the pod container port.
            /// </summary>
            public int RemotePort { get; }

            /// <summary>
            /// Gets the local transport endpoint.
            /// </summary>
            public string Endpoint { get; }
        }

        /// <summary>
        /// Represents a local Kubernetes port-forward endpoint.
        /// </summary>
        private sealed class KubernetesPortForwardEndpoint
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="KubernetesPortForwardEndpoint"/> class.
            /// </summary>
            /// <param name="serviceName">The Kubernetes service name, when available.</param>
            /// <param name="localPort">The local port.</param>
            /// <param name="endpoint">The local transport endpoint.</param>
            /// <param name="reusedExisting">Whether an existing live port-forward was reused.</param>
            public KubernetesPortForwardEndpoint(
                string serviceName,
                int localPort,
                string endpoint,
                bool reusedExisting)
            {
                ServiceName = serviceName ?? throw new ArgumentNullException(nameof(serviceName));
                LocalPort = localPort;
                Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
                ReusedExisting = reusedExisting;
            }

            /// <summary>
            /// Gets the Kubernetes service name.
            /// </summary>
            public string ServiceName { get; }

            /// <summary>
            /// Gets the local port.
            /// </summary>
            public int LocalPort { get; }

            /// <summary>
            /// Gets the local endpoint.
            /// </summary>
            public string Endpoint { get; }

            /// <summary>
            /// Gets a value indicating whether an existing live port-forward was reused.
            /// </summary>
            public bool ReusedExisting { get; }
        }
    }
}