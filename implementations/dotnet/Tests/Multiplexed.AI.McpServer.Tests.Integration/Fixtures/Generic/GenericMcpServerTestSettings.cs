namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic
{
    /// <summary>
    /// Provides reusable startup settings for generic MCP integration test hosts.
    /// </summary>
    public static class GenericMcpServerTestSettings
    {
        private const string RedisConnectionString =
            "localhost:6379";

        private const string MongoConnectionString =
            "mongodb://localhost:27017";

        private const string DatabaseName =
            "multiplexed-ai-mcp-http-tests";

        private static readonly string DefaultControlPlaneId =
            CreateControlPlaneId("mcp-http-tests");

        /// <summary>
        /// Creates a unique logical control-plane identifier for one MCP integration test scope.
        /// </summary>
        /// <param name="prefix">The control-plane identifier prefix.</param>
        /// <returns>A unique logical control-plane identifier.</returns>
        public static string CreateControlPlaneId(
            string prefix = "mcp-integration")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

            return $"{prefix.Trim()}-{Guid.NewGuid():N}";
        }

        /// <summary>
        /// Creates MCP control-plane host startup settings using the default test control-plane id.
        /// </summary>
        /// <param name="overrides">Optional configuration overrides.</param>
        /// <returns>The startup settings.</returns>
        public static Dictionary<string, string?> CreateMcpSettings(
            IReadOnlyDictionary<string, string?>? overrides)
        {
            return CreateMcpSettings(
                controlPlaneId: null,
                overrides: overrides);
        }

        /// <summary>
        /// Creates MCP control-plane host startup settings.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier shared by all hosts in the scenario.</param>
        /// <param name="overrides">Optional configuration overrides.</param>
        /// <returns>The startup settings.</returns>
        public static Dictionary<string, string?> CreateMcpSettings(
            string? controlPlaneId = null,
            IReadOnlyDictionary<string, string?>? overrides = null)
        {
            var effectiveControlPlaneId =
                string.IsNullOrWhiteSpace(controlPlaneId)
                    ? DefaultControlPlaneId
                    : controlPlaneId;

            var settings =
                new Dictionary<string, string?>
                {
                    ["AiMcpHost:Mode"] = "ControlPlaneWithHttpRuntimeInstances",
                    ["AiMcpHost:Port"] = "5001",
                    ["AiMcpHost:EnableSharedQueuePump"] = "false",
                    ["AiMcpHost:SharedQueuePumpIntervalSeconds"] = "1",
                    ["AiMcpHost:EnableReplayTools"] = "true",
                    ["AiMcpHost:EnableObservabilityTools"] = "true",

                    ["AiEngine:ControlPlane:ControlPlaneId"] = effectiveControlPlaneId,
                    ["AiEngine:ControlPlane:RedisDiscoveryKey"] = $"multiplexed-ai:{effectiveControlPlaneId}",
                    ["AiEngine:ControlPlane:EnableDiscovery"] = "true",
                    ["AiEngine:ControlPlane:PublishDiscovery"] = "true",
                    ["AiEngine:ControlPlane:RequireDiscovery"] = "false",

                    ["AiSharedQueueBackgroundService:WaitForRuntimeReadiness"] = "false",

                    ["AiRuntimeInstanceRegistration:Enabled"] = "true",
                    ["AiRuntimeInstanceRegistration:ControlPlaneId"] = effectiveControlPlaneId,
                    ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = "mcp-control-plane-http",
                    ["AiRuntimeInstanceRegistration:ProviderName"] = "local",
                    ["AiRuntimeInstanceRegistration:Role"] = "ControlPlane",
                    ["AiRuntimeInstanceRegistration:WorkerCount"] = "30",
                    ["AiRuntimeInstanceRegistration:MaxConcurrentRuns"] = "30",
                    ["AiRuntimeInstanceRegistration:QueueCapacity"] = "100",
                    ["AiRuntimeInstanceRegistration:RuntimeVersion"] = "test",
                    ["AiRuntimeInstanceRegistration:HeartbeatInterval"] = "00:00:02",

                    ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "local",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:controlPlaneId"] = effectiveControlPlaneId,

                    ["AiRuntimeInstanceRegistration:Metadata:controlPlaneId"] = effectiveControlPlaneId,
                    ["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "local",
                    ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "control-plane-with-http-runtime",
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-http",

                    ["AiLocalRuntimeInstancePool:Enabled"] = "false",
                    ["AiLocalRuntimeInstancePool:InstanceCount"] = "0",
                    ["AiLocalRuntimeInstancePool:WorkerCountPerInstance"] = "0",
                    ["AiLocalRuntimeInstancePool:MaxConcurrentRunsPerInstance"] = "0",
                    ["AiLocalRuntimeInstancePool:RuntimeInstanceIdPrefix"] = "disabled",

                    ["ConnectionStrings:Redis"] = RedisConnectionString,
                    ["ConnectionStrings:Mongo"] = MongoConnectionString,
                    ["Mongo:DatabaseName"] = DatabaseName,

                    ["AiEngine:RuntimeInstanceId"] = "mcp-control-plane-http",

                    ["AiEngine:Snapshots:Enabled"] = "true",
                    ["AiEngine:Snapshots:Mongo:Enabled"] = "true",
                    ["AiEngine:Snapshots:Mongo:ConnectionString"] = MongoConnectionString,
                    ["AiEngine:Snapshots:Mongo:DatabaseName"] = DatabaseName
                };

            ApplyOverrides(
                settings,
                overrides);

            return settings;
        }

        /// <summary>
        /// Creates HTTP runtime-instance host startup settings using the default test control-plane id.
        /// </summary>
        /// <param name="overrides">Optional configuration overrides.</param>
        /// <returns>The startup settings.</returns>
        public static Dictionary<string, string?> CreateRuntimeInstanceSettings(
            IReadOnlyDictionary<string, string?>? overrides)
        {
            return CreateRuntimeInstanceSettings(
                controlPlaneId: null,
                runtimeInstanceId: "runtime-http-1",
                port: 5002,
                overrides: overrides);
        }

        /// <summary>
        /// Creates HTTP runtime-instance host startup settings.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier shared by all hosts in the scenario.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="port">The runtime instance port.</param>
        /// <param name="overrides">Optional configuration overrides.</param>
        /// <returns>The startup settings.</returns>
        public static Dictionary<string, string?> CreateRuntimeInstanceSettings(
            string? controlPlaneId = null,
            string runtimeInstanceId = "runtime-http-1",
            int port = 5002,
            IReadOnlyDictionary<string, string?>? overrides = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            var effectiveControlPlaneId =
                string.IsNullOrWhiteSpace(controlPlaneId)
                    ? DefaultControlPlaneId
                    : controlPlaneId;

            var endpoint =
                $"http://localhost:{port}";

            var settings =
                new Dictionary<string, string?>
                {
                    ["AiMcpHost:Mode"] = "RuntimeInstanceOnly",
                    ["AiMcpHost:Port"] = port.ToString(),
                    ["AiMcpHost:EnableSharedQueuePump"] = "false",
                    ["AiMcpHost:SharedQueuePumpIntervalSeconds"] = "1",
                    ["AiMcpHost:EnableReplayTools"] = "false",
                    ["AiMcpHost:EnableObservabilityTools"] = "false",

                    ["AiEngine:ControlPlane:ControlPlaneId"] = effectiveControlPlaneId,
                    ["AiEngine:ControlPlane:RedisDiscoveryKey"] = $"multiplexed-ai:{effectiveControlPlaneId}",
                    ["AiEngine:ControlPlane:EnableDiscovery"] = "true",
                    ["AiEngine:ControlPlane:PublishDiscovery"] = "false",
                    ["AiEngine:ControlPlane:RequireDiscovery"] = "false",

                    ["AiSharedQueueBackgroundService:WaitForRuntimeReadiness"] = "false",

                    ["AiRuntimeInstanceRegistration:Enabled"] = "true",
                    ["AiRuntimeInstanceRegistration:ControlPlaneId"] = effectiveControlPlaneId,
                    ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = runtimeInstanceId,
                    ["AiRuntimeInstanceRegistration:ProviderName"] = "http",
                    ["AiRuntimeInstanceRegistration:Role"] = "Runtime",
                    ["AiRuntimeInstanceRegistration:WorkerCount"] = "10",
                    ["AiRuntimeInstanceRegistration:MaxConcurrentRuns"] = "5",
                    ["AiRuntimeInstanceRegistration:QueueCapacity"] = "100",
                    ["AiRuntimeInstanceRegistration:RuntimeVersion"] = "test",
                    ["AiRuntimeInstanceRegistration:HeartbeatInterval"] = "00:00:02",

                    ["AiRuntimeInstanceRegistration:ProviderMetadata:controlPlaneId"] = effectiveControlPlaneId,
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:transport.endpoint"] = endpoint,
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:runtime.instance.id"] = runtimeInstanceId,

                    ["AiRuntimeInstanceRegistration:Metadata:controlPlaneId"] = effectiveControlPlaneId,
                    ["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:transport.endpoint"] = endpoint,
                    ["AiRuntimeInstanceRegistration:Metadata:runtime.instance.id"] = runtimeInstanceId,
                    ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "runtime-instance-only",
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-http",

                    ["AiLocalRuntimeInstancePool:Enabled"] = "false",
                    ["AiLocalRuntimeInstancePool:InstanceCount"] = "0",
                    ["AiLocalRuntimeInstancePool:WorkerCountPerInstance"] = "0",
                    ["AiLocalRuntimeInstancePool:MaxConcurrentRunsPerInstance"] = "0",
                    ["AiLocalRuntimeInstancePool:RuntimeInstanceIdPrefix"] = "disabled",

                    ["ConnectionStrings:Redis"] = RedisConnectionString,
                    ["ConnectionStrings:Mongo"] = MongoConnectionString,
                    ["Mongo:DatabaseName"] = DatabaseName,

                    ["AiEngine:RuntimeInstanceId"] = runtimeInstanceId,

                    ["AiEngine:Snapshots:Enabled"] = "true",
                    ["AiEngine:Snapshots:Mongo:Enabled"] = "true",
                    ["AiEngine:Snapshots:Mongo:ConnectionString"] = MongoConnectionString,
                    ["AiEngine:Snapshots:Mongo:DatabaseName"] = DatabaseName,

                    ["AiEngine:PipelineBackgroundController:RuntimeInstanceId"] = runtimeInstanceId,
                    ["AiEngine:PipelineBackgroundController:MaxConcurrentRuns"] = "5",
                    ["AiEngine:PipelineBackgroundController:QueueCapacity"] = "1000",
                    ["AiEngine:PipelineBackgroundController:RejectEnqueueWhenStopped"] = "false",
                    ["AiEngine:PipelineBackgroundController:StopOnFirstFailure"] = "false",

                    ["AiEngine:PipelineBackgroundController:Distributed:Enabled"] = "true",
                    ["AiEngine:PipelineBackgroundController:Distributed:WorkerCount"] = "10",
                    ["AiEngine:PipelineBackgroundController:Distributed:StopOnFirstTerminal"] = "true",
                    ["AiEngine:PipelineBackgroundController:Distributed:TerminalObservationTimeout"] = "00:00:30",

                    ["AiEngine:RuntimeInstanceWorker:RuntimeInstanceId"] = runtimeInstanceId,
                    ["AiEngine:RuntimeInstanceWorker:MaxCycles"] = "-1",
                    ["AiEngine:RuntimeInstanceWorker:MaxStepsPerCycle"] = "10",
                    ["AiEngine:RuntimeInstanceWorker:IdleDelay"] = "00:00:00.025",
                    ["AiEngine:RuntimeInstanceWorker:IgnoreConcurrencyConflicts"] = "true"
                };

            ApplyOverrides(
                settings,
                overrides);

            return settings;
        }

        /// <summary>
        /// Applies configuration overrides to a settings dictionary.
        /// </summary>
        /// <param name="settings">The target settings dictionary.</param>
        /// <param name="overrides">The optional overrides.</param>
        private static void ApplyOverrides(
            IDictionary<string, string?> settings,
            IReadOnlyDictionary<string, string?>? overrides)
        {
            if (overrides is null)
            {
                return;
            }

            foreach (var overrideValue in overrides)
            {
                settings[overrideValue.Key] =
                    overrideValue.Value;
            }
        }
    }
}
