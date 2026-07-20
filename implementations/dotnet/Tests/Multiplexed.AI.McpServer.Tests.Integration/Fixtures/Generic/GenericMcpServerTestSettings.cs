namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic
{
    /// <summary>
    /// Provides reusable startup settings for generic MCP integration test hosts.
    /// </summary>
    public static class GenericMcpServerTestSettings
    {
        private const string RedisConnectionString = "localhost:6379";
        private const string MongoConnectionString = "mongodb://localhost:27017";
        public const string DefaultDatabaseName = "multiplexed-ai-mcp-http-tests";

        private static readonly string DefaultControlPlaneId = CreateControlPlaneId("mcp-http-tests");

        /// <summary>
        /// Creates a unique logical control-plane identifier for one MCP integration test scope.
        /// </summary>
        /// <param name="prefix">The control-plane identifier prefix.</param>
        /// <returns>A unique logical control-plane identifier.</returns>
        public static string CreateControlPlaneId(string prefix = "mcp-integration")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

            return $"{prefix.Trim()}-{Guid.NewGuid():N}";
        }

        /// <summary>
        /// Creates MCP control-plane host startup settings using the default test control-plane id.
        /// </summary>
        /// <param name="overrides">Optional configuration overrides.</param>
        /// <returns>The startup settings.</returns>
        public static Dictionary<string, string?> CreateMcpSettings(IReadOnlyDictionary<string, string?>? overrides)
        {
            return CreateMcpSettings(controlPlaneId: null, overrides: overrides);
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
            var effectiveControlPlaneId = string.IsNullOrWhiteSpace(controlPlaneId) ? DefaultControlPlaneId : controlPlaneId;
            var effectiveDatabaseName = ResolveDatabaseName(overrides);

            var settings = new Dictionary<string, string?>
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
                ["Mongo:DatabaseName"] = effectiveDatabaseName,

                ["AiEngine:RuntimeInstanceId"] = "mcp-control-plane-http",

                ["AiEngine:Snapshots:Enabled"] = "true",
                ["AiEngine:Snapshots:Mongo:Enabled"] = "true",
                ["AiEngine:Snapshots:Mongo:ConnectionString"] = MongoConnectionString,
                ["AiEngine:Snapshots:Mongo:DatabaseName"] = effectiveDatabaseName
            };

            ApplyOverrides(settings, overrides);

            return settings;
        }

        /// <summary>
        /// Creates HTTP runtime-instance host startup settings using the default test control-plane id.
        /// </summary>
        /// <param name="overrides">Optional configuration overrides.</param>
        /// <returns>The startup settings.</returns>
        public static Dictionary<string, string?> CreateRuntimeInstanceSettings(IReadOnlyDictionary<string, string?>? overrides)
        {
            return CreateRuntimeInstanceSettings(controlPlaneId: null, runtimeInstanceId: "runtime-http-1", port: 5002, overrides: overrides);
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

            var effectiveControlPlaneId = string.IsNullOrWhiteSpace(controlPlaneId) ? DefaultControlPlaneId : controlPlaneId;
            var effectiveDatabaseName = ResolveDatabaseName(overrides);
            var endpoint = $"http://localhost:{port}";

            var settings = new Dictionary<string, string?>
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
                ["Mongo:DatabaseName"] = effectiveDatabaseName,

                ["AiEngine:RuntimeInstanceId"] = runtimeInstanceId,

                ["AiEngine:Snapshots:Enabled"] = "true",
                ["AiEngine:Snapshots:Mongo:Enabled"] = "true",
                ["AiEngine:Snapshots:Mongo:ConnectionString"] = MongoConnectionString,
                ["AiEngine:Snapshots:Mongo:DatabaseName"] = effectiveDatabaseName,

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

            ApplyOverrides(settings, overrides);

            return settings;
        }

        /// <summary>
        /// Creates control-plane settings that force admission into scale-out request mode
        /// when no dispatchable runtime capacity is available.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier shared by the scenario.</param>
        /// <returns>The control-plane scale-out request settings.</returns>
        public static Dictionary<string, string?> CreateScaleOutOnlyControlPlaneSettings(string controlPlaneId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);

            var controlPlaneRuntimeInstanceId = $"mcp-control-plane-scaleout-{Guid.NewGuid():N}";

            return CreateMcpSettings(
                controlPlaneId,
                new Dictionary<string, string?>
                {
                    ["AiMcpHost:Mode"] = "ControlPlaneWithHttpRuntimeInstances",
                    ["AiMcpHost:EnableSharedQueuePump"] = "false",

                    ["AiSharedQueueBackgroundService:Enabled"] = "false",
                    ["AiSharedQueuePump:Enabled"] = "false",

                    ["AiSharedRuntimeController:SubmitMode"] = "DirectDispatch",
                    ["AiSharedRuntimeController:EnableScaleOutRequest"] = "true",

                    ["AiRunAdmission:Enabled"] = "true",
                    ["AiRunAdmission:EnableScaleOutRequest"] = "true",
                    ["AiRunAdmission:EnableGlobalQueueFallback"] = "false",
                    ["AiRunAdmission:RejectWhenNoCapacity"] = "false",
                    ["AiRunAdmission:MaxInstanceCount"] = "3",

                    ["AiRuntimeScaleOutRequestWatcher:Enabled"] = "true",
                    ["AiRuntimeScaleOutRequestWatcher:ControlPlaneId"] = controlPlaneId,
                    ["AiRuntimeScaleOutRequestWatcher:WatcherId"] = "mcp-scaleout-watcher",
                    ["AiRuntimeScaleOutRequestWatcher:Interval"] = "00:00:00.200",
                    ["AiRuntimeScaleOutRequestWatcher:MaxRequestsPerCycle"] = "10",
                    ["AiRuntimeScaleOutRequestWatcher:RejectOnProviderFailure"] = "true",
                    ["AiRuntimeScaleOutRequestWatcher:IgnoreWhenControlPlaneIdMissing"] = "true",

                    ["SimulatedAiRuntimeScaleOutProvider:Succeed"] = "true",
                    ["SimulatedAiRuntimeScaleOutProvider:RuntimeInstanceIdPrefix"] = "simulated-mcp-runtime",
                    ["SimulatedAiRuntimeScaleOutProvider:Delay"] = "00:00:00",

                    ["AiRuntimeInstanceRegistration:ControlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:ProviderName"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "control-plane-with-http-runtime-scaleout-test",
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-http-scaleout-request",

                    ["AiEngine:ControlPlane:ControlPlaneId"] = controlPlaneId,
                    ["AiEngine:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiEngine:PipelineBackgroundController:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiEngine:RuntimeInstanceWorker:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId
                });
        }

        /// <summary>
        /// Creates local scale-out-only control-plane settings for a single isolated scenario.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier shared by the scenario.</param>
        /// <returns>The local scale-out-only control-plane settings.</returns>
        public static Dictionary<string, string?> CreateLocalScaleOutOnlyControlPlaneSettings(string controlPlaneId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);

            var controlPlaneRuntimeInstanceId = $"mcp-control-plane-local-scaleout-{Guid.NewGuid():N}";
            var deployment = $"test-local-scaleout-{Guid.NewGuid():N}";
            const string runtimeInstanceIdPrefix = "mcp-scaleout-runtime";

            return CreateMcpSettings(
                controlPlaneId,
                new Dictionary<string, string?>
                {
                    ["AiMcpHost:Mode"] = "ControlPlaneWithLocalRuntimeInstances",
                    ["AiMcpHost:EnableSharedQueuePump"] = "true",

                    ["AiSharedQueueBackgroundService:Enabled"] = "true",
                    ["AiSharedQueueBackgroundService:WaitForRuntimeReadiness"] = "false",
                    ["AiSharedQueueBackgroundService:RuntimeReadinessPollInterval"] = "00:00:00.100",
                    ["AiSharedQueueBackgroundService:RuntimeReadinessTimeout"] = "00:01:00",

                    ["AiSharedQueuePump:Enabled"] = "true",
                    ["AiSharedRuntimeController:SubmitMode"] = "DirectDispatch",

                    ["AiRuntimeInstanceRegistration:ControlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:ProviderName"] = "local",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "local",
                    ["AiRuntimeInstanceRegistration:Metadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "local",
                    ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "control-plane-with-local-scaleout",
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = deployment,
                    ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiRuntimeInstanceRegistration:Role"] = "ControlPlane",
                    ["AiRuntimeInstanceRegistration:HeartbeatInterval"] = "00:00:02",
                    ["AiRuntimeInstanceRegistration:RegistryTtl"] = "00:00:30",
                    ["AiRuntimeInstanceRegistration:CapacityTtl"] = "00:00:30",

                    ["AiLocalRuntimeInstancePool:Enabled"] = "false",
                    ["AiLocalRuntimeInstancePool:InstanceCount"] = "1",
                    ["AiLocalRuntimeInstancePool:WorkerCountPerInstance"] = "10",
                    ["AiLocalRuntimeInstancePool:MaxConcurrentRunsPerInstance"] = "3",
                    ["AiLocalRuntimeInstancePool:LocalQueueCapacity"] = "100",
                    ["AiLocalRuntimeInstancePool:RuntimeInstanceIdPrefix"] = runtimeInstanceIdPrefix,

                    ["AiRunAdmission:MaxInstanceCount"] = "3",
                    ["AiRunAdmission:EnableScaleOutRequest"] = "true",
                    ["AiRunAdmission:EnableGlobalQueueFallback"] = "false",
                    ["AiRunAdmission:RejectWhenNoCapacity"] = "false",

                    ["AiRuntimeScaleOutRequestWatcher:Enabled"] = "true",
                    ["AiRuntimeScaleOutRequestWatcher:ControlPlaneId"] = controlPlaneId,
                    ["AiRuntimeScaleOutRequestWatcher:WatcherId"] = "mcp-scaleout-watcher",
                    ["AiRuntimeScaleOutRequestWatcher:Interval"] = "00:00:00.200",
                    ["AiRuntimeScaleOutRequestWatcher:MaxRequestsPerCycle"] = "10",
                    ["AiRuntimeScaleOutRequestWatcher:RejectOnProviderFailure"] = "true",

                    ["AiEngine:ControlPlane:ControlPlaneId"] = controlPlaneId,
                    ["AiEngine:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiEngine:PipelineBackgroundController:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiEngine:PipelineBackgroundController:MaxConcurrentRuns"] = "3",
                    ["AiEngine:PipelineBackgroundController:QueueCapacity"] = "100",
                    ["AiEngine:PipelineBackgroundController:Distributed:Enabled"] = "true",
                    ["AiEngine:PipelineBackgroundController:Distributed:WorkerCount"] = "10",
                    ["AiEngine:PipelineBackgroundController:MaxLocalWorkersPerExecution"] = "3",
                    ["AiEngine:RuntimeInstanceWorker:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId
                });
        }

        /// <summary>
        /// Creates HTTP scale-out-only control-plane settings for a single isolated scenario.
        /// </summary>
        /// <remarks>
        /// By default this factory keeps the historical metadata-only HTTP scale-out behavior.
        /// Set <paramref name="useHostManagerMode" /> to <c>true</c> only for scenarios that explicitly
        /// validate the runtime host manager lifecycle boundary.
        /// </remarks>
        /// <param name="controlPlaneId">The logical control-plane identifier shared by the scenario.</param>
        /// <param name="useHostManagerMode">Whether HTTP scale-out should delegate host startup to IAiRuntimeHostManager.</param>
        /// <param name="useRegisteringTestRuntimeHostManager">Whether the generic test host should override IAiRuntimeHostManager with the legacy registering test host manager. When null, it follows <paramref name="useHostManagerMode" /> for backward compatibility.</param>
        /// <param name="overrides">Optional configuration overrides.</param>
        /// <returns>The HTTP scale-out-only control-plane settings.</returns>
        public static Dictionary<string, string?> CreateHttpScaleOutOnlyControlPlaneSettings(
            string controlPlaneId,
            bool useHostManagerMode = false,
            bool? useRegisteringTestRuntimeHostManager = null,
            IReadOnlyDictionary<string, string?>? overrides = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);

            var controlPlaneRuntimeInstanceId = $"mcp-control-plane-http-scaleout-{Guid.NewGuid():N}";
            var deployment = $"test-http-scaleout-{Guid.NewGuid():N}";
            var httpScaleOutMode = useHostManagerMode ? "HostManager" : "MetadataOnly";
            var useRegisteringHostManager = useRegisteringTestRuntimeHostManager ?? useHostManagerMode;

            var settings = CreateMcpSettings(
                controlPlaneId,
                new Dictionary<string, string?>
                {
                    ["Tests:UseRegisteringTestRuntimeHostManager"] = useRegisteringHostManager.ToString(),

                    ["AiMcpHost:Mode"] = "ControlPlaneWithHttpRuntimeInstances",
                    ["AiMcpHost:EnableSharedQueuePump"] = "true",

                    ["AiSharedQueueBackgroundService:Enabled"] = "true",
                    ["AiSharedQueueBackgroundService:WaitForRuntimeReadiness"] = "false",
                    ["AiSharedQueueBackgroundService:RuntimeReadinessPollInterval"] = "00:00:00.100",
                    ["AiSharedQueueBackgroundService:RuntimeReadinessTimeout"] = "00:01:00",

                    ["AiSharedQueuePump:Enabled"] = "true",
                    ["AiSharedRuntimeController:SubmitMode"] = "DirectDispatch",

                    ["AiRuntimeInstanceRegistration:ControlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:ProviderName"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "control-plane-with-http-scaleout",
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = deployment,
                    ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiRuntimeInstanceRegistration:Role"] = "ControlPlane",
                    ["AiRuntimeInstanceRegistration:HeartbeatInterval"] = "00:00:02",
                    ["AiRuntimeInstanceRegistration:RegistryTtl"] = "00:00:30",
                    ["AiRuntimeInstanceRegistration:CapacityTtl"] = "00:00:30",

                    ["AiLocalRuntimeInstancePool:Enabled"] = "false",
                    ["AiLocalRuntimeInstancePool:InstanceCount"] = "0",
                    ["AiLocalRuntimeInstancePool:WorkerCountPerInstance"] = "0",
                    ["AiLocalRuntimeInstancePool:MaxConcurrentRunsPerInstance"] = "0",
                    ["AiLocalRuntimeInstancePool:LocalQueueCapacity"] = "0",
                    ["AiLocalRuntimeInstancePool:RuntimeInstanceIdPrefix"] = "disabled",

                    ["AiRunAdmission:MaxInstanceCount"] = "3",
                    ["AiRunAdmission:EnableScaleOutRequest"] = "true",
                    ["AiRunAdmission:EnableGlobalQueueFallback"] = "false",
                    ["AiRunAdmission:RejectWhenNoCapacity"] = "false",

                    ["AiRuntimeScaleOutRequestWatcher:Enabled"] = "true",
                    ["AiRuntimeScaleOutRequestWatcher:ControlPlaneId"] = controlPlaneId,
                    ["AiRuntimeScaleOutRequestWatcher:WatcherId"] = "mcp-scaleout-watcher",
                    ["AiRuntimeScaleOutRequestWatcher:Interval"] = "00:00:00.200",
                    ["AiRuntimeScaleOutRequestWatcher:MaxRequestsPerCycle"] = "10",
                    ["AiRuntimeScaleOutRequestWatcher:RejectOnProviderFailure"] = "true",
                    ["AiRuntimeScaleOutRequestWatcher:IgnoreWhenControlPlaneIdMissing"] = "true",

                    ["AiHttpRuntimeScaleOut:Enabled"] = "true",
                    ["AiHttpRuntimeScaleOut:Mode"] = httpScaleOutMode,
                    ["AiHttpRuntimeScaleOut:HostCreationMode"] = "Fixture",
                    ["AiHttpRuntimeScaleOut:RequireReadiness"] = useHostManagerMode ? "true" : "false",
                    ["AiHttpRuntimeScaleOut:ReadinessTimeoutSeconds"] = "15",
                    ["AiHttpRuntimeScaleOut:ReadinessPollIntervalMilliseconds"] = "100",
                    ["AiHttpRuntimeScaleOut:DefaultRuntimeInstanceIdPrefix"] = "http-runtime",
                    ["AiHttpRuntimeScaleOut:EndpointTemplate"] = "http://runtime-host/{runtimeInstanceId}",

                    ["AiEngine:ControlPlane:ControlPlaneId"] = controlPlaneId,
                    ["AiEngine:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiEngine:PipelineBackgroundController:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiEngine:PipelineBackgroundController:MaxConcurrentRuns"] = "3",
                    ["AiEngine:PipelineBackgroundController:QueueCapacity"] = "100",
                    ["AiEngine:PipelineBackgroundController:Distributed:Enabled"] = "true",
                    ["AiEngine:PipelineBackgroundController:Distributed:WorkerCount"] = "10",
                    ["AiEngine:PipelineBackgroundController:MaxLocalWorkersPerExecution"] = "3",
                    ["AiEngine:RuntimeInstanceWorker:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId
                });

            ApplyOverrides(settings, overrides);

            return settings;
        }

        /// <summary>
        /// Creates HTTP process-host scale-out control-plane settings for a single isolated scenario.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier shared by the scenario.</param>
        /// <param name="runtimeHostAssemblyPath">The real Multiplexed.AI.McpServer.Host.dll path to start as RuntimeInstanceOnly.</param>
        /// <returns>The HTTP process-host scale-out control-plane settings.</returns>
        public static Dictionary<string, string?> CreateHttpProcessHostScaleOutOnlyControlPlaneSettings(
            string controlPlaneId,
            string runtimeHostAssemblyPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeHostAssemblyPath);

            return CreateHttpScaleOutOnlyControlPlaneSettings(
                controlPlaneId,
                useHostManagerMode: true,
                useRegisteringTestRuntimeHostManager: false,
                overrides: new Dictionary<string, string?>
                {
                    ["AiHttpRuntimeScaleOut:HostCreationMode"] = "Process",
                    ["AiHttpRuntimeScaleOut:RequireReadiness"] = "true",

                    ["AiRuntimeProcessHostCreation:Enabled"] = "true",
                    ["AiRuntimeProcessHostCreation:DotnetExecutablePath"] = "dotnet",
                    ["AiRuntimeProcessHostCreation:RuntimeHostAssemblyPath"] = runtimeHostAssemblyPath,
                    ["AiRuntimeProcessHostCreation:BasePort"] = "5800",
                    ["AiRuntimeProcessHostCreation:MaxPort"] = "5899",
                    ["AiRuntimeProcessHostCreation:StartupTimeoutSeconds"] = "30",
                    ["AiRuntimeProcessHostCreation:RedirectOutput"] = "true",
                    ["AiRuntimeProcessHostCreation:KillOnDispose"] = "true",

                    ["AiRuntimeProcessHostCreation:EnvironmentVariables:ConnectionStrings__Redis"] = RedisConnectionString,
                    ["AiRuntimeProcessHostCreation:EnvironmentVariables:ConnectionStrings__Mongo"] = MongoConnectionString,
                    ["AiRuntimeProcessHostCreation:EnvironmentVariables:Mongo__DatabaseName"] = DefaultDatabaseName,
                    ["AiRuntimeProcessHostCreation:EnvironmentVariables:AiEngine__Snapshots__Enabled"] = "true",
                    ["AiRuntimeProcessHostCreation:EnvironmentVariables:AiEngine__Snapshots__Mongo__Enabled"] = "true",
                    ["AiRuntimeProcessHostCreation:EnvironmentVariables:AiEngine__Snapshots__Mongo__ConnectionString"] = MongoConnectionString,
                    ["AiRuntimeProcessHostCreation:EnvironmentVariables:AiEngine__Snapshots__Mongo__DatabaseName"] = DefaultDatabaseName,

                    ["AiDecisionLedger:Provider"] = "mongo",
                    ["AiRuntimeProcessHostCreation:EnvironmentVariables:AiDecisionLedger__Provider"] = "mongo",

                });
        }

        /// <summary>
        /// Resolves the MongoDB database used by the current test host.
        /// </summary>
        /// <param name="overrides">Optional scenario-specific configuration overrides.</param>
        /// <returns>The effective MongoDB database name.</returns>
        private static string ResolveDatabaseName(
            IReadOnlyDictionary<string, string?>? overrides)
        {
            if (overrides is not null)
            {
                if (overrides.TryGetValue(
                        "AiEngine:Snapshots:Mongo:DatabaseName",
                        out var snapshotDatabaseName) &&
                    !string.IsNullOrWhiteSpace(snapshotDatabaseName))
                {
                    return snapshotDatabaseName;
                }

                if (overrides.TryGetValue(
                        "Mongo:DatabaseName",
                        out var mongoDatabaseName) &&
                    !string.IsNullOrWhiteSpace(mongoDatabaseName))
                {
                    return mongoDatabaseName;
                }
            }

            return DefaultDatabaseName;
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
                settings[overrideValue.Key] = overrideValue.Value;
            }
        }
    }
}