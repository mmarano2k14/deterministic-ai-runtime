using System;
using System.Collections.Generic;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod
{
    /// <summary>
    /// Provides strongly typed bootstrap configuration for the Process Pool Manager running
    /// inside one Kubernetes Runtime Pool Pod.
    /// </summary>
    public sealed class AiKubernetesRuntimePoolInPodOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether the current RuntimeInstanceOnly host is a
        /// Kubernetes Runtime Pool parent.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets the logical Runtime Pool identifier.
        /// </summary>
        public string PoolId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the path populated by the Kubernetes Downward API with the Pod UID.
        /// </summary>
        public string PodUidFilePath { get; set; } =
            "/var/run/multiplexed/pod/uid";

        /// <summary>
        /// Gets or sets the runtime instance identifier prefix used for replacement children.
        /// </summary>
        public string RuntimeInstanceIdPrefix { get; set; } =
            "runtime-pool";

        /// <summary>
        /// Gets or sets the logical control-plane identifier.
        /// </summary>
        public string ControlPlaneId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the runtime provider name.
        /// </summary>
        public string ProviderName { get; set; } = "http";

        /// <summary>
        /// Gets or sets the runtime command transport name.
        /// </summary>
        public string TransportName { get; set; } = "http";

        /// <summary>
        /// Gets or sets the stable parent transport port.
        /// </summary>
        public int StableTransportPort { get; set; } = 8080;

        /// <summary>
        /// Gets or sets the initial child process count.
        /// </summary>
        public int InitialProcessCount { get; set; } = 3;

        /// <summary>
        /// Gets or sets the minimum healthy child process count.
        /// </summary>
        public int MinimumProcessCount { get; set; } = 3;

        /// <summary>
        /// Gets or sets the maximum child process count.
        /// </summary>
        public int MaximumProcessCount { get; set; } = 3;

        /// <summary>
        /// Gets or sets the maximum number of parallel child startups.
        /// </summary>
        public int StartupParallelism { get; set; } = 1;

        /// <summary>
        /// Gets or sets the graceful pool shutdown timeout.
        /// </summary>
        public int ShutdownTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Gets or sets the dotnet executable used for RuntimeInstanceOnly children.
        /// </summary>
        public string DotnetExecutablePath { get; set; } = "dotnet";

        /// <summary>
        /// Gets or sets the RuntimeInstanceOnly host assembly path used by child processes.
        /// </summary>
        public string RuntimeHostAssemblyPath { get; set; } =
            "/app/Multiplexed.AI.McpServer.Host.dll";

        /// <summary>
        /// Gets or sets the child working directory.
        /// </summary>
        public string WorkingDirectory { get; set; } = "/app";

        /// <summary>
        /// Gets or sets the child loopback endpoint host.
        /// </summary>
        public string EndpointHost { get; set; } = "127.0.0.1";

        /// <summary>
        /// Gets or sets the worker count published by every child runtime.
        /// </summary>
        public int WorkerCountPerInstance { get; set; } = 3;

        /// <summary>
        /// Gets or sets the maximum concurrent runs published by every child runtime.
        /// </summary>
        public int MaxConcurrentRunsPerInstance { get; set; } = 3;

        /// <summary>
        /// Gets or sets the local queue capacity published by every child runtime.
        /// </summary>
        public int LocalQueueCapacity { get; set; } = 100;

        /// <summary>
        /// Gets or sets the runtime isolation mode.
        /// </summary>
        public string IsolationMode { get; set; } = "Shared";

        /// <summary>
        /// Gets or sets a value indicating whether dedicated capacity is preferred.
        /// </summary>
        public bool PreferDedicatedCapacity { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether shared fallback is allowed.
        /// </summary>
        public bool AllowSharedFallback { get; set; } = true;

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
        /// Gets or sets the runtime heartbeat interval.
        /// </summary>
        public TimeSpan HeartbeatInterval { get; set; } =
            TimeSpan.FromSeconds(2);

        /// <summary>
        /// Gets or sets the Redis connection string copied to child process configuration.
        /// </summary>
        public string RedisConnectionString { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the MongoDB connection string copied to child process configuration.
        /// </summary>
        public string MongoConnectionString { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the MongoDB database name copied to child process configuration.
        /// </summary>
        public string MongoDatabaseName { get; set; } = "multiplexed_ai";

        /// <summary>
        /// Gets or sets the placeholder or real model-provider key copied to child configuration.
        /// </summary>
        public string OpenAiApiKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the durable context key.
        /// </summary>
        public string ContextKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the durable project name.
        /// </summary>
        public string Project { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the durable user identifier.
        /// </summary>
        public string UserId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the durable tenant identifier.
        /// </summary>
        public string TenantId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the durable tenant-group identifier.
        /// </summary>
        public string TenantGroupId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the current namespace.
        /// </summary>
        public string CurrentNamespace { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the snapshot TTL in seconds.
        /// </summary>
        public int SnapshotTtlSeconds { get; set; } = 3600;

        /// <summary>
        /// Gets the exact initial child runtime plans.
        /// </summary>
        public IList<AiKubernetesRuntimePoolInPodRuntimeInstanceOptions>
            RuntimeInstances
        {
            get;
        } = new List<AiKubernetesRuntimePoolInPodRuntimeInstanceOptions>();
    }
}
