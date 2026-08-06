using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.KubernetesPool
{
    /// <summary>
    /// Applies the transport-neutral bounded Kubernetes Runtime Pool production settings
    /// to an already composed HTTP or gRPC process-host settings dictionary.
    /// </summary>
    internal static class KubernetesRuntimePoolProductionScenarioSettingsComposer
    {
        /// <summary>
        /// Replaces process-host topology settings with the bounded Kubernetes Runtime Pool contract.
        /// </summary>
        /// <param name="settings">The transport-specific parent settings to extend.</param>
        /// <param name="poolId">The scenario-isolated Runtime Pool identifier.</param>
        /// <param name="profile">The Runtime Pool scenario profile.</param>
        /// <param name="scaleOutSectionName">The active transport scale-out configuration section.</param>
        /// <param name="firstChildTransportPort">The first in-Pod child transport port.</param>
        /// <returns>The same settings dictionary after Runtime Pool composition.</returns>
        public static Dictionary<string, string?> Apply(
            Dictionary<string, string?> settings,
            string poolId,
            IRuntimePoolCrashRecoveryScenarioRuntimeProfile profile,
            string scaleOutSectionName,
            int firstChildTransportPort)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentException.ThrowIfNullOrWhiteSpace(scaleOutSectionName);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(firstChildTransportPort);

            ValidateProfile(profile);

            ApplyStrictCrashRecoverySettings(settings);

            ApplyKubernetesPoolScaleOutSettings(
                settings,
                poolId,
                profile,
                scaleOutSectionName);

            ApplyRuntimePoolSettings(
                settings,
                poolId,
                profile,
                firstChildTransportPort);

            ApplyRuntimePoolHostSettings(settings);

            WriteRuntimePoolTransportSettingsDebug(
                settings,
                profile,
                scaleOutSectionName);

            return settings;
        }

        private static void ValidateProfile(
            IRuntimePoolCrashRecoveryScenarioRuntimeProfile profile)
        {
            if (profile.CapacityTopologyMode !=
                AiRuntimeCapacityTopologyMode.KubernetesPool)
            {
                throw new InvalidOperationException(
                    "The Kubernetes Runtime Pool settings composer requires the KubernetesPool capacity topology.");
            }

            if (profile.HostCreationMode !=
                AiRuntimeHostCreationMode.KubernetesPool)
            {
                throw new InvalidOperationException(
                    "The Kubernetes Runtime Pool settings composer requires the KubernetesPool host creation mode.");
            }

            if (!string.Equals(
                    profile.ProviderName,
                    "grpc",
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    profile.ProviderName,
                    "http",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    string.Concat(
                        "Unsupported Kubernetes Runtime Pool transport provider '",
                        profile.ProviderName,
                        "'."));
            }
        }

        private static void ApplyStrictCrashRecoverySettings(
            Dictionary<string, string?> settings)
        {
            /*
             * Every scenario composed through this boundary is a strict crash-recovery proof.
             * Do not depend on scenario-name heuristics inherited from process-host profiles.
             */
            settings["AiRuntimeExecutionRecoveryReconciliation:EnableDagExecutionResume"] =
                "true";
        }

        private static void ApplyKubernetesPoolScaleOutSettings(
            Dictionary<string, string?> settings,
            string poolId,
            IRuntimePoolCrashRecoveryScenarioRuntimeProfile profile,
            string scaleOutSectionName)
        {
            settings[$"{scaleOutSectionName}:CapacityTopologyMode"] =
                profile.CapacityTopologyMode.ToString();
            settings[$"{scaleOutSectionName}:HostCreationMode"] =
                profile.HostCreationMode.ToString();
            settings[$"{scaleOutSectionName}:PoolId"] = poolId;
            settings[$"{scaleOutSectionName}:RequireReadiness"] = "true";
            settings[$"{scaleOutSectionName}:ReadinessTimeoutSeconds"] = "180";
            settings[$"{scaleOutSectionName}:ReadinessPollIntervalMilliseconds"] = "250";
            settings[$"{scaleOutSectionName}:MaxConcurrentProcessHostStartups"] = "0";
            settings[$"{scaleOutSectionName}:DefaultRuntimeInstanceIdPrefix"] =
                string.Concat(poolId, "-primary");
            settings[$"{scaleOutSectionName}:EndpointTemplate"] =
                "http://127.0.0.1";

            settings["AiRuntimeScaleOutRequestWatcher:RequestProcessingCoordinationKey"] =
                string.Concat(
                    profile.ProviderName,
                    "-kubernetes-pool-",
                    poolId);
            settings["AiRuntimeScaleOutRequestWatcher:MaxConcurrentRequestProcessingWorkflows"] =
                "3";
            settings["AiRuntimeScaleOutRequestWatcher:MaxConcurrentRequestProcessingWorkflowsPerControlPlane"] =
                "1";
        }

        private static void ApplyRuntimePoolSettings(
            Dictionary<string, string?> settings,
            string poolId,
            IRuntimePoolCrashRecoveryScenarioRuntimeProfile profile,
            int firstChildTransportPort)
        {
            var plan = profile.CrashRecoveryPlan;

            /*
             * AiRunAdmission counts first-class RuntimeInstance identities, not Pods.
             */
            var maximumRuntimeCapacity =
                checked(
                    plan.MaximumPodCount *
                    plan.MaximumRuntimeCountPerPod);

            settings["AiRunAdmission:MaxInstanceCount"] =
                maximumRuntimeCapacity.ToString();

            settings["AiKubernetesRuntimePool:Enabled"] = "true";
            settings["AiKubernetesRuntimePool:PoolId"] = poolId;
            settings["AiKubernetesRuntimePool:MaximumPodCount"] =
                plan.MaximumPodCount.ToString();
            settings["AiKubernetesRuntimePool:Namespace"] =
                KubernetesRuntimePoolScenarioConstants.Namespace;
            settings["AiKubernetesRuntimePool:PodNamePrefix"] =
                "runtime-pool-7g";
            settings["AiKubernetesRuntimePool:RuntimeInstanceIdPrefix"] =
                string.Concat(poolId, "-runtime");
            settings["AiKubernetesRuntimePool:ProviderName"] =
                profile.ProviderName;
            settings["AiKubernetesRuntimePool:TransportName"] =
                profile.ProviderName;
            settings["AiKubernetesRuntimePool:InitialRuntimeInstanceCount"] =
                plan.InitialRuntimeCountPerPod.ToString();
            settings["AiKubernetesRuntimePool:MinimumRuntimeInstanceCount"] =
                plan.InitialRuntimeCountPerPod.ToString();
            settings["AiKubernetesRuntimePool:MaximumRuntimeInstanceCount"] =
                plan.MaximumRuntimeCountPerPod.ToString();
            settings["AiKubernetesRuntimePool:StartupParallelism"] = "1";
            settings["AiKubernetesRuntimePool:StableTransportPort"] = "8080";
            settings["AiKubernetesRuntimePool:ReadinessPort"] = "8081";
            settings["AiKubernetesRuntimePool:FirstChildTransportPort"] =
                firstChildTransportPort.ToString();
            settings["AiKubernetesRuntimePool:ChildTransportPortStride"] = "1";
            settings["AiKubernetesRuntimePool:ShutdownTimeoutSeconds"] = "30";
        }

        private static void ApplyRuntimePoolHostSettings(
            Dictionary<string, string?> settings)
        {
            if (!settings.TryGetValue(
                    "Mongo:DatabaseName",
                    out var mongoDatabaseName) ||
                string.IsNullOrWhiteSpace(mongoDatabaseName))
            {
                throw new InvalidOperationException(
                    "The Kubernetes Runtime Pool scenario requires the parent Mongo database name before child host settings are applied.");
            }

            settings["AiKubernetesRuntimePoolHost:RuntimeImage"] =
                KubernetesRuntimePoolScenarioConstants.RuntimeImage;
            settings["AiKubernetesRuntimePoolHost:ContainerName"] =
                "runtime-pool";
            settings["AiKubernetesRuntimePoolHost:ImagePullPolicy"] = "Never";
            settings["AiKubernetesRuntimePoolHost:ClientMode"] =
                "KubernetesSdk";
            settings["AiKubernetesRuntimePoolHost:CreateService"] = "true";
            settings["AiKubernetesRuntimePoolHost:ServiceType"] = "ClusterIP";
            settings["AiKubernetesRuntimePoolHost:UseGatewayTransportEndpoint"] =
                "true";
            settings["AiKubernetesRuntimePoolHost:StartupTimeout"] =
                "00:03:00";
            settings["AiKubernetesRuntimePoolHost:ReadinessPollInterval"] =
                "00:00:01";
            settings["AiKubernetesRuntimePoolHost:RedisConnectionString"] =
                KubernetesRuntimePoolScenarioConstants.RedisConnectionString;
            settings["AiKubernetesRuntimePoolHost:MongoConnectionString"] =
                KubernetesRuntimePoolScenarioConstants.MongoConnectionString;
            settings["AiKubernetesRuntimePoolHost:MongoDatabaseName"] =
                mongoDatabaseName;
            settings["AiKubernetesRuntimePoolHost:OpenAiApiKey"] =
                "kubernetes-runtime-pool-7g-not-used";

            /*
             * Reuse the production Kubernetes Gateway and its single host-local
             * port-forward. Exact child routing is selected by the gateway header.
             */
            settings["AiKubernetesRuntimeHost:Enabled"] = "true";
            settings["AiKubernetesRuntimeHost:ClientMode"] = "KubernetesSdk";
            settings["AiKubernetesRuntimeHost:Namespace"] =
                KubernetesRuntimePoolScenarioConstants.Namespace;
            settings["AiKubernetesRuntimeHost:UseGatewayTransportEndpoint"] =
                "true";
            settings["AiKubernetesRuntimeHost:GatewayName"] =
                KubernetesSdkScenarioConstants.GatewayName;
            settings["AiKubernetesRuntimeHost:GatewayClassName"] =
                KubernetesSdkScenarioConstants.GatewayClassName;
            settings["AiKubernetesRuntimeHost:GatewayControllerName"] =
                KubernetesSdkScenarioConstants.GatewayControllerName;
            settings["AiKubernetesRuntimeHost:CreateGatewayClassWhenMissing"] =
                "true";
            settings["AiKubernetesRuntimeHost:GatewayListenerName"] =
                KubernetesSdkScenarioConstants.GatewayListenerName;
            settings["AiKubernetesRuntimeHost:GatewayPort"] =
                KubernetesSdkScenarioConstants.GatewayPort;
            settings["AiKubernetesRuntimeHost:GatewayRouteHeaderName"] =
                KubernetesSdkScenarioConstants.GatewayRouteHeaderName;
            settings["AiKubernetesRuntimeHost:CreateGatewayWhenMissing"] =
                "true";
            settings["AiKubernetesRuntimeHost:RequireGatewayProgrammed"] =
                "true";
            settings["AiKubernetesRuntimeHost:GatewayReadinessTimeout"] =
                KubernetesSdkScenarioConstants.GatewayReadinessTimeout;
            settings["AiKubernetesRuntimeHost:GatewayReadinessPollInterval"] =
                KubernetesSdkScenarioConstants.GatewayReadinessPollInterval;
            settings["AiKubernetesRuntimeHost:UsePortForwardTransportEndpoint"] =
                "true";
            settings["AiKubernetesRuntimeHost:PortForwardLocalPort"] = "0";
            settings["AiKubernetesRuntimeHost:KubectlPath"] =
                KubernetesSdkScenarioConstants.KubectlPath;
            settings["AiKubernetesRuntimeHost:PublishNodePortTransportEndpoint"] =
                "false";
        }

        private static void WriteRuntimePoolTransportSettingsDebug(
            IReadOnlyDictionary<string, string?> settings,
            IRuntimePoolCrashRecoveryScenarioRuntimeProfile profile,
            string scaleOutSectionName)
        {
            Console.WriteLine(
                string.Concat(
                    "[",
                    profile.LogPrefix,
                    " SETTINGS] HostCreationMode='",
                    settings[$"{scaleOutSectionName}:HostCreationMode"],
                    "', ServiceType='",
                    settings["AiKubernetesRuntimePoolHost:ServiceType"],
                    "', PoolUseGateway='",
                    settings["AiKubernetesRuntimePoolHost:UseGatewayTransportEndpoint"],
                    "', GatewayUseGateway='",
                    settings["AiKubernetesRuntimeHost:UseGatewayTransportEndpoint"],
                    "', UsePortForward='",
                    settings["AiKubernetesRuntimeHost:UsePortForwardTransportEndpoint"],
                    "', PublishNodePort='",
                    settings["AiKubernetesRuntimeHost:PublishNodePortTransportEndpoint"],
                    "'."));
        }
    }
}
