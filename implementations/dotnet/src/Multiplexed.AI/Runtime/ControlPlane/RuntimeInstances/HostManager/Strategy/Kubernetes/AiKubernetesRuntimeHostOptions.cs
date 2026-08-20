using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;
using System;
using System.Collections.Generic;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes
{
    /// <summary>
    /// Provides options used by the Kubernetes runtime host creation strategy.
    /// </summary>
    /// <remarks>
    /// These options describe Kubernetes host lifecycle behavior only.
    /// Runtime command dispatch must remain owned by the selected transport provider, such as HTTP or gRPC.
    /// </remarks>
    public sealed class AiKubernetesRuntimeHostOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether Kubernetes runtime host creation is enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets the Kubernetes runtime host client mode.
        /// </summary>
        public AiKubernetesRuntimeHostClientMode ClientMode { get; set; } = AiKubernetesRuntimeHostClientMode.Fake;

        /// <summary>
        /// Gets or sets the Kubernetes namespace where runtime pods are created.
        /// </summary>
        public string Namespace { get; set; } = "ai-runtime";

        /// <summary>
        /// Gets or sets the runtime container image used for RuntimeInstanceOnly pods.
        /// </summary>
        public string RuntimeImage { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the runtime container name.
        /// </summary>
        public string ContainerName { get; set; } = "runtime-instance";

        /// <summary>
        /// Gets or sets the optional Kubernetes service account name used by runtime pods.
        /// </summary>
        public string? ServiceAccountName { get; set; }

        /// <summary>
        /// Gets or sets the prefix used when generating Kubernetes pod names.
        /// </summary>
        public string PodNamePrefix { get; set; } = "ai-runtime";

        /// <summary>
        /// Gets or sets the transport name exposed by the runtime pod.
        /// </summary>
        /// <remarks>
        /// This value should normally be <c>grpc</c> or <c>http</c>.
        /// Kubernetes owns host lifecycle, not command transport.
        /// </remarks>
        public string TransportName { get; set; } = "grpc";

        /// <summary>
        /// Gets or sets the container port exposed by the runtime transport.
        /// </summary>
        public int ContainerPort { get; set; } = 8080;

        /// <summary>
        /// Gets or sets a value indicating whether a dedicated Kubernetes service should be created per runtime instance.
        /// </summary>
        public bool UseServicePerRuntime { get; set; } = true;

        /// <summary>
        /// Gets or sets the timeout used while waiting for the Kubernetes host to start.
        /// </summary>
        public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Gets or sets the timeout used while waiting for the runtime instance to publish registry and capacity state.
        /// </summary>
        public TimeSpan ReadinessTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Gets or sets a value indicating whether Kubernetes resources should be deleted when host creation fails.
        /// </summary>
        public bool DeleteResourcesOnFailure { get; set; } = true;

        /// <summary>
        /// Gets or sets additional Kubernetes labels applied to runtime pods and services.
        /// </summary>
        public Dictionary<string, string> Labels { get; set; } = new();

        /// <summary>
        /// Gets or sets additional Kubernetes annotations applied to runtime pods and services.
        /// </summary>
        public Dictionary<string, string> Annotations { get; set; } = new();

        /// <summary>
        /// Gets or sets the Kubernetes pod readiness polling interval.
        /// </summary>
        /// <remarks>
        /// This controls only Kubernetes host readiness polling.
        /// Runtime transport readiness remains validated separately by the runtime readiness waiter.
        /// </remarks>
        public TimeSpan ReadinessPollInterval { get; set; } =
            TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Gets or sets the Kubernetes container image pull policy.
        /// </summary>
        /// <remarks>
        /// Use <see cref="AiKubernetesImagePullPolicy.IfNotPresent"/> for most local and cached-image scenarios.
        /// Use <see cref="AiKubernetesImagePullPolicy.Always"/> for registry-driven deployments.
        /// Use <see cref="AiKubernetesImagePullPolicy.Never"/> when the image must already exist on the node.
        /// </remarks>
        public AiKubernetesImagePullPolicy ImagePullPolicy { get; set; } =
            AiKubernetesImagePullPolicy.IfNotPresent;

        /// <summary>
        /// Gets or sets a value indicating whether the Kubernetes host strategy should wait for runtime registry readiness.
        /// </summary>
        /// <remarks>
        /// This should be enabled for real Kubernetes pods and disabled for fake Kubernetes lifecycle tests
        /// where no real runtime process registers capacity.
        /// </remarks>
        public bool RequireRuntimeReadiness { get; set; } = true;

        /// <summary>
        /// Gets the environment variables injected into Kubernetes runtime pods.
        /// </summary>
        public IDictionary<string, string> EnvironmentVariables { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets or sets the external host used to reach Kubernetes NodePort services from the control-plane process.
        /// </summary>
        public string? NodePortHost { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether Kubernetes NodePort endpoints should be published as runtime transport endpoints.
        /// </summary>
        public bool PublishNodePortTransportEndpoint { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether runtime transport endpoints should be exposed through
        /// a shared Kubernetes Gateway instead of directly through each runtime service.
        /// </summary>
        /// <remarks>
        /// The default value preserves the current per-runtime endpoint behavior.
        /// </remarks>
        public bool UseGatewayTransportEndpoint { get; set; }

        /// <summary>
        /// Gets or sets the name of the shared Kubernetes Gateway.
        /// </summary>
        public string GatewayName { get; set; } = "ai-runtime-gateway";

        /// <summary>
        /// Gets or sets the Kubernetes GatewayClass name used when the shared Gateway must be created.
        /// </summary>
        /// <remarks>
        /// This value is required when <see cref="UseGatewayTransportEndpoint"/> is enabled.
        /// The GatewayClass and its controller remain cluster-level prerequisites.
        /// </remarks>
        public string GatewayClassName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Gateway API controller name responsible for the configured
        /// <see cref="GatewayClassName"/>.
        /// </summary>
        /// <remarks>
        /// Envoy Gateway uses
        /// <c>gateway.envoyproxy.io/gatewayclass-controller</c> by default.
        /// Existing GatewayClass resources must declare the same controller name.
        /// </remarks>
        public string GatewayControllerName { get; set; } =
            "gateway.envoyproxy.io/gatewayclass-controller";

        /// <summary>
        /// Gets or sets a value indicating whether the configured GatewayClass should
        /// be created dynamically when it is missing.
        /// </summary>
        /// <remarks>
        /// The Gateway API CRDs and a matching controller deployment must already
        /// exist in the cluster. This option only manages the GatewayClass resource.
        /// </remarks>
        public bool CreateGatewayClassWhenMissing { get; set; } = true;

        /// <summary>
        /// Gets or sets the listener name exposed by the shared Kubernetes Gateway.
        /// </summary>
        public string GatewayListenerName { get; set; } = "runtime";

        /// <summary>
        /// Gets or sets the listener port exposed by the shared Kubernetes Gateway.
        /// </summary>
        public int GatewayPort { get; set; } = 8080;

        /// <summary>
        /// Gets or sets the optional Kubernetes Service name backing the shared Gateway.
        /// </summary>
        /// <remarks>
        /// Leave this value empty to discover the controller-managed Service dynamically.
        /// </remarks>
        public string? GatewayServiceName { get; set; }

        /// <summary>
        /// Gets or sets the optional namespace containing the Kubernetes Service
        /// backing the shared Gateway.
        /// </summary>
        /// <remarks>
        /// Leave this value empty to discover the controller-managed Service across
        /// namespaces. Some Gateway controllers create their data-plane Service in
        /// a controller-owned namespace rather than beside the Gateway resource.
        /// </remarks>
        public string? GatewayServiceNamespace { get; set; }

        /// <summary>
        /// Gets or sets the request header used by HTTPRoute and GRPCRoute resources
        /// to select the target runtime instance.
        /// </summary>
        public string GatewayRouteHeaderName { get; set; } =
            AiRuntimeInstanceCommandTransportDefaults.DefaultGatewayRoutingHeaderName;

        /// <summary>
        /// Gets or sets a value indicating whether the shared Kubernetes Gateway should be created when missing.
        /// </summary>
        public bool CreateGatewayWhenMissing { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether Gateway readiness requires the Programmed condition.
        /// </summary>
        public bool RequireGatewayProgrammed { get; set; } = true;

        /// <summary>
        /// Gets or sets the timeout used while waiting for the shared Kubernetes Gateway to become ready.
        /// </summary>
        public TimeSpan GatewayReadinessTimeout { get; set; } =
            TimeSpan.FromSeconds(60);

        /// <summary>
        /// Gets or sets the polling interval used while waiting for Gateway and route readiness.
        /// </summary>
        public TimeSpan GatewayReadinessPollInterval { get; set; } =
            TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Gets or sets a value indicating whether the Kubernetes SDK host manager should publish a local kubectl port-forward endpoint instead of the Kubernetes NodePort endpoint.
        /// </summary>
        public bool UsePortForwardTransportEndpoint { get; set; } = false;

        /// <summary>
        /// Gets or sets the local port used for kubectl port-forward. Use 0 to allocate a free local port.
        /// </summary>
        public int PortForwardLocalPort { get; set; }

        /// <summary>
        /// Gets or sets the kubectl executable path.
        /// </summary>
        public string KubectlPath { get; set; } = "kubectl";

        /// <summary>
        /// Gets or sets the timeout used while waiting for kubectl port-forward to become reachable.
        /// </summary>
        public TimeSpan PortForwardStartupTimeout { get; set; } = TimeSpan.FromSeconds(10);
    }
}