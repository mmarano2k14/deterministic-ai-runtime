namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic
{
    /// <summary>
    /// Provides reusable startup settings for generic MCP integration test hosts.
    /// </summary>
    public static class GenericMcpServerTestSettings
    {
        private const string RedisConnectionString =
            "localhost:6379,defaultDatabase=15";

        private const string MongoConnectionString =
            "mongodb://localhost:27017";

        private const string DatabaseName =
            "multiplexed-ai-mcp-http-tests";

        public static Dictionary<string, string?> CreateMcpSettings(
            IReadOnlyDictionary<string, string?>? overrides = null)
        {
            var settings =
                new Dictionary<string, string?>
                {
                    ["AiMcpHost:Mode"] = "ControlPlaneWithHttpRuntimeInstances",
                    ["AiMcpHost:Port"] = "5001",
                    ["AiMcpHost:EnableSharedQueuePump"] = "false",
                    ["AiMcpHost:SharedQueuePumpIntervalSeconds"] = "1",
                    ["AiMcpHost:EnableReplayTools"] = "true",
                    ["AiMcpHost:EnableObservabilityTools"] = "true",

                    ["AiRuntimeInstanceRegistration:Enabled"] = "true",
                    ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = "mcp-control-plane-http",
                    ["AiRuntimeInstanceRegistration:ProviderName"] = "local",
                    ["AiRuntimeInstanceRegistration:Role"] = "ControlPlane",
                    ["AiRuntimeInstanceRegistration:WorkerCount"] = "30",
                    ["AiRuntimeInstanceRegistration:MaxConcurrentRuns"] = "30",
                    ["AiRuntimeInstanceRegistration:QueueCapacity"] = "100",
                    ["AiRuntimeInstanceRegistration:RuntimeVersion"] = "test",
                    ["AiRuntimeInstanceRegistration:HeartbeatInterval"] = "00:00:02",

                    ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "local",

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

        public static Dictionary<string, string?> CreateRuntimeInstanceSettings(
            IReadOnlyDictionary<string, string?>? overrides = null)
        {
            var settings =
                new Dictionary<string, string?>
                {
                    ["AiMcpHost:Mode"] = "RuntimeInstanceOnly",
                    ["AiMcpHost:Port"] = "5002",
                    ["AiMcpHost:EnableSharedQueuePump"] = "false",
                    ["AiMcpHost:SharedQueuePumpIntervalSeconds"] = "1",
                    ["AiMcpHost:EnableReplayTools"] = "false",
                    ["AiMcpHost:EnableObservabilityTools"] = "false",

                    ["AiRuntimeInstanceRegistration:Enabled"] = "true",
                    ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = "runtime-http-1",
                    ["AiRuntimeInstanceRegistration:ProviderName"] = "http",
                    ["AiRuntimeInstanceRegistration:Role"] = "Runtime",
                    ["AiRuntimeInstanceRegistration:WorkerCount"] = "10",
                    ["AiRuntimeInstanceRegistration:MaxConcurrentRuns"] = "5",
                    ["AiRuntimeInstanceRegistration:QueueCapacity"] = "100",
                    ["AiRuntimeInstanceRegistration:RuntimeVersion"] = "test",
                    ["AiRuntimeInstanceRegistration:HeartbeatInterval"] = "00:00:02",

                    ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:transport.endpoint"] = "http://localhost",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:runtime.instance.id"] = "runtime-http-1",

                    ["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:transport.endpoint"] = "http://localhost",
                    ["AiRuntimeInstanceRegistration:Metadata:runtime.instance.id"] = "runtime-http-1",
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

                    ["AiEngine:RuntimeInstanceId"] = "runtime-http-1",

                    ["AiEngine:Snapshots:Enabled"] = "true",
                    ["AiEngine:Snapshots:Mongo:Enabled"] = "true",
                    ["AiEngine:Snapshots:Mongo:ConnectionString"] = MongoConnectionString,
                    ["AiEngine:Snapshots:Mongo:DatabaseName"] = DatabaseName,

                    ["AiEngine:PipelineBackgroundController:RuntimeInstanceId"] = "runtime-http-1",
                    ["AiEngine:PipelineBackgroundController:MaxConcurrentRuns"] = "5",
                    ["AiEngine:PipelineBackgroundController:QueueCapacity"] = "1000",
                    ["AiEngine:PipelineBackgroundController:RejectEnqueueWhenStopped"] = "false",
                    ["AiEngine:PipelineBackgroundController:StopOnFirstFailure"] = "false",

                    ["AiEngine:PipelineBackgroundController:Distributed:Enabled"] = "true",
                    ["AiEngine:PipelineBackgroundController:Distributed:WorkerCount"] = "10",
                    ["AiEngine:PipelineBackgroundController:Distributed:StopOnFirstTerminal"] = "true",
                    ["AiEngine:PipelineBackgroundController:Distributed:TerminalObservationTimeout"] = "00:00:30",

                    ["AiEngine:RuntimeInstanceWorker:RuntimeInstanceId"] = "runtime-http-1",
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