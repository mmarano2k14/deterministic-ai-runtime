using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.KubernetesPool;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Runners
{
    /// <summary>
    /// Builds MCP host settings for the real gRPC Kubernetes Runtime Pool crash-recovery scenario.
    /// </summary>
    internal static class GrpcKubernetesRuntimePoolCrashRecoveryProductionScenarioSettingsBuilder
    {
        /// <summary>
        /// Builds the complete settings dictionary for the bounded all-in-one Runtime Pool scenario.
        /// </summary>
        /// <param name="scenario">The production runtime scenario definition.</param>
        /// <param name="controlPlaneId">The resolved control-plane identifier.</param>
        /// <param name="runtimeHostAssemblyPath">The runtime host assembly path retained by the shared settings contract.</param>
        /// <param name="profile">The Runtime Pool scenario profile.</param>
        /// <returns>The complete MCP host settings dictionary.</returns>
        public static Dictionary<string, string?> Build(
            ProductionRuntimeScenarioDefinition scenario,
            string controlPlaneId,
            string runtimeHostAssemblyPath,
            IRuntimePoolCrashRecoveryScenarioRuntimeProfile profile)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeHostAssemblyPath);
            ArgumentNullException.ThrowIfNull(profile);

            var settings =
                GrpcProcessHostProductionScenarioSettingsBuilder.Build(
                    scenario,
                    controlPlaneId,
                    runtimeHostAssemblyPath);

            var poolId =
                RuntimePoolCrashRecoveryScenarioIdentity.CreatePoolId(
                    profile.PoolIdPrefix,
                    controlPlaneId);

            ValidateProfile(profile);

            ApplyGrpcKubernetesPoolScaleOutSettings(
                settings,
                poolId,
                profile);

            ApplyRuntimePoolSettings(
                settings,
                poolId,
                profile.CrashRecoveryPlan);

            ApplyRuntimePoolHostSettings(
                settings);

            WriteRuntimePoolTransportSettingsDebug(
                settings);

            return settings;
        }

        private static void ValidateProfile(
            IRuntimePoolCrashRecoveryScenarioRuntimeProfile profile)
        {
            if (profile.CapacityTopologyMode !=
                AiRuntimeCapacityTopologyMode.KubernetesPool)
            {
                throw new InvalidOperationException(
                    "The gRPC Kubernetes Runtime Pool settings builder requires the KubernetesPool capacity topology.");
            }

            if (profile.HostCreationMode !=
                AiRuntimeHostCreationMode.KubernetesPool)
            {
                throw new InvalidOperationException(
                    "The gRPC Kubernetes Runtime Pool settings builder requires the KubernetesPool host creation mode.");
            }
        }

        private static void ApplyGrpcKubernetesPoolScaleOutSettings(
            Dictionary<string, string?> settings,
            string poolId,
            IRuntimePoolCrashRecoveryScenarioRuntimeProfile profile)
        {
            settings["AiGrpcRuntimeScaleOut:CapacityTopologyMode"] =
                profile.CapacityTopologyMode.ToString();
            settings["AiGrpcRuntimeScaleOut:HostCreationMode"] =
                profile.HostCreationMode.ToString();
            settings["AiGrpcRuntimeScaleOut:PoolId"] = poolId;
            settings["AiGrpcRuntimeScaleOut:RequireReadiness"] = "true";
            settings["AiGrpcRuntimeScaleOut:ReadinessTimeoutSeconds"] = "180";
            settings["AiGrpcRuntimeScaleOut:ReadinessPollIntervalMilliseconds"] = "250";
            settings["AiGrpcRuntimeScaleOut:MaxConcurrentProcessHostStartups"] = "0";
            settings["AiGrpcRuntimeScaleOut:DefaultRuntimeInstanceIdPrefix"] =
                string.Concat(poolId, "-primary");
            settings["AiGrpcRuntimeScaleOut:EndpointTemplate"] =
                "http://127.0.0.1";
            settings["AiRuntimeScaleOutRequestWatcher:RequestProcessingCoordinationKey"] =
                string.Concat("grpc-kubernetes-pool-", poolId);
            settings["AiRuntimeScaleOutRequestWatcher:MaxConcurrentRequestProcessingWorkflows"] =
                "3";
            settings["AiRuntimeScaleOutRequestWatcher:MaxConcurrentRequestProcessingWorkflowsPerControlPlane"] =
                "1";
        }

        private static void ApplyRuntimePoolSettings(
            Dictionary<string, string?> settings,
            string poolId,
            RuntimePoolCrashRecoveryScenarioPlan plan)
        {
            settings["AiKubernetesRuntimePool:Enabled"] = "true";
            settings["AiKubernetesRuntimePool:PoolId"] = poolId;
            settings["AiKubernetesRuntimePool:Namespace"] =
                KubernetesRuntimePoolScenarioConstants.Namespace;
            settings["AiKubernetesRuntimePool:PodNamePrefix"] =
                "runtime-pool-7g";
            settings["AiKubernetesRuntimePool:RuntimeInstanceIdPrefix"] =
                string.Concat(poolId, "-runtime");
            settings["AiKubernetesRuntimePool:ProviderName"] = "grpc";
            settings["AiKubernetesRuntimePool:TransportName"] = "grpc";
            settings["AiKubernetesRuntimePool:InitialRuntimeInstanceCount"] =
                plan.InitialRuntimeCountPerPod.ToString();
            settings["AiKubernetesRuntimePool:MinimumRuntimeInstanceCount"] =
                plan.InitialRuntimeCountPerPod.ToString();
            settings["AiKubernetesRuntimePool:MaximumRuntimeInstanceCount"] =
                plan.MaximumRuntimeCountPerPod.ToString();
            settings["AiKubernetesRuntimePool:StartupParallelism"] = "1";
            settings["AiKubernetesRuntimePool:StableTransportPort"] = "8080";
            settings["AiKubernetesRuntimePool:ReadinessPort"] = "8081";
            settings["AiKubernetesRuntimePool:FirstChildTransportPort"] = "19080";
            settings["AiKubernetesRuntimePool:ChildTransportPortStride"] = "1";
            settings["AiKubernetesRuntimePool:ShutdownTimeoutSeconds"] = "30";
        }

        /// <summary>
        /// Writes the final transport settings after the process-host defaults have been
        /// replaced by the Kubernetes Runtime Pool contract.
        /// </summary>
        private static void WriteRuntimePoolTransportSettingsDebug(
            IReadOnlyDictionary<string, string?> settings)
        {
            Console.WriteLine(
                string.Concat(
                    "[GRPC KUBERNETES RUNTIME POOL SETTINGS] ",
                    "HostCreationMode='",
                    settings["AiGrpcRuntimeScaleOut:HostCreationMode"],
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

        private static void ApplyRuntimePoolHostSettings(
            Dictionary<string, string?> settings)
        {
            if (!settings.TryGetValue(
                    "Mongo:DatabaseName",
                    out var mongoDatabaseName)
                || string.IsNullOrWhiteSpace(mongoDatabaseName))
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
             * Reuse the existing production Kubernetes Gateway and its single
             * host-local port-forward. Every exact Pool child receives its own
             * header route to the same stable Runtime Pool Service.
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
    }
}
