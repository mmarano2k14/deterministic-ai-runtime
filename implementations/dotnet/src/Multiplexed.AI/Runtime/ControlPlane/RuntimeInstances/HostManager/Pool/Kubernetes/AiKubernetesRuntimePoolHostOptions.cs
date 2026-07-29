using System;
using System.Collections.Generic;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes
{
    /// <summary>
    /// Provides Kubernetes lifecycle, container, and in-Pod child configuration for one
    /// Runtime Pool Pod.
    /// </summary>
    public sealed class AiKubernetesRuntimePoolHostOptions
    {
        /// <summary>
        /// Gets or sets the runtime image used by the pool host container.
        /// </summary>
        public string RuntimeImage { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the runtime pool container name.
        /// </summary>
        public string ContainerName { get; set; } = "runtime-pool";

        /// <summary>
        /// Gets or sets the optional Kubernetes service account name.
        /// </summary>
        public string? ServiceAccountName { get; set; }

        /// <summary>
        /// Gets or sets the Kubernetes image pull policy.
        /// </summary>
        public AiKubernetesImagePullPolicy ImagePullPolicy { get; set; } =
            AiKubernetesImagePullPolicy.IfNotPresent;

        /// <summary>
        /// Gets or sets the Kubernetes lifecycle client mode.
        /// </summary>
        public AiKubernetesRuntimeHostClientMode ClientMode { get; set; } =
            AiKubernetesRuntimeHostClientMode.Fake;

        /// <summary>
        /// Gets or sets a value indicating whether a stable Service should be created.
        /// </summary>
        public bool CreateService { get; set; } = true;

        /// <summary>
        /// Gets or sets the Service type: ClusterIP or NodePort.
        /// </summary>
        public string ServiceType { get; set; } = "ClusterIP";

        /// <summary>
        /// Gets or sets the NodePort host used by local integration tests.
        /// </summary>
        public string NodePortHost { get; set; } = "127.0.0.1";

        /// <summary>
        /// Gets or sets a value indicating whether the stable Runtime Pool Service should be
        /// exposed through the existing shared Kubernetes Gateway transport endpoint.
        /// </summary>
        /// <remarks>
        /// Gateway lifecycle and the optional host-local kubectl port-forward remain owned by
        /// <see cref="AiKubernetesRuntimeHostOptions"/> and the existing Gateway managers.
        /// </remarks>
        public bool UseGatewayTransportEndpoint { get; set; }

        /// <summary>
        /// Gets or sets Kubernetes resource and readiness timeout.
        /// </summary>
        public TimeSpan StartupTimeout { get; set; } =
            TimeSpan.FromSeconds(90);

        /// <summary>
        /// Gets or sets the Kubernetes readiness polling interval.
        /// </summary>
        public TimeSpan ReadinessPollInterval { get; set; } =
            TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Gets or sets a value indicating whether invocation-owned resources are deleted after failure.
        /// </summary>
        public bool DeleteResourcesOnFailure { get; set; } = true;

        /// <summary>
        /// Gets or sets the Pod UID Downward API file path.
        /// </summary>
        public string PodUidFilePath { get; set; } =
            "/var/run/multiplexed/pod/uid";

        /// <summary>
        /// Gets or sets the mounted directory containing the Pod identity file.
        /// </summary>
        public string PodIdentityMountPath { get; set; } =
            "/var/run/multiplexed/pod";

        /// <summary>
        /// Gets or sets the runtime instance prefix used for replacement children.
        /// </summary>
        public string RuntimeInstanceIdPrefix { get; set; } =
            "runtime-pool";

        /// <summary>
        /// Gets or sets the child dotnet executable.
        /// </summary>
        public string DotnetExecutablePath { get; set; } = "dotnet";

        /// <summary>
        /// Gets or sets the child runtime host assembly.
        /// </summary>
        public string RuntimeHostAssemblyPath { get; set; } =
            "/app/Multiplexed.AI.McpServer.Host.dll";

        /// <summary>
        /// Gets or sets the child working directory.
        /// </summary>
        public string WorkingDirectory { get; set; } = "/app";

        /// <summary>
        /// Gets or sets the loopback host used by child transports.
        /// </summary>
        public string ChildEndpointHost { get; set; } = "127.0.0.1";

        /// <summary>
        /// Gets or sets the default child worker count.
        /// </summary>
        public int WorkerCountPerInstance { get; set; } = 3;

        /// <summary>
        /// Gets or sets the default child concurrent-run count.
        /// </summary>
        public int MaxConcurrentRunsPerInstance { get; set; } = 3;

        /// <summary>
        /// Gets or sets the default child queue capacity.
        /// </summary>
        public int LocalQueueCapacity { get; set; } = 100;

        /// <summary>
        /// Gets or sets the default runtime isolation mode.
        /// </summary>
        public string IsolationMode { get; set; } = "Shared";

        /// <summary>
        /// Gets or sets the child startup timeout.
        /// </summary>
        public TimeSpan ChildStartupTimeout { get; set; } =
            TimeSpan.FromSeconds(60);

        /// <summary>
        /// Gets or sets the child readiness polling interval.
        /// </summary>
        public TimeSpan ChildReadinessPollInterval { get; set; } =
            TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Gets or sets the child heartbeat interval.
        /// </summary>
        public TimeSpan HeartbeatInterval { get; set; } =
            TimeSpan.FromSeconds(2);

        /// <summary>
        /// Gets or sets the Redis connection string used inside the Pod.
        /// </summary>
        public string RedisConnectionString { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the MongoDB connection string used inside the Pod.
        /// </summary>
        public string MongoConnectionString { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the MongoDB database name.
        /// </summary>
        public string MongoDatabaseName { get; set; } = "multiplexed_ai";

        /// <summary>
        /// Gets or sets the model-provider key used by the runtime host.
        /// </summary>
        public string OpenAiApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets additional non-authoritative labels.
        /// </summary>
        public IDictionary<string, string> Labels { get; } =
            new Dictionary<string, string>();

        /// <summary>
        /// Gets additional non-authoritative annotations.
        /// </summary>
        public IDictionary<string, string> Annotations { get; } =
            new Dictionary<string, string>();
    }
}
