using System.Globalization;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.ProcessHostPool
{
    /// <summary>
    /// Composes one MCP control plane and several external Process Hosts that share one logical
    /// ProcessPool while owning independent host incarnations.
    /// </summary>
    internal static class ProcessHostPoolProductionScenarioSettingsComposer
    {
        /// <summary>
        /// Builds control-plane settings for capacity that already exists in external Process Hosts.
        /// </summary>
        public static Dictionary<string, string?> BuildControlPlaneSettings(
            ProcessHostPoolProductionScenarioProfile profile,
            ProductionRuntimeScenarioDefinition scenario,
            string controlPlaneId,
            string runtimeHostAssemblyPath,
            int totalRuntimeCount)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeHostAssemblyPath);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalRuntimeCount);

            var settings =
                profile.BuildControlPlaneSettings(
                    scenario,
                    controlPlaneId,
                    runtimeHostAssemblyPath);

            settings["AiSharedRuntimeController:SubmitMode"] = "QueueFirst";
            settings["AiRuntimeExecutionRecoveryReconciliation:Enabled"] = "false";
            settings["AiRuntimeExecutionRecoveryReconciliation:EnableDagExecutionResume"] = "true";
            settings["AiRunAdmission:MaxInstanceCount"] =
                totalRuntimeCount.ToString(CultureInfo.InvariantCulture);

            // The external Process Hosts already own the complete bounded topology.
            settings["AiRuntimeScaleOutWatcher:Enabled"] = "false";
            settings["AiRuntimeScaleOutRequestWatcher:Enabled"] = "false";
            settings["AiHttpRuntimeScaleOut:Enabled"] = "false";
            settings["AiGrpcRuntimeScaleOut:Enabled"] = "false";
            settings["AiRuntimeProcessHostCreation:Enabled"] = "false";
            settings["AiKubernetesRuntimeHost:Enabled"] = "false";
            settings["AiKubernetesRuntimePool:Enabled"] = "false";
            settings["AiRuntimeProcessPool:Enabled"] = "false";
            settings["AiKubernetesRuntimePoolInPod:Enabled"] = "false";

            DisableLocalRuntimeCapacity(settings);

            // Preserve the real network provider. The test host must not replace it with an
            // in-memory runtime HttpClient factory.
            settings["Tests:UseRegisteringTestRuntimeHostManager"] = "false";

            // The production proof queries control-plane dispatch ledger entries through MCP.
            // GenericMcpServerTestHost otherwise replaces the durable recorder with its
            // test-only in-memory capture recorder, leaving the Mongo ledger empty for
            // synthetic control-plane-run execution identifiers.
            settings["Tests:UseCapturingLedgerRecorder"] = "false";
            settings["Tests:UseMongoRuntimeLifecycleJournal"] = "true";
            settings["AiRuntimeRecoveryForensics:StrictPersistence"] = "true";
            settings["AiRuntimePoolFailureJournal:Provider"] = "mongo";

            if (!settings.TryGetValue("Mongo:DatabaseName", out var mongoDatabaseName) ||
                string.IsNullOrWhiteSpace(mongoDatabaseName))
            {
                throw new InvalidOperationException(
                    "ProcessHostPool production proof requires an explicit Mongo:DatabaseName for the shared failure authority.");
            }

            settings["AiRuntimePoolFailureJournal:Mongo:DatabaseName"] =
                mongoDatabaseName;
            settings["AiRuntimePoolFailureJournal:Mongo:CollectionName"] =
                "ai_runtime_pool_failures";

            return settings;
        }

        /// <summary>
        /// Builds one independent RuntimeInstanceOnly parent Process Host configuration.
        /// </summary>
        public static Dictionary<string, string?> BuildProcessHostSettings(
            ProcessHostPoolProductionScenarioProfile profile,
            IReadOnlyDictionary<string, string?> controlPlaneSettings,
            string controlPlaneId,
            string poolId,
            string runtimeHostAssemblyPath,
            string stableTransportEndpoint,
            int childBasePort,
            int processHostOrdinal,
            int runtimeCountPerHost,
            ProductionTenantScenarioDefinition tenant)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(controlPlaneSettings);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeHostAssemblyPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(stableTransportEndpoint);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(childBasePort);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processHostOrdinal);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runtimeCountPerHost);
            ArgumentNullException.ThrowIfNull(tenant);

            string? Read(string key)
            {
                controlPlaneSettings.TryGetValue(key, out var value);
                return value;
            }

            var hostOrdinal =
                processHostOrdinal.ToString(
                    "000",
                    CultureInfo.InvariantCulture);

            var settings =
                new Dictionary<string, string?>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["AiMcpHost:Mode"] = "RuntimeInstanceOnly",
                    ["AiMcpHost:EnableRuntimeTool"] = "false",
                    ["AiMcpHost:EnableRuntimeQueuePump"] = "false",
                    ["AiMcpHost:EnableSharedQueuePump"] = "false",
                    ["AiMcpHost:EnableReplayTools"] = "false",
                    ["AiMcpHost:EnableObservabilityTools"] = "false",
                    ["AiRuntimeInstanceRegistration:Enabled"] = "false",
                    ["AiKubernetesRuntimePoolInPod:Enabled"] = "false",

                    ["AiRuntimeExecutionRecoveryReconciliation:Enabled"] = "false",
                    ["AiRuntimeExecutionRecoveryReconciliation:EnableDagExecutionResume"] = "true",

                    ["AiEngine:ControlPlane:ControlPlaneId"] = controlPlaneId,
                    ["AiEngine:ControlPlane:RedisDiscoveryKey"] =
                        string.Concat("multiplexed-ai:", controlPlaneId),

                    ["ConnectionStrings:Redis"] = Read("ConnectionStrings:Redis"),
                    ["ConnectionStrings:Mongo"] = Read("ConnectionStrings:Mongo"),
                    ["Mongo:DatabaseName"] = Read("Mongo:DatabaseName"),
                    ["AiRuntimePoolFailureJournal:Provider"] = "mongo",
                    ["AiRuntimePoolFailureJournal:Mongo:DatabaseName"] =
                        Read("AiRuntimePoolFailureJournal:Mongo:DatabaseName") ??
                        Read("Mongo:DatabaseName"),
                    ["AiRuntimePoolFailureJournal:Mongo:CollectionName"] =
                        "ai_runtime_pool_failures",
                    ["OpenAI:ApiKey"] = "process-host-pool-production-proof-not-used",

                    ["AiRuntimeProcessPool:Enabled"] = "true",
                    ["AiRuntimeProcessPool:PoolId"] = poolId,
                    ["AiRuntimeProcessPool:HostIdPrefix"] =
                        string.Concat(poolId, "-process-host-", hostOrdinal),
                    ["AiRuntimeProcessPool:RuntimeInstanceIdPrefix"] =
                        string.Concat(poolId, "-host-", hostOrdinal, "-runtime"),
                    ["AiRuntimeProcessPool:InitialProcessCount"] =
                        runtimeCountPerHost.ToString(CultureInfo.InvariantCulture),
                    ["AiRuntimeProcessPool:MinimumProcessCount"] =
                        runtimeCountPerHost.ToString(CultureInfo.InvariantCulture),
                    ["AiRuntimeProcessPool:MaximumProcessCount"] =
                        runtimeCountPerHost.ToString(CultureInfo.InvariantCulture),
                    ["AiRuntimeProcessPool:StartupParallelism"] =
                        Math.Clamp(runtimeCountPerHost, 1, 4)
                            .ToString(CultureInfo.InvariantCulture),
                    ["AiRuntimeProcessPool:ShutdownTimeoutSeconds"] = "30",

                    ["AiRuntimeProcessPoolRuntimeInstance:RuntimeHostAssemblyPath"] =
                        runtimeHostAssemblyPath,
                    ["AiRuntimeProcessPoolRuntimeInstance:WorkingDirectory"] =
                        Path.GetDirectoryName(runtimeHostAssemblyPath) ??
                        Environment.CurrentDirectory,
                    ["AiRuntimeProcessPoolRuntimeInstance:BasePort"] =
                        childBasePort.ToString(CultureInfo.InvariantCulture),
                    ["AiRuntimeProcessPoolRuntimeInstance:MaxPort"] =
                        checked(childBasePort + runtimeCountPerHost + 16)
                            .ToString(CultureInfo.InvariantCulture),
                    ["AiRuntimeProcessPoolRuntimeInstance:EndpointHost"] = "127.0.0.1",
                    ["AiRuntimeProcessPoolRuntimeInstance:PublishedTransportEndpoint"] =
                        stableTransportEndpoint,
                    ["AiRuntimeProcessPoolRuntimeInstance:ControlPlaneId"] =
                        controlPlaneId,
                    ["AiRuntimeProcessPoolRuntimeInstance:EnableControlPlaneDiscovery"] =
                        "false",
                    ["AiRuntimeProcessPoolRuntimeInstance:RequireControlPlaneDiscovery"] =
                        "false",
                    ["AiRuntimeProcessPoolRuntimeInstance:ProviderName"] =
                        profile.ProviderName,
                    ["AiRuntimeProcessPoolRuntimeInstance:TransportName"] =
                        profile.ProviderName,
                    ["AiRuntimeProcessPoolRuntimeInstance:RuntimeVersion"] =
                        string.Concat(
                            profile.ProviderName,
                            "-process-host-pool-production"),
                    ["AiRuntimeProcessPoolRuntimeInstance:WorkerCountPerInstance"] = "1",
                    ["AiRuntimeProcessPoolRuntimeInstance:MaxConcurrentRunsPerInstance"] = "1",
                    ["AiRuntimeProcessPoolRuntimeInstance:LocalQueueCapacity"] = "0",
                    ["AiRuntimeProcessPoolRuntimeInstance:StartupTimeout"] = "00:03:00",
                    ["AiRuntimeProcessPoolRuntimeInstance:ReadinessPollInterval"] =
                        "00:00:00.100",
                    ["AiRuntimeProcessPoolRuntimeInstance:HeartbeatInterval"] = "00:00:01",
                    ["AiRuntimeProcessPoolRuntimeInstance:StopTimeoutSeconds"] = "15",

                    ["AiRuntimeProcessPoolRuntimeInstance:ExecutionContextSnapshot:ContextKey"] =
                        string.Concat(poolId, ":host:", hostOrdinal),
                    ["AiRuntimeProcessPoolRuntimeInstance:ExecutionContextSnapshot:Project"] =
                        string.Concat(
                            profile.ProviderName,
                            "-process-host-pool-production"),
                    ["AiRuntimeProcessPoolRuntimeInstance:ExecutionContextSnapshot:UserId"] =
                        "system",
                    ["AiRuntimeProcessPoolRuntimeInstance:ExecutionContextSnapshot:TenantId"] =
                        tenant.TenantId,
                    ["AiRuntimeProcessPoolRuntimeInstance:ExecutionContextSnapshot:TenantGroupId"] =
                        tenant.TenantGroupId,
                    ["AiRuntimeProcessPoolRuntimeInstance:ExecutionContextSnapshot:CurrentNamespace"] =
                        "tests"
                };

            DisableLocalRuntimeCapacity(settings);

            CopyWhenPresent(
                controlPlaneSettings,
                settings,
                "AiTenantRuntimeSettings:Provider");
            CopyWhenPresent(
                controlPlaneSettings,
                settings,
                "AiTenantRuntimeSettings:Enabled");

            foreach (var pair in controlPlaneSettings)
            {
                if (pair.Key.StartsWith(
                        "AiTenantRuntimeSettings:Tenants:",
                        StringComparison.OrdinalIgnoreCase))
                {
                    settings[pair.Key] = pair.Value;
                }
            }

            return settings;
        }

        private static void DisableLocalRuntimeCapacity(
            IDictionary<string, string?> settings)
        {
            settings["AiLocalRuntimeInstancePool:Enabled"] = "false";
            settings["AiLocalRuntimeInstancePool:InstanceCount"] = "0";
            settings["AiLocalRuntimeInstancePool:WorkerCountPerInstance"] = "0";
            settings["AiLocalRuntimeInstancePool:MaxConcurrentRunsPerInstance"] = "0";
            settings["AiLocalRuntimeInstancePool:LocalQueueCapacity"] = "0";
            settings["AiLocalRuntimeInstancePool:RuntimeInstanceIdPrefix"] = "disabled";
        }

        private static void CopyWhenPresent(
            IReadOnlyDictionary<string, string?> source,
            IDictionary<string, string?> destination,
            string key)
        {
            if (source.TryGetValue(key, out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                destination[key] = value;
            }
        }
    }
}
