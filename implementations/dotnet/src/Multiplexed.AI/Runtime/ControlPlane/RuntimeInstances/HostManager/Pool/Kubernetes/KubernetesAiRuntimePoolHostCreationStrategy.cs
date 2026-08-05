using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway.Transport;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes
{
    /// <summary>
    /// Creates one opt-in Kubernetes Runtime Pool Pod for a provider scale-out request.
    /// </summary>
    /// <remarks>
    /// This strategy owns Kubernetes resource lifecycle only. The in-Pod Process Pool Manager,
    /// child registration, stable HTTP/gRPC routing, and runtime-level readiness are completed
    /// by the following milestone packages.
    /// </remarks>
    public sealed class KubernetesAiRuntimePoolHostCreationStrategy :
        IAiRuntimeHostCreationStrategy
    {
        private readonly AiKubernetesRuntimePoolOptions poolOptions;
        private readonly AiKubernetesRuntimePoolHostOptions hostOptions;
        private readonly AiKubernetesRuntimePoolPodSpecBuilder podSpecBuilder;
        private readonly IAiKubernetesRuntimePoolHostClient client;
        private readonly AiKubernetesRuntimePoolInPodCommandLineFactory commandLineFactory;
        private readonly IAiRuntimeInstanceCapacityStore? capacityStore;
        private readonly IAiKubernetesRuntimeGatewayManager? gatewayManager;
        private readonly IAiKubernetesGatewayTransportEndpointManager? gatewayTransportEndpointManager;
        private readonly ILogger<KubernetesAiRuntimePoolHostCreationStrategy> logger;

        /// <summary>
        /// Initializes a new Kubernetes Runtime Pool host creation strategy.
        /// </summary>
        public KubernetesAiRuntimePoolHostCreationStrategy(
            IOptions<AiKubernetesRuntimePoolOptions> poolOptions,
            IOptions<AiKubernetesRuntimePoolHostOptions> hostOptions,
            AiKubernetesRuntimePoolPodSpecBuilder podSpecBuilder,
            IAiKubernetesRuntimePoolHostClient client,
            AiKubernetesRuntimePoolInPodCommandLineFactory commandLineFactory,
            ILogger<KubernetesAiRuntimePoolHostCreationStrategy> logger,
            IAiRuntimeInstanceCapacityStore? capacityStore = null,
            IAiKubernetesRuntimeGatewayManager? gatewayManager = null,
            IAiKubernetesGatewayTransportEndpointManager? gatewayTransportEndpointManager = null)
        {
            this.poolOptions =
                poolOptions?.Value
                ?? throw new ArgumentNullException(nameof(poolOptions));

            this.hostOptions =
                hostOptions?.Value
                ?? throw new ArgumentNullException(nameof(hostOptions));

            this.podSpecBuilder =
                podSpecBuilder
                ?? throw new ArgumentNullException(nameof(podSpecBuilder));

            this.client =
                client
                ?? throw new ArgumentNullException(nameof(client));

            this.commandLineFactory =
                commandLineFactory
                ?? throw new ArgumentNullException(nameof(commandLineFactory));

            this.capacityStore =
                capacityStore;

            this.gatewayManager =
                gatewayManager;

            this.gatewayTransportEndpointManager =
                gatewayTransportEndpointManager;

            this.logger =
                logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public AiRuntimeHostCreationMode Mode =>
            AiRuntimeHostCreationMode.KubernetesPool;

        /// <inheritdoc />
        public async Task<AiRuntimeHostStartResult> StartAsync(
            AiRuntimeHostStartRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var validationFailure = this.ValidateRequest(request);
            if (!string.IsNullOrWhiteSpace(validationFailure))
            {
                return CreateRejected(
                    request,
                    validationFailure,
                    retryable: false);
            }

            AiKubernetesRuntimePoolPodSpec podSpec;
            try
            {
                var podRequestId =
                    CreatePodRequestId(request);

                var plan =
                    AiKubernetesRuntimePoolPodPlanFactory.Create(
                        this.poolOptions,
                        podRequestId,
                        request.RuntimeInstanceId);

                var basePodSpec =
                    this.podSpecBuilder.Build(plan);

                podSpec =
                    basePodSpec with
                    {
                        ContainerArguments =
                            this.commandLineFactory.Create(
                                basePodSpec,
                                request)
                    };
            }
            catch (Exception exception)
            {
                this.logger.LogWarning(
                    exception,
                    "KUBERNETES RUNTIME POOL SPEC BUILD FAILED RuntimeInstanceId={RuntimeInstanceId} PoolId={PoolId} Reason={Reason}",
                    request.RuntimeInstanceId,
                    request.PoolId,
                    exception.Message);

                return CreateRejected(
                    request,
                    string.Concat(
                        "kubernetes-runtime-pool-spec-build-failed:",
                        exception.Message),
                    retryable: false);
            }

            var createResult =
                await this.client
                    .CreateRuntimePoolHostAsync(
                        podSpec,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!createResult.Success)
            {
                return CreateRejected(
                    request,
                    createResult.FailureReason
                    ?? "kubernetes-runtime-pool-create-failed",
                    createResult.Retryable,
                    createResult.Metadata);
            }

            var readinessResult =
                await this.client
                    .WaitUntilHostReadyAsync(
                        podSpec,
                        cancellationToken)
                    .ConfigureAwait(false);

            var metadata =
                MergeMetadata(
                    createResult.Metadata,
                    readinessResult.Metadata);

            if (!readinessResult.Success)
            {
                if (this.hostOptions.DeleteResourcesOnFailure)
                {
                    await this.client
                        .DeleteRuntimePoolHostAsync(
                            podSpec,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                return CreateRejected(
                    request,
                    readinessResult.FailureReason
                    ?? "kubernetes-runtime-pool-readiness-failed",
                    readinessResult.Retryable,
                    metadata);
            }

            if (!metadata.TryGetValue(
                    AiRuntimeHostMetadataKeys.HostId,
                    out var hostId)
                || string.IsNullOrWhiteSpace(hostId))
            {
                if (this.hostOptions.DeleteResourcesOnFailure)
                {
                    await this.client
                        .DeleteRuntimePoolHostAsync(
                            podSpec,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                return CreateRejected(
                    request,
                    "kubernetes-runtime-pool-pod-uid-missing",
                    retryable: true,
                    metadata);
            }

            metadata =
                MergeMetadata(
                    metadata,
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        [AiRuntimeHostMetadataKeys.HostProvider] =
                            "kubernetes",
                        [AiRuntimeHostMetadataKeys.HostCreationMode] =
                            AiRuntimeHostCreationMode
                                .KubernetesPool
                                .ToString(),
                        [AiRuntimeHostMetadataKeys.HostCreationStrategy] =
                            nameof(
                                KubernetesAiRuntimePoolHostCreationStrategy),
                        [AiRuntimeHostMetadataKeys.HostId] = hostId,
                        [AiRuntimeHostMetadataKeys.HostName] =
                            podSpec.PodName,
                        ["runtime.pool.id"] = podSpec.PoolId,
                        ["runtime.pool.primaryRuntimeInstanceId"] =
                            request.RuntimeInstanceId
                    });

            string? transportEndpoint = null;
            IReadOnlyDictionary<
                string,
                IReadOnlyDictionary<string, string>>?
                runtimeTransportMetadata = null;
            var transportEndpointSource =
                "kubernetes-pool-service";
            var runtimeServiceName =
                createResult.ServiceName
                ?? readinessResult.ServiceName;

            try
            {
                if (this.hostOptions.UseGatewayTransportEndpoint)
                {
                    var gatewayProjection =
                        await this.ResolveGatewayTransportProjectionAsync(
                                request,
                                podSpec,
                                runtimeServiceName,
                                cancellationToken)
                            .ConfigureAwait(false);

                    transportEndpoint =
                        gatewayProjection.TransportEndpoint;
                    runtimeTransportMetadata =
                        gatewayProjection.RuntimeMetadata;
                    transportEndpointSource =
                        "kubernetes-pool-gateway";
                    metadata =
                        MergeMetadata(
                            metadata,
                            gatewayProjection.SharedMetadata);

                    if (gatewayProjection.RuntimeMetadata.TryGetValue(
                            request.RuntimeInstanceId,
                            out var primaryRuntimeTransportMetadata))
                    {
                        metadata =
                            MergeMetadata(
                                metadata,
                                primaryRuntimeTransportMetadata);
                    }
                }
                else
                {
                    metadata.TryGetValue(
                        "transport.endpoint",
                        out transportEndpoint);
                }

                if (string.IsNullOrWhiteSpace(transportEndpoint))
                {
                    throw new InvalidOperationException(
                        "kubernetes-runtime-pool-stable-transport-endpoint-missing");
                }

                await this.ProjectStableTransportEndpointAsync(
                        podSpec,
                        hostId,
                        transportEndpoint,
                        metadata,
                        runtimeTransportMetadata,
                        transportEndpointSource,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                this.logger.LogWarning(
                    exception,
                    "KUBERNETES RUNTIME POOL TRANSPORT PROJECTION FAILED RuntimeInstanceId={RuntimeInstanceId} PoolId={PoolId} HostId={HostId} TransportEndpoint={TransportEndpoint} Reason={Reason}",
                    request.RuntimeInstanceId,
                    podSpec.PoolId,
                    hostId,
                    transportEndpoint ?? "(none)",
                    exception.Message);

                if (this.hostOptions.DeleteResourcesOnFailure)
                {
                    await this.client
                        .DeleteRuntimePoolHostAsync(
                            podSpec,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                return CreateRejected(
                    request,
                    string.Concat(
                        "kubernetes-runtime-pool-transport-projection-failed:",
                        exception.Message),
                    retryable: true,
                    metadata);
            }

            return AiRuntimeHostStartResult.Started(
                request.ExecutionContextSnapshot,
                request.RuntimeInstanceId,
                request.ProviderName,
                request.TransportName,
                transportEndpoint!,
                metadata);
        }

        /// <summary>
        /// Ensures one exact Gateway route per child while exposing the shared Gateway endpoint
        /// to the external control plane.
        /// </summary>
        private async Task<GatewayTransportProjection>
            ResolveGatewayTransportProjectionAsync(
                AiRuntimeHostStartRequest request,
                AiKubernetesRuntimePoolPodSpec podSpec,
                string? runtimeServiceName,
                CancellationToken cancellationToken)
        {
            var gatewayManager =
                this.gatewayManager
                ?? throw new InvalidOperationException(
                    "kubernetes-runtime-pool-gateway-manager-unavailable");

            var gatewayTransportEndpointManager =
                this.gatewayTransportEndpointManager
                ?? throw new InvalidOperationException(
                    "kubernetes-runtime-pool-gateway-transport-manager-unavailable");

            if (string.IsNullOrWhiteSpace(runtimeServiceName))
            {
                throw new InvalidOperationException(
                    "kubernetes-runtime-pool-gateway-service-name-missing");
            }

            var gatewayEndpoint =
                await gatewayManager
                    .EnsureGatewayAsync(
                        request.ControlPlaneId,
                        cancellationToken)
                    .ConfigureAwait(false);

            var runtimeMetadata =
                new Dictionary<
                    string,
                    IReadOnlyDictionary<string, string>>(
                    StringComparer.Ordinal);

            foreach (var runtime in podSpec.Bootstrap.RuntimeInstances)
            {
                AiKubernetesRuntimeRouteResult routeResult;

                if (IsGrpcRuntimeTransport(request))
                {
                    routeResult =
                        await gatewayManager
                            .EnsureGrpcRouteAsync(
                                request.ControlPlaneId,
                                runtime.RuntimeInstanceId,
                                runtimeServiceName,
                                podSpec.Bootstrap.StableTransportPort,
                                cancellationToken)
                            .ConfigureAwait(false);
                }
                else if (IsHttpRuntimeTransport(request))
                {
                    routeResult =
                        await gatewayManager
                            .EnsureHttpRouteAsync(
                                request.ControlPlaneId,
                                runtime.RuntimeInstanceId,
                                runtimeServiceName,
                                podSpec.Bootstrap.StableTransportPort,
                                cancellationToken)
                            .ConfigureAwait(false);
                }
                else
                {
                    throw new InvalidOperationException(
                        string.Concat(
                            "kubernetes-runtime-pool-gateway-transport-unsupported:",
                            request.ProviderName,
                            ":",
                            request.TransportName));
                }

                runtimeMetadata[runtime.RuntimeInstanceId] =
                    CreateGatewayRouteMetadata(routeResult);
            }

            var transportEndpoint =
                await gatewayTransportEndpointManager
                    .ResolveAsync(
                        gatewayEndpoint,
                        cancellationToken)
                    .ConfigureAwait(false);

            this.logger.LogInformation(
                "KUBERNETES RUNTIME POOL GATEWAY TRANSPORT RESOLVED PoolId={PoolId} HostName={HostName} RuntimeServiceName={RuntimeServiceName} RuntimeServicePort={RuntimeServicePort} GatewayName={GatewayName} GatewayServiceName={GatewayServiceName} TransportEndpoint={TransportEndpoint} InternalEndpoint={InternalEndpoint} UsesPortForward={UsesPortForward} LocalPort={LocalPort} RuntimeRouteCount={RuntimeRouteCount}",
                podSpec.PoolId,
                podSpec.PodName,
                runtimeServiceName,
                podSpec.Bootstrap.StableTransportPort,
                gatewayEndpoint.GatewayName,
                gatewayEndpoint.ServiceName,
                transportEndpoint.Endpoint,
                transportEndpoint.InternalEndpoint,
                transportEndpoint.UsesPortForward,
                transportEndpoint.LocalPort,
                runtimeMetadata.Count);

            return new GatewayTransportProjection(
                transportEndpoint.Endpoint,
                CreateGatewaySharedMetadata(
                    gatewayEndpoint,
                    transportEndpoint,
                    runtimeServiceName,
                    podSpec.Bootstrap.StableTransportPort),
                runtimeMetadata);
        }

        /// <summary>
        /// Determines whether the runtime provider uses gRPC command transport.
        /// </summary>
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
        /// Determines whether the runtime provider uses HTTP command transport.
        /// </summary>
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
        /// Creates Gateway metadata shared by every child descriptor in one Pool Pod.
        /// </summary>
        private static IReadOnlyDictionary<string, string>
            CreateGatewaySharedMetadata(
                AiKubernetesGatewayEndpoint gatewayEndpoint,
                AiKubernetesGatewayTransportEndpoint transportEndpoint,
                string runtimeServiceName,
                int runtimeServicePort)
        {
            var metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] =
                        transportEndpoint.Endpoint,
                    ["transport.endpoint"] = transportEndpoint.Endpoint,
                    ["transportEndpoint"] = transportEndpoint.Endpoint,
                    ["grpc.endpoint"] = transportEndpoint.Endpoint,
                    ["transport.endpoint.source"] =
                        "kubernetes-pool-gateway",
                    ["transport.endpoint.scope"] =
                        "control-plane",
                    ["kubernetes.transport.endpoint.source"] =
                        transportEndpoint.UsesPortForward
                            ? "gateway-port-forward"
                            : "gateway-service",
                    ["kubernetes.gateway.name"] =
                        gatewayEndpoint.GatewayName,
                    ["kubernetes.gateway.namespace"] =
                        gatewayEndpoint.Namespace,
                    ["kubernetes.gateway.class.name"] =
                        gatewayEndpoint.GatewayClassName,
                    ["kubernetes.gateway.listener.name"] =
                        gatewayEndpoint.ListenerName,
                    ["kubernetes.gateway.listener.port"] =
                        gatewayEndpoint.ListenerPort.ToString(),
                    ["kubernetes.gateway.service.name"] =
                        gatewayEndpoint.ServiceName,
                    ["kubernetes.gateway.service.namespace"] =
                        gatewayEndpoint.ServiceNamespace,
                    ["kubernetes.gateway.service.port"] =
                        gatewayEndpoint.ServicePort.ToString(),
                    ["kubernetes.gateway.internalEndpoint"] =
                        gatewayEndpoint.InternalEndpoint,
                    ["kubernetes.gateway.transport.endpoint"] =
                        transportEndpoint.Endpoint,
                    ["kubernetes.gateway.transport.internalEndpoint"] =
                        transportEndpoint.InternalEndpoint,
                    ["kubernetes.gateway.transport.usesPortForward"] =
                        transportEndpoint.UsesPortForward.ToString(),
                    ["kubernetes.runtime.service.name"] =
                        runtimeServiceName,
                    ["kubernetes.runtime.service.port"] =
                        runtimeServicePort.ToString()
                };

            if (transportEndpoint.LocalPort is int localPort)
            {
                metadata["kubernetes.gateway.transport.localPort"] =
                    localPort.ToString();
            }

            return metadata;
        }

        /// <summary>
        /// Creates metadata unique to one exact child Gateway route.
        /// </summary>
        private static IReadOnlyDictionary<string, string>
            CreateGatewayRouteMetadata(
                AiKubernetesRuntimeRouteResult routeResult)
        {
            return new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["kubernetes.gateway.route.name"] =
                    routeResult.RouteName,
                ["kubernetes.gateway.route.kind"] =
                    routeResult.RouteKind.ToString(),
                ["kubernetes.runtime.service.name"] =
                    routeResult.RuntimeServiceName,
                ["kubernetes.runtime.service.port"] =
                    routeResult.BackendPort.ToString(),
                ["gateway.routing.header"] =
                    routeResult.RoutingHeaderName,
                ["gateway.routing.value"] =
                    routeResult.RoutingHeaderValue
            };
        }

        /// <summary>
        /// Projects the stable Pool Service endpoint onto every exact child capacity descriptor.
        /// </summary>
        private async Task ProjectStableTransportEndpointAsync(
            AiKubernetesRuntimePoolPodSpec podSpec,
            string hostId,
            string transportEndpoint,
            IReadOnlyDictionary<string, string> hostMetadata,
            IReadOnlyDictionary<
                string,
                IReadOnlyDictionary<string, string>>?
                runtimeTransportMetadata,
            string transportEndpointSource,
            CancellationToken cancellationToken)
        {
            var capacityStore =
                this.capacityStore
                ?? throw new InvalidOperationException(
                    "kubernetes-runtime-pool-capacity-store-unavailable");

            foreach (var runtime in podSpec.Bootstrap.RuntimeInstances)
            {
                var descriptor =
                    await capacityStore
                        .GetAsync(
                            runtime.RuntimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        string.Concat(
                            "runtime-capacity-descriptor-missing:",
                            runtime.RuntimeInstanceId));

                if (!string.Equals(
                        descriptor.PoolId,
                        podSpec.PoolId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        descriptor.HostId,
                        hostId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        string.Concat(
                            "runtime-capacity-membership-mismatch:",
                            runtime.RuntimeInstanceId));
                }

                var metadata =
                    new Dictionary<string, string>(
                        descriptor.Metadata,
                        StringComparer.OrdinalIgnoreCase);

                if (TryGetTransportEndpoint(
                        descriptor.Metadata,
                        out var internalTransportEndpoint) &&
                    !string.Equals(
                        internalTransportEndpoint,
                        transportEndpoint,
                        StringComparison.OrdinalIgnoreCase))
                {
                    metadata["transport.endpoint.internal"] =
                        internalTransportEndpoint;
                }

                CopyHostMetadata(
                    metadata,
                    hostMetadata);

                if (runtimeTransportMetadata is not null &&
                    runtimeTransportMetadata.TryGetValue(
                        runtime.RuntimeInstanceId,
                        out var exactRuntimeTransportMetadata))
                {
                    CopyMetadata(
                        metadata,
                        exactRuntimeTransportMetadata);
                }

                AddTransportEndpointAliases(
                    metadata,
                    transportEndpoint);

                metadata["transport.endpoint.source"] =
                    transportEndpointSource;
                metadata["transport.endpoint.scope"] =
                    "control-plane";
                metadata["host.provider"] =
                    "kubernetes";
                metadata["host.creation.mode"] =
                    AiRuntimeHostCreationMode.KubernetesPool.ToString();
                metadata["host.creation.strategy"] =
                    nameof(KubernetesAiRuntimePoolHostCreationStrategy);
                metadata["host.id"] = hostId;
                metadata["host.name"] = podSpec.PodName;
                metadata["hostType"] =
                    "runtime-instance-kubernetes-pool";
                metadata["deployment"] =
                    "kubernetes-pool";

                await capacityStore
                    .PublishAsync(
                        CopyDescriptor(
                            descriptor,
                            metadata),
                        cancellationToken)
                    .ConfigureAwait(false);

                this.logger.LogInformation(
                    "KUBERNETES RUNTIME POOL TRANSPORT PROJECTED RuntimeInstanceId={RuntimeInstanceId} PoolId={PoolId} HostId={HostId} TransportEndpoint={TransportEndpoint} InternalTransportEndpoint={InternalTransportEndpoint}",
                    runtime.RuntimeInstanceId,
                    podSpec.PoolId,
                    hostId,
                    transportEndpoint,
                    metadata.TryGetValue(
                        "transport.endpoint.internal",
                        out var projectedInternalEndpoint)
                        ? projectedInternalEndpoint
                        : "(none)");
            }

            await this.WaitUntilProjectedCapacityReadyAsync(
                    podSpec,
                    hostId,
                    transportEndpoint,
                    capacityStore,
                    runtimeTransportMetadata,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Waits until every child heartbeat has preserved the projected endpoint and confirmed
        /// transport-ready membership before the scale-out request is fulfilled. A ready runtime
        /// may already be busy, so transient dispatch availability is not a startup requirement.
        /// </summary>
        private async Task WaitUntilProjectedCapacityReadyAsync(
            AiKubernetesRuntimePoolPodSpec podSpec,
            string hostId,
            string transportEndpoint,
            IAiRuntimeInstanceCapacityStore capacityStore,
            IReadOnlyDictionary<
                string,
                IReadOnlyDictionary<string, string>>?
                runtimeTransportMetadata,
            CancellationToken cancellationToken)
        {
            var timeout =
                this.hostOptions.StartupTimeout > TimeSpan.Zero
                    ? this.hostOptions.StartupTimeout
                    : TimeSpan.FromSeconds(30);

            var pollInterval =
                this.hostOptions.ReadinessPollInterval > TimeSpan.Zero
                    ? this.hostOptions.ReadinessPollInterval
                    : TimeSpan.FromMilliseconds(100);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var allRuntimeInstancesReady = true;

                foreach (var runtime in podSpec.Bootstrap.RuntimeInstances)
                {
                    var descriptor =
                        await capacityStore
                            .GetAsync(
                                runtime.RuntimeInstanceId,
                                cancellationToken)
                            .ConfigureAwait(false);

                    IReadOnlyDictionary<string, string>?
                        expectedRuntimeTransportMetadata = null;

                    if (runtimeTransportMetadata is not null)
                    {
                        runtimeTransportMetadata.TryGetValue(
                            runtime.RuntimeInstanceId,
                            out expectedRuntimeTransportMetadata);
                    }

                    if (descriptor is null ||
                        !string.Equals(
                            descriptor.PoolId,
                            podSpec.PoolId,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            descriptor.HostId,
                            hostId,
                            StringComparison.Ordinal) ||
                        descriptor.Status != AiRuntimeInstanceStatus.Ready ||
                        !TryGetTransportEndpoint(
                            descriptor.Metadata,
                            out var projectedTransportEndpoint) ||
                        !string.Equals(
                            projectedTransportEndpoint,
                            transportEndpoint,
                            StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(
                            GetMetadataValue(
                                descriptor.Metadata,
                                "transport.endpoint.scope"),
                            "control-plane",
                            StringComparison.OrdinalIgnoreCase) ||
                        !ContainsExpectedMetadata(
                            descriptor.Metadata,
                            expectedRuntimeTransportMetadata))
                    {
                        allRuntimeInstancesReady = false;
                        break;
                    }
                }

                if (allRuntimeInstancesReady)
                {
                    this.logger.LogInformation(
                        "KUBERNETES RUNTIME POOL TRANSPORT CAPACITY READY PoolId={PoolId} HostId={HostId} TransportEndpoint={TransportEndpoint} RuntimeInstanceCount={RuntimeInstanceCount}",
                        podSpec.PoolId,
                        hostId,
                        transportEndpoint,
                        podSpec.Bootstrap.RuntimeInstances.Count);

                    return;
                }

                await Task
                    .Delay(
                        pollInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                "kubernetes-runtime-pool-transport-capacity-readiness-timeout");
        }

        /// <summary>
        /// Verifies that exact runtime routing metadata survives the child heartbeat.
        /// </summary>
        private static bool ContainsExpectedMetadata(
            IReadOnlyDictionary<string, string> actual,
            IReadOnlyDictionary<string, string>? expected)
        {
            if (expected is null)
            {
                return true;
            }

            foreach (var pair in expected)
            {
                if (!string.Equals(
                        GetMetadataValue(
                            actual,
                            pair.Key),
                        pair.Value,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Copies Kubernetes host diagnostics without allowing them to alter typed membership.
        /// </summary>
        private static void CopyHostMetadata(
            IDictionary<string, string> destination,
            IReadOnlyDictionary<string, string> source)
        {
            foreach (var pair in source)
            {
                if (pair.Key.StartsWith(
                        "kubernetes.",
                        StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.StartsWith(
                        "runtime.pool.",
                        StringComparison.OrdinalIgnoreCase))
                {
                    destination[pair.Key] = pair.Value;
                }
            }
        }

        /// <summary>
        /// Copies exact runtime transport metadata without changing first-class membership.
        /// </summary>
        private static void CopyMetadata(
            IDictionary<string, string> destination,
            IReadOnlyDictionary<string, string> source)
        {
            foreach (var pair in source)
            {
                destination[pair.Key] = pair.Value;
            }
        }

        /// <summary>
        /// Gets the child-published transport endpoint from known aliases.
        /// </summary>
        private static bool TryGetTransportEndpoint(
            IReadOnlyDictionary<string, string> metadata,
            out string transportEndpoint)
        {
            transportEndpoint =
                metadata.TryGetValue(
                    "transport.endpoint",
                    out var canonicalEndpoint)
                    ? canonicalEndpoint
                    : metadata.TryGetValue(
                        "transportEndpoint",
                        out var transportEndpointAlias)
                        ? transportEndpointAlias
                        : metadata.TryGetValue(
                            AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint,
                            out var commandEndpoint)
                            ? commandEndpoint
                            : metadata.TryGetValue(
                                "grpc.endpoint",
                                out var grpcEndpoint)
                                ? grpcEndpoint
                                : string.Empty;

            return !string.IsNullOrWhiteSpace(transportEndpoint);
        }

        /// <summary>
        /// Gets one metadata value without changing routing authority.
        /// </summary>
        private static string? GetMetadataValue(
            IReadOnlyDictionary<string, string> metadata,
            string key)
        {
            if (metadata.TryGetValue(
                    key,
                    out var value))
            {
                return value;
            }

            foreach (var pair in metadata)
            {
                if (string.Equals(
                        pair.Key,
                        key,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }

            return null;
        }

        /// <summary>
        /// Adds every transport endpoint alias consumed by runtime providers.
        /// </summary>
        private static void AddTransportEndpointAliases(
            IDictionary<string, string> metadata,
            string transportEndpoint)
        {
            metadata["transport.endpoint"] = transportEndpoint;
            metadata["transportEndpoint"] = transportEndpoint;
            metadata[
                AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] =
                transportEndpoint;
            metadata["grpc.endpoint"] = transportEndpoint;
        }

        /// <summary>
        /// Copies one descriptor while changing only its published metadata.
        /// </summary>
        private static AiRuntimeInstanceCapacityDescriptor CopyDescriptor(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            IReadOnlyDictionary<string, string> metadata)
        {
            return new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = descriptor.RuntimeInstanceId,
                PoolId = descriptor.PoolId,
                HostId = descriptor.HostId,
                TenantId = descriptor.TenantId,
                TenantGroupId = descriptor.TenantGroupId,
                ProviderName = descriptor.ProviderName,
                IsolationMode = descriptor.IsolationMode,
                AllowSharedFallback = descriptor.AllowSharedFallback,
                PreferDedicatedCapacity = descriptor.PreferDedicatedCapacity,
                Role = descriptor.Role,
                Status = descriptor.Status,
                WorkerCount = descriptor.WorkerCount,
                ActiveWorkerCount = descriptor.ActiveWorkerCount,
                AvailableWorkerCount = descriptor.AvailableWorkerCount,
                MaxWorkersPerRun = descriptor.MaxWorkersPerRun,
                MinWorkersRequiredPerRun = descriptor.MinWorkersRequiredPerRun,
                QueuedRunCount = descriptor.QueuedRunCount,
                RunningRunCount = descriptor.RunningRunCount,
                ActiveRunCount = descriptor.ActiveRunCount,
                MaxConcurrentRuns = descriptor.MaxConcurrentRuns,
                MaxRunSlots = descriptor.MaxRunSlots,
                AvailableRunSlots = descriptor.AvailableRunSlots,
                ReservedRunSlots = descriptor.ReservedRunSlots,
                EffectiveAvailableRunSlots = descriptor.EffectiveAvailableRunSlots,
                IsQueuePaused = descriptor.IsQueuePaused,
                CanAcceptRun = descriptor.CanAcceptRun,
                LastHeartbeatAtUtc = descriptor.LastHeartbeatAtUtc,
                Metadata = metadata,
                ControlPlaneHostId = descriptor.ControlPlaneHostId,
                ControlPlaneId = descriptor.ControlPlaneId
            };
        }

        /// <summary>
        /// Validates first-class request authority before resource creation.
        /// </summary>
        private string? ValidateRequest(
            AiRuntimeHostStartRequest request)
        {
            if (!this.poolOptions.Enabled)
            {
                return "kubernetes-runtime-pool-disabled";
            }

            if (this.capacityStore is null)
            {
                return "kubernetes-runtime-pool-capacity-store-unavailable";
            }

            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                return "kubernetes-runtime-pool-request-id-missing";
            }

            if (string.IsNullOrWhiteSpace(request.PoolId))
            {
                return "kubernetes-runtime-pool-id-missing";
            }

            if (!string.Equals(
                    request.PoolId,
                    this.poolOptions.PoolId,
                    StringComparison.Ordinal))
            {
                return "kubernetes-runtime-pool-id-mismatch";
            }

            if (string.IsNullOrWhiteSpace(request.RuntimeInstanceId))
            {
                return "kubernetes-runtime-pool-primary-runtime-id-missing";
            }

            if (!string.Equals(
                    request.ProviderName,
                    this.poolOptions.ProviderName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "kubernetes-runtime-pool-provider-mismatch";
            }

            if (!string.Equals(
                    request.TransportName,
                    this.poolOptions.TransportName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "kubernetes-runtime-pool-transport-mismatch";
            }

            if (this.hostOptions.UseGatewayTransportEndpoint)
            {
                if (!this.hostOptions.CreateService)
                {
                    return "kubernetes-runtime-pool-gateway-service-disabled";
                }

                if (string.IsNullOrWhiteSpace(request.ControlPlaneId))
                {
                    return "kubernetes-runtime-pool-gateway-control-plane-id-missing";
                }

                if (this.gatewayManager is null)
                {
                    return "kubernetes-runtime-pool-gateway-manager-unavailable";
                }

                if (this.gatewayTransportEndpointManager is null)
                {
                    return "kubernetes-runtime-pool-gateway-transport-manager-unavailable";
                }

                if (!IsGrpcRuntimeTransport(request) &&
                    !IsHttpRuntimeTransport(request))
                {
                    return "kubernetes-runtime-pool-gateway-transport-unsupported";
                }
            }

            return null;
        }

        /// <summary>
        /// Creates a stable DNS-safe Pod request identity from first-class request fields.
        /// </summary>
        private static string CreatePodRequestId(
            AiRuntimeHostStartRequest request)
        {
            var source =
                string.Concat(
                    request.RequestId,
                    "|",
                    request.PoolId,
                    "|",
                    request.RuntimeInstanceId);

            return Convert
                .ToHexString(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes(source)))
                .ToLowerInvariant()[..24];
        }

        /// <summary>
        /// Creates a rejected strategy result.
        /// </summary>
        private static AiRuntimeHostStartResult CreateRejected(
            AiRuntimeHostStartRequest request,
            string failureReason,
            bool retryable,
            IReadOnlyDictionary<string, string>? metadata = null)
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
        /// Merges diagnostic metadata without changing first-class request fields.
        /// </summary>
        private static Dictionary<string, string> MergeMetadata(
            IReadOnlyDictionary<string, string>? first,
            IReadOnlyDictionary<string, string>? second)
        {
            var result =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            if (first is not null)
            {
                foreach (var pair in first)
                {
                    result[pair.Key] = pair.Value;
                }
            }

            if (second is not null)
            {
                foreach (var pair in second)
                {
                    result[pair.Key] = pair.Value;
                }
            }

            return result;
        }

        /// <summary>
        /// Represents one shared Gateway endpoint and the exact route metadata for every child.
        /// </summary>
        private sealed record GatewayTransportProjection(
            string TransportEndpoint,
            IReadOnlyDictionary<string, string> SharedMetadata,
            IReadOnlyDictionary<
                string,
                IReadOnlyDictionary<string, string>> RuntimeMetadata);
    }
}
