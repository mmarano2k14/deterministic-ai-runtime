using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod
{
    /// <summary>
    /// Creates strongly typed parent-container command-line configuration for the in-Pod
    /// Process Pool Manager.
    /// </summary>
    public sealed class AiKubernetesRuntimePoolInPodCommandLineFactory
    {
        private readonly AiKubernetesRuntimePoolHostOptions hostOptions;

        /// <summary>
        /// Initializes a new command-line factory.
        /// </summary>
        public AiKubernetesRuntimePoolInPodCommandLineFactory(
            AiKubernetesRuntimePoolHostOptions hostOptions)
        {
            this.hostOptions =
                hostOptions
                ?? throw new ArgumentNullException(nameof(hostOptions));
        }

        /// <summary>
        /// Creates the exact parent container arguments.
        /// </summary>
        public IReadOnlyList<string> Create(
            AiKubernetesRuntimePoolPodSpec podSpec,
            AiRuntimeHostStartRequest request)
        {
            ArgumentNullException.ThrowIfNull(podSpec);
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(
                request.ExecutionContextSnapshot);

            var section = "AiKubernetesRuntimePoolInPod";
            var arguments = new List<string>
            {
                Setting("AiMcpHost:Mode", "RuntimeInstanceOnly"),
                Setting("AiMcpHost:Port",
                    podSpec.Bootstrap.StableTransportPort),
                Setting("Kestrel:Endpoints:RuntimePool:Url",
                    string.Concat(
                        "http://0.0.0.0:",
                        podSpec.Bootstrap.StableTransportPort)),
                Setting("Kestrel:Endpoints:RuntimePool:Protocols",
                    ResolveStableEndpointProtocols(
                        podSpec.Bootstrap.TransportName)),
                Setting("Kestrel:Endpoints:Readiness:Url",
                    string.Concat(
                        "http://0.0.0.0:",
                        podSpec.Bootstrap.ReadinessPort)),
                Setting("Kestrel:Endpoints:Readiness:Protocols",
                    "Http1"),
                Setting("ConnectionStrings:Redis",
                    this.hostOptions.RedisConnectionString),
                Setting("ConnectionStrings:Mongo",
                    this.hostOptions.MongoConnectionString),
                Setting("Mongo:DatabaseName",
                    this.hostOptions.MongoDatabaseName),
                Setting("OpenAI:ApiKey",
                    this.hostOptions.OpenAiApiKey),
                Setting("AiEngine:ControlPlane:ControlPlaneId",
                    request.ControlPlaneId),
                Setting("AiRuntimeInstanceRegistration:Enabled",
                    false),
                Setting("AiLocalRuntimeInstancePool:Enabled",
                    false),
                Setting(string.Concat(section, ":Enabled"), true),
                Setting(string.Concat(section, ":PoolId"),
                    podSpec.PoolId),
                Setting(string.Concat(section, ":PodUidFilePath"),
                    this.hostOptions.PodUidFilePath),
                Setting(string.Concat(
                        section,
                        ":RuntimeInstanceIdPrefix"),
                    this.hostOptions.RuntimeInstanceIdPrefix),
                Setting(string.Concat(section, ":ControlPlaneId"),
                    request.ControlPlaneId),
                Setting(string.Concat(section, ":ProviderName"),
                    podSpec.Bootstrap.ProviderName),
                Setting(string.Concat(section, ":TransportName"),
                    podSpec.Bootstrap.TransportName),
                Setting(string.Concat(section, ":StableTransportPort"),
                    podSpec.Bootstrap.StableTransportPort),
                Setting(string.Concat(section, ":InitialProcessCount"),
                    podSpec.Bootstrap.InitialRuntimeInstanceCount),
                Setting(string.Concat(section, ":MinimumProcessCount"),
                    podSpec.Bootstrap.MinimumRuntimeInstanceCount),
                Setting(string.Concat(section, ":MaximumProcessCount"),
                    podSpec.Bootstrap.MaximumRuntimeInstanceCount),
                Setting(string.Concat(section, ":StartupParallelism"),
                    podSpec.Bootstrap.StartupParallelism),
                Setting(string.Concat(section, ":ShutdownTimeoutSeconds"),
                    podSpec.Bootstrap.ShutdownTimeoutSeconds),
                Setting(string.Concat(section, ":DotnetExecutablePath"),
                    this.hostOptions.DotnetExecutablePath),
                Setting(string.Concat(
                        section,
                        ":RuntimeHostAssemblyPath"),
                    this.hostOptions.RuntimeHostAssemblyPath),
                Setting(string.Concat(section, ":WorkingDirectory"),
                    this.hostOptions.WorkingDirectory),
                Setting(string.Concat(section, ":EndpointHost"),
                    this.hostOptions.ChildEndpointHost),
                Setting(string.Concat(
                        section,
                        ":WorkerCountPerInstance"),
                    PositiveOrDefault(
                        request.WorkerCountPerInstance,
                        this.hostOptions.WorkerCountPerInstance)),
                Setting(string.Concat(
                        section,
                        ":MaxConcurrentRunsPerInstance"),
                    PositiveOrDefault(
                        request.MaxConcurrentRunsPerInstance,
                        this.hostOptions.MaxConcurrentRunsPerInstance)),
                Setting(string.Concat(section, ":LocalQueueCapacity"),
                    NonNegativeOrDefault(
                        request.LocalQueueCapacity,
                        this.hostOptions.LocalQueueCapacity)),
                Setting(string.Concat(section, ":IsolationMode"),
                    request.IsolationMode
                    ?? this.hostOptions.IsolationMode),
                Setting(string.Concat(
                        section,
                        ":PreferDedicatedCapacity"),
                    request.PreferDedicatedCapacity),
                Setting(string.Concat(
                        section,
                        ":AllowSharedFallback"),
                    request.AllowSharedFallback),
                Setting(string.Concat(section, ":ChildStartupTimeout"),
                    this.hostOptions.ChildStartupTimeout),
                Setting(string.Concat(
                        section,
                        ":ChildReadinessPollInterval"),
                    this.hostOptions.ChildReadinessPollInterval),
                Setting(string.Concat(section, ":HeartbeatInterval"),
                    this.hostOptions.HeartbeatInterval),
                Setting(string.Concat(
                        section,
                        ":RedisConnectionString"),
                    this.hostOptions.RedisConnectionString),
                Setting(string.Concat(
                        section,
                        ":MongoConnectionString"),
                    this.hostOptions.MongoConnectionString),
                Setting(string.Concat(
                        section,
                        ":MongoDatabaseName"),
                    this.hostOptions.MongoDatabaseName),
                Setting(string.Concat(section, ":OpenAiApiKey"),
                    this.hostOptions.OpenAiApiKey),
                Setting(string.Concat(section, ":ContextKey"),
                    request.ExecutionContextSnapshot.ContextKey),
                Setting(string.Concat(section, ":Project"),
                    request.ExecutionContextSnapshot.Project),
                Setting(string.Concat(section, ":UserId"),
                    request.ExecutionContextSnapshot.UserId),
                Setting(string.Concat(section, ":TenantId"),
                    request.ExecutionContextSnapshot.TenantId),
                Setting(string.Concat(section, ":TenantGroupId"),
                    request.ExecutionContextSnapshot.TenantGroupId),
                Setting(string.Concat(section, ":CurrentNamespace"),
                    request.ExecutionContextSnapshot.CurrentNamespace),
                Setting(string.Concat(section, ":SnapshotTtlSeconds"),
                    request.ExecutionContextSnapshot.TtlSeconds)
            };

            foreach (var pair in this.hostOptions.ChildEnvironmentVariables
                         .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                arguments.Add(
                    Setting(
                        string.Concat(
                            section,
                            ":ChildEnvironmentVariables:",
                            pair.Key),
                        pair.Value));
            }

            for (var index = 0;
                 index < podSpec.Bootstrap.RuntimeInstances.Count;
                 index++)
            {
                var runtime =
                    podSpec.Bootstrap.RuntimeInstances[index];
                var item =
                    string.Concat(
                        section,
                        ":RuntimeInstances:",
                        index.ToString(
                            CultureInfo.InvariantCulture));

                arguments.Add(
                    Setting(
                        string.Concat(item, ":Ordinal"),
                        runtime.Ordinal));
                arguments.Add(
                    Setting(
                        string.Concat(item, ":RuntimeInstanceId"),
                        runtime.RuntimeInstanceId));
                arguments.Add(
                    Setting(
                        string.Concat(item, ":TransportPort"),
                        runtime.TransportPort));
            }

            return arguments.AsReadOnly();
        }

        /// <summary>
        /// Resolves the clear-text protocol contract for the stable pool endpoint.
        /// </summary>
        private static string ResolveStableEndpointProtocols(
            string transportName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(transportName);

            return string.Equals(
                    transportName.Trim(),
                    "grpc",
                    StringComparison.OrdinalIgnoreCase)
                ? "Http2"
                : "Http1";
        }

        /// <summary>
        /// Formats one ASP.NET Core command-line setting.
        /// </summary>
        private static string Setting(
            string key,
            object? value)
        {
            var text =
                value switch
                {
                    bool boolean =>
                        boolean.ToString(
                            CultureInfo.InvariantCulture),
                    TimeSpan timeSpan =>
                        timeSpan.ToString(
                            "c",
                            CultureInfo.InvariantCulture),
                    IFormattable formattable =>
                        formattable.ToString(
                            null,
                            CultureInfo.InvariantCulture),
                    _ => value?.ToString() ?? string.Empty
                };

            return string.Concat(
                "--",
                key,
                "=",
                text);
        }

        /// <summary>
        /// Uses the request value when non-negative, otherwise the configured default.
        /// </summary>
        private static int NonNegativeOrDefault(
            int requestValue,
            int defaultValue)
        {
            return requestValue >= 0
                ? requestValue
                : defaultValue;
        }

        /// <summary>
        /// Uses the request value when positive, otherwise the configured default.
        /// </summary>
        private static int PositiveOrDefault(
            int requestValue,
            int defaultValue)
        {
            return requestValue > 0
                ? requestValue
                : defaultValue;
        }
    }
}
