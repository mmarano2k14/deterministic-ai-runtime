using System;
using System.Collections.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Runners
{
    /// <summary>
    /// Builds MCP host settings for production HTTP process-host scenarios.
    /// </summary>
    /// <remarks>
    /// This builder centralizes the configuration required to run production-like
    /// scenarios where the MCP control-plane starts from zero runtime capacity,
    /// creates scale-out requests, provisions RuntimeInstanceOnly hosts as real
    /// processes, dispatches through the HTTP provider, and reads durable
    /// persistence and observability data from shared stores.
    ///
    /// The runner should orchestrate the scenario execution only. Provider,
    /// persistence, replay, tracing, tenant runtime settings, and process-host
    /// settings belong here.
    /// </remarks>
    internal static class HttpProcessHostProductionScenarioSettingsBuilder
    {
        /// <summary>
        /// Builds the complete settings dictionary for a production HTTP process-host scenario.
        /// </summary>
        /// <param name="scenario">The production runtime scenario definition.</param>
        /// <param name="controlPlaneId">The resolved control-plane identifier.</param>
        /// <param name="runtimeHostAssemblyPath">The runtime host assembly path used for process creation.</param>
        /// <returns>The complete MCP host settings dictionary.</returns>
        public static Dictionary<string, string?> Build(
            ProductionRuntimeScenarioDefinition scenario,
            string controlPlaneId,
            string runtimeHostAssemblyPath)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeHostAssemblyPath);

            var settings =
                GenericMcpServerTestSettings.CreateHttpProcessHostScaleOutOnlyControlPlaneSettings(
                    controlPlaneId,
                    runtimeHostAssemblyPath);

            ApplySharedQueueSettings(settings);
            ApplyRuntimeExecutionRecoverySettings(settings, scenario);
            ApplySubmitModeSettings(settings, scenario);
            ApplyTenantRuntimeSettings(settings, scenario);
            ApplyScaleOutSettings(settings);
            ApplyHttpProviderSettings(settings);
            ApplyPersistenceSettings(settings, scenario);
            ApplyObservabilitySettings(settings, scenario);

            return settings;
        }

        /// <summary>
        /// Applies shared queue and background pump settings.
        /// </summary>
        /// <param name="settings">The settings dictionary to mutate.</param>
        /// <remarks>
        /// The process-host production scenario relies on the parent MCP host
        /// pumping the Redis-backed shared queue after runtime capacity becomes
        /// available.
        /// </remarks>
        private static void ApplySharedQueueSettings(
            Dictionary<string, string?> settings)
        {
            settings["AiMcpHost:EnableSharedQueuePump"] = "true";

            settings["AiSharedQueueBackgroundService:Enabled"] = "true";
            settings["AiSharedQueueBackgroundService:WaitForRuntimeReadiness"] = "false";
            settings["AiSharedQueueBackgroundService:RuntimeReadinessPollInterval"] = "00:00:00.100";
            settings["AiSharedQueueBackgroundService:RuntimeReadinessTimeout"] = "00:00:05";
            settings["AiSharedQueueBackgroundService:IntervalSeconds"] = "1";
            settings["AiSharedQueueBackgroundService:MaxDispatchesPerCycle"] = "10";

            settings["AiSharedQueuePump:Enabled"] = "true";
        }

        /// <summary>
        /// Applies runtime execution recovery settings used by process-host recovery scenarios.
        /// </summary>
        /// <param name="settings">The settings dictionary to mutate.</param>
        /// <param name="scenario">The production runtime scenario definition.</param>
        /// <remarks>
        /// DAG execution resume is enabled for process-host crash recovery scenarios because
        /// strict recovery must preserve the durable execution identifier across runtime
        /// process failure, shared queue requeue, scale-out replacement, HTTP redispatch,
        /// and runtime queue resume.
        /// </remarks>
        private static void ApplyRuntimeExecutionRecoverySettings(
            Dictionary<string, string?> settings,
            ProductionRuntimeScenarioDefinition scenario)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(scenario);

            var enableDagExecutionResume =
                scenario.Name.Contains(
                    "dag-resume",
                    StringComparison.OrdinalIgnoreCase) ||
                scenario.Name.Contains(
                    "real-runtime-crash-recovery",
                    StringComparison.OrdinalIgnoreCase);

            settings["AiRuntimeExecutionRecoveryReconciliation:Enabled"] = "true";
            settings["AiRuntimeExecutionRecoveryReconciliation:IncludeUnhealthyRuntimeInstances"] = "true";
            settings["AiRuntimeExecutionRecoveryReconciliation:IncludeStoppedRuntimeInstances"] = "true";
            settings["AiRuntimeExecutionRecoveryReconciliation:IncludeDrainingRuntimeInstances"] = "true";
            settings["AiRuntimeExecutionRecoveryReconciliation:RequeueUnfinishedRuns"] = "true";
            settings["AiRuntimeExecutionRecoveryReconciliation:DryRun"] = "false";
            settings["AiRuntimeExecutionRecoveryReconciliation:EnableDagExecutionResume"] = enableDagExecutionResume.ToString();
        }

        /// <summary>
        /// Applies the shared runtime controller submit mode.
        /// </summary>
        /// <param name="settings">The settings dictionary to mutate.</param>
        /// <param name="scenario">The production runtime scenario definition.</param>
        /// <remarks>
        /// Direct-dispatch mode is required for zero-capacity scale-out scenarios
        /// because admission must immediately observe missing capacity and create
        /// scale-out requests.
        /// </remarks>
        private static void ApplySubmitModeSettings(
            Dictionary<string, string?> settings,
            ProductionRuntimeScenarioDefinition scenario)
        {
            settings["AiSharedRuntimeController:SubmitMode"] =
                scenario.SubmitMode == ProductionRuntimeSubmitMode.DirectDispatch
                    ? "DirectDispatch"
                    : "QueueFirst";
        }

        /// <summary>
        /// Applies tenant runtime settings for the parent MCP host and child runtime processes.
        /// </summary>
        /// <param name="settings">The settings dictionary to mutate.</param>
        /// <param name="scenario">The production runtime scenario definition.</param>
        /// <remarks>
        /// Tenant runtime mode is a scenario-level contract. It must flow into
        /// admission, scale-out request creation, runtime host creation, runtime
        /// registration metadata, capacity metadata, and routing visibility.
        ///
        /// The settings are written both to the parent host configuration and to
        /// process-host environment variables so RuntimeInstanceOnly child
        /// processes can resolve the same tenant runtime policy.
        /// </remarks>
        private static void ApplyTenantRuntimeSettings(
            Dictionary<string, string?> settings,
            ProductionRuntimeScenarioDefinition scenario)
        {
            settings["AiTenantRuntimeSettings:Provider"] = "Configuration";
            settings["AiTenantRuntimeSettings:Enabled"] = "true";

            ApplyParentAndProcessSetting(settings, "AiTenantRuntimeSettings:Provider", "Configuration");
            ApplyParentAndProcessSetting(settings, "AiTenantRuntimeSettings:Enabled", "true");

            for (var index = 0; index < scenario.Tenants.Count; index++)
            {
                var tenant = scenario.Tenants[index];
                var isolationMode = ProductionTenantRuntimeModeMapper.ResolveIsolationMode(tenant.RuntimeMode);
                var preferDedicatedCapacity = ProductionTenantRuntimeModeMapper.ResolvePreferDedicatedCapacity(tenant.RuntimeMode);
                var allowSharedFallback = ProductionTenantRuntimeModeMapper.ResolveAllowSharedFallback(tenant.RuntimeMode);

                var prefix = $"AiTenantRuntimeSettings:Tenants:{index}";

                ApplyParentAndProcessSetting(settings, $"{prefix}:TenantId", tenant.TenantId);

                if (!string.IsNullOrWhiteSpace(tenant.TenantGroupId))
                {
                    ApplyParentAndProcessSetting(settings, $"{prefix}:TenantGroupId", tenant.TenantGroupId);
                }

                ApplyParentAndProcessSetting(settings, $"{prefix}:IsolationMode", isolationMode.ToString());
                ApplyParentAndProcessSetting(settings, $"{prefix}:PreferDedicatedCapacity", preferDedicatedCapacity.ToString());
                ApplyParentAndProcessSetting(settings, $"{prefix}:AllowSharedFallback", allowSharedFallback.ToString());
                ApplyParentAndProcessSetting(settings, $"{prefix}:MaxRuntimeInstances", tenant.MaxRuntimeInstances.ToString());
                ApplyParentAndProcessSetting(settings, $"{prefix}:WorkerCountPerInstance", tenant.WorkerCountPerInstance.ToString());
                ApplyParentAndProcessSetting(settings, $"{prefix}:MaxConcurrentRunsPerInstance", tenant.MaxConcurrentRunsPerInstance.ToString());
                ApplyParentAndProcessSetting(settings, $"{prefix}:LocalQueueCapacity", tenant.LocalQueueCapacity.ToString());
                ApplyParentAndProcessSetting(settings, $"{prefix}:RuntimeInstanceIdPrefix", tenant.RuntimeInstanceIdPrefix);
            }
        }

        /// <summary>
        /// Applies scale-out watcher settings.
        /// </summary>
        /// <param name="settings">The settings dictionary to mutate.</param>
        /// <remarks>
        /// The watcher observes Redis scale-out requests and asks the selected
        /// provider to provision runtime capacity.
        /// </remarks>
        private static void ApplyScaleOutSettings(
            Dictionary<string, string?> settings)
        {
            settings["AiRuntimeScaleOutWatcher:Enabled"] = "true";
            settings["AiRuntimeScaleOutWatcher:IntervalSeconds"] = "1";
        }

        /// <summary>
        /// Applies HTTP runtime provider settings.
        /// </summary>
        /// <param name="settings">The settings dictionary to mutate.</param>
        /// <remarks>
        /// Circuit breaker behavior is disabled in this production scenario so
        /// the test validates process-host provisioning, dispatch, persistence,
        /// replay, and observability without mixing endpoint-health behavior into
        /// the same assertion path.
        /// </remarks>
        private static void ApplyHttpProviderSettings(
            Dictionary<string, string?> settings)
        {
            settings["AiHttpRuntimeInstanceProvider:EnableCircuitBreaker"] = "false";
            settings["AiHttpRuntimeInstanceProvider:CircuitBreakerFailureThreshold"] = "100";
        }

        /// <summary>
        /// Applies durable persistence settings for the parent MCP host and child runtime processes.
        /// </summary>
        /// <param name="settings">The settings dictionary to mutate.</param>
        /// <param name="scenario">The production runtime scenario definition.</param>
        /// <remarks>
        /// In process-host scenarios, any data written by RuntimeInstanceOnly
        /// child processes and later read by the parent MCP process must be stored
        /// in a shared durable store. This includes snapshots, payloads, decision
        /// ledger entries, and replay fingerprint metadata.
        /// </remarks>
        private static void ApplyPersistenceSettings(
            Dictionary<string, string?> settings,
            ProductionRuntimeScenarioDefinition scenario)
        {
            if (scenario.PersistenceProfile != ProductionRuntimePersistenceProfile.MongoRedis)
            {
                return;
            }

            settings["AiEngine:Snapshots:Enabled"] = "true";
            settings["AiEngine:Snapshots:Mongo:Enabled"] = "true";

            settings["AiRuntimeProcessHostCreation:EnvironmentVariables:AiPayloadStore__Enabled"] = "true";
            settings["AiRuntimeProcessHostCreation:EnvironmentVariables:AiPayloadStore__Provider"] = "mongo-redis";
            settings["AiRuntimeProcessHostCreation:EnvironmentVariables:AiPayloadStore__RequireReplaySafePayloads"] = "true";

            settings["AiDecisionLedger:Provider"] = "mongo";
            settings["AiObservability:Ledger:Provider"] = "mongo";

            settings["AiRuntimeProcessHostCreation:EnvironmentVariables:AiDecisionLedger__Provider"] = "mongo";
            settings["AiRuntimeProcessHostCreation:EnvironmentVariables:AiObservability__Ledger__Provider"] = "mongo";

            settings["AiExecutionReplay:MetadataStore:Provider"] = "mongo";
            settings["AiExecutionReplay:MetadataStore:Mongo:CollectionName"] = "ai_execution_replay_metadata";

            settings["AiRuntimeProcessHostCreation:EnvironmentVariables:AiExecutionReplay__MetadataStore__Provider"] = "mongo";
            settings["AiRuntimeProcessHostCreation:EnvironmentVariables:AiExecutionReplay__MetadataStore__Mongo__CollectionName"] = "ai_execution_replay_metadata";
        }

        /// <summary>
        /// Applies durable observability settings for the parent MCP host and child runtime processes.
        /// </summary>
        /// <param name="settings">The settings dictionary to mutate.</param>
        /// <param name="scenario">The production runtime scenario definition.</param>
        /// <remarks>
        /// Direct trace queries must work across process boundaries. The runtime
        /// child process writes trace records to the durable runtime trace store,
        /// while the parent MCP process reads them through the trace timeline query
        /// abstraction.
        /// </remarks>
        private static void ApplyObservabilitySettings(
            Dictionary<string, string?> settings,
            ProductionRuntimeScenarioDefinition scenario)
        {
            if (scenario.ObservabilityProfile != ProductionRuntimeObservabilityProfile.DurableMongo)
            {
                return;
            }

            settings["AiEngine:Observability:EnableTracing"] = "true";
            settings["AiEngine:Observability:EnableInMemoryRecording"] = "true";
            settings["AiEngine:Observability:Tracing:Mode"] = "Mongo";
            settings["AiEngine:Observability:Tracing:MongoCollectionName"] = "ai_runtime_traces";

            settings["AiRuntimeProcessHostCreation:EnvironmentVariables:AiEngine__Observability__EnableTracing"] = "true";
            settings["AiRuntimeProcessHostCreation:EnvironmentVariables:AiEngine__Observability__EnableInMemoryRecording"] = "true";
            settings["AiRuntimeProcessHostCreation:EnvironmentVariables:AiEngine__Observability__Tracing__Mode"] = "Mongo";
            settings["AiRuntimeProcessHostCreation:EnvironmentVariables:AiEngine__Observability__Tracing__MongoCollectionName"] = "ai_runtime_traces";
        }

        /// <summary>
        /// Applies one setting to the parent MCP host and to process-host child environment variables.
        /// </summary>
        /// <param name="settings">The settings dictionary to mutate.</param>
        /// <param name="key">The parent configuration key.</param>
        /// <param name="value">The setting value.</param>
        /// <remarks>
        /// Parent host settings use colon-separated configuration keys. Child
        /// process environment variables use double underscores because they are
        /// passed through the process-host creation options.
        /// </remarks>
        private static void ApplyParentAndProcessSetting(
            Dictionary<string, string?> settings,
            string key,
            string value)
        {
            settings[key] = value;
            settings[$"AiRuntimeProcessHostCreation:EnvironmentVariables:{key.Replace(":", "__", StringComparison.Ordinal)}"] = value;
        }
    }
}