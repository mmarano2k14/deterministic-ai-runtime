namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base
{
    /// <summary>
    /// Provides constants used by Kubernetes SDK production scenarios.
    /// </summary>
    public static class KubernetesSdkScenarioConstants
    {
        /// <summary>
        /// The Kubernetes namespace used by runtime instance pods.
        /// </summary>
        public const string Namespace = "ai-runtime";

        /// <summary>
        /// The current local Kubernetes runtime image used by Minikube-based integration tests.
        /// </summary>
        public const string RuntimeImage = "multiplexed-ai-runtime:k8s-debug-137";

        /// <summary>
        /// The Kubernetes image pull policy used for locally built Minikube images.
        /// </summary>
        public const string ImagePullPolicy = "Never";

        /// <summary>
        /// The runtime instance container name.
        /// </summary>
        public const string ContainerName = "runtime-instance";

        /// <summary>
        /// The runtime instance container port.
        /// </summary>
        public const string ContainerPort = "8080";

        /// <summary>
        /// The Kubernetes pod name prefix used for runtime instances.
        /// </summary>
        public const string PodNamePrefix = "rt";

        /// <summary>
        /// The Kubernetes SDK client mode value.
        /// </summary>
        public const string ClientMode = "KubernetesSdk";

        /// <summary>
        /// The host creation mode value for Kubernetes-backed runtime instances.
        /// </summary>
        public const string HostCreationMode = "Kubernetes";

        /// <summary>
        /// The Kubernetes host provider metadata value.
        /// </summary>
        public const string HostProvider = "kubernetes";

        /// <summary>
        /// The default Minikube node host used when publishing NodePort endpoints.
        /// </summary>
        public const string NodePortHost = "192.168.49.2";

        /// <summary>
        /// The kubectl executable name.
        /// </summary>
        public const string KubectlPath = "kubectl";

        /// <summary>
        /// The shared Kubernetes Gateway resource name.
        /// </summary>
        public const string GatewayName = "ai-runtime-gateway";

        /// <summary>
        /// The shared Kubernetes Gateway listener name.
        /// </summary>
        public const string GatewayListenerName = "runtime";

        /// <summary>
        /// The shared Kubernetes Gateway listener port.
        /// </summary>
        public const string GatewayPort = "8080";

        /// <summary>
        /// The routing header shared by HTTPRoute and GRPCRoute resources.
        /// </summary>
        public const string GatewayRouteHeaderName = "x-ai-runtime-instance-id";

        /// <summary>
        /// The timeout used while waiting for Gateway and route readiness.
        /// </summary>
        public const string GatewayReadinessTimeout = "00:01:00";

        /// <summary>
        /// The polling interval used while waiting for Gateway and route readiness.
        /// </summary>
        public const string GatewayReadinessPollInterval = "00:00:01";

        /// <summary>
        /// The environment variable that can override the GatewayClass used by local tests.
        /// </summary>
        public const string GatewayClassNameEnvironmentVariable =
            "AI_KUBERNETES_GATEWAY_CLASS_NAME";

        /// <summary>
        /// The environment variable that can override the Gateway API controller name.
        /// </summary>
        public const string GatewayControllerNameEnvironmentVariable =
            "AI_KUBERNETES_GATEWAY_CONTROLLER_NAME";

        /// <summary>
        /// Gets the GatewayClass used by local Kubernetes integration tests.
        /// </summary>
        /// <remarks>
        /// Envoy Gateway quickstart installations commonly expose the <c>eg</c>
        /// GatewayClass. The environment override keeps the scenario portable to
        /// another conformant Gateway API controller without changing test code.
        /// </remarks>
        public static string GatewayClassName
        {
            get
            {
                var configuredValue =
                    System.Environment.GetEnvironmentVariable(
                        GatewayClassNameEnvironmentVariable);

                return string.IsNullOrWhiteSpace(configuredValue)
                    ? "eg"
                    : configuredValue.Trim();
            }
        }

        /// <summary>
        /// Gets the Gateway API controller name used by local Kubernetes integration tests.
        /// </summary>
        public static string GatewayControllerName
        {
            get
            {
                var configuredValue =
                    System.Environment.GetEnvironmentVariable(
                        GatewayControllerNameEnvironmentVariable);

                return string.IsNullOrWhiteSpace(configuredValue)
                    ? "gateway.envoyproxy.io/gatewayclass-controller"
                    : configuredValue.Trim();
            }
        }

        /// <summary>
        /// The Redis connection string used from inside Minikube pods.
        /// </summary>
        public const string RedisConnectionString = "host.minikube.internal:6379,abortConnect=false";

        /// <summary>
        /// The MongoDB connection string used from inside Minikube pods.
        /// </summary>
        public const string MongoConnectionString = "mongodb://host.minikube.internal:27017/?directConnection=true";

        /// <summary>
        /// The MongoDB database used by production integration tests.
        /// </summary>
        public const string MongoDatabaseName = "multiplexed_ai_tests";

        /// <summary>
        /// The MongoDB collection used for execution snapshots.
        /// </summary>
        public const string SnapshotCollectionName = "ai_execution_snapshots";

        /// <summary>
        /// The default startup timeout used for Kubernetes runtime pods.
        /// </summary>
        public const string StartupTimeout = "00:00:30";

        /// <summary>
        /// The default runtime readiness timeout used by the Kubernetes host manager.
        /// </summary>
        public const string RuntimeReadinessTimeout = "00:00:30";

        /// <summary>
        /// The default runtime readiness poll interval used by the Kubernetes host manager.
        /// </summary>
        public const string RuntimeReadinessPollInterval = "00:00:01";

        /// <summary>
        /// The default provider scale-out readiness timeout in seconds.
        /// </summary>
        public const string ScaleOutReadinessTimeoutSeconds = "30";

        /// <summary>
        /// The default provider scale-out readiness poll interval in milliseconds.
        /// </summary>
        public const string ScaleOutReadinessPollIntervalMilliseconds = "500";

        /// <summary>
        /// The ASP.NET Core URLs value used by runtime pods.
        /// </summary>
        public const string AspNetCoreUrls = "http://0.0.0.0:8080";

        /// <summary>
        /// The Kestrel endpoint protocol used when a runtime transport requires HTTP/2.
        /// </summary>
        public const string KestrelEndpointProtocols = "Http2";

        /// <summary>
        /// The placeholder OpenAI API key used by local Kubernetes integration tests.
        /// </summary>
        public const string OpenAiApiKey = "demo-local-kubernetes-not-used";
    }
}