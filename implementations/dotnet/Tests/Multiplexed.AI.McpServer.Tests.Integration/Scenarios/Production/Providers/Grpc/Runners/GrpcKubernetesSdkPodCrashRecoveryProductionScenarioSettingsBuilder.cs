using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Runners
{
    /// <summary>
    /// Builds MCP host settings for production gRPC Kubernetes SDK pod crash recovery scenarios.
    /// </summary>
    /// <remarks>
    /// This builder intentionally keeps the same Kubernetes SDK host baseline as the runtime-readiness
    /// scenario because that configuration is already proven to create a pod, register capacity,
    /// expose a routable runtime endpoint, dispatch real work, and execute at least one DAG step.
    ///
    /// The crash recovery scenario should first preserve that known-good Kubernetes dispatch path.
    /// Recovery-specific behavior must then be validated by the scenario itself rather than by
    /// importing process-host child-environment assumptions into the Kubernetes pod.
    /// </remarks>
    public static class GrpcKubernetesSdkPodCrashRecoveryProductionScenarioSettingsBuilder
    {
        /// <summary>
        /// Builds the complete settings dictionary for a production gRPC Kubernetes SDK pod crash recovery scenario.
        /// </summary>
        /// <param name="scenario">The production runtime scenario definition.</param>
        /// <param name="controlPlaneId">The resolved logical control-plane identifier.</param>
        /// <param name="runtimeHostAssemblyPath">The runtime host assembly path kept for compatibility with shared settings.</param>
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
                GrpcKubernetesSdkHostProductionScenarioSettingsBuilder.Build(
                    scenario,
                    controlPlaneId,
                    runtimeHostAssemblyPath);

            ApplyPodCrashRecoverySettings(
                settings,
                controlPlaneId);

            WritePodCrashRecoverySettingsDebug(
                settings);

            return settings;
        }

        /// <summary>
        /// Applies Kubernetes SDK pod crash recovery settings while preserving the known-good
        /// Kubernetes dispatch and readiness configuration.
        /// </summary>
        /// <param name="settings">The settings dictionary to mutate.</param>
        /// <param name="controlPlaneId">The resolved logical control-plane identifier.</param>
        private static void ApplyPodCrashRecoverySettings(
            Dictionary<string, string?> settings,
            string controlPlaneId)
        {
            settings["ScenarioDebug:Profile"] = "GrpcKubernetesSdkPodCrashRecovery";

            settings["AiMcpHost:Mode"] = "ControlPlaneWithGrpcRuntimeInstances";

            settings["AiRuntimeScaleOutRequestWatcher:Enabled"] = "true";

            settings["AiHttpRuntimeScaleOut:Enabled"] = "false";

            settings["AiGrpcRuntimeScaleOut:Enabled"] = "true";
            settings["AiGrpcRuntimeScaleOut:Mode"] = "HostManager";
            settings["AiGrpcRuntimeScaleOut:HostCreationMode"] = KubernetesSdkScenarioConstants.HostCreationMode;
            settings["AiGrpcRuntimeScaleOut:RequireReadiness"] = "true";
            settings["AiGrpcRuntimeScaleOut:ReadinessTimeoutSeconds"] = KubernetesSdkScenarioConstants.ScaleOutReadinessTimeoutSeconds;
            settings["AiGrpcRuntimeScaleOut:ReadinessPollIntervalMilliseconds"] = KubernetesSdkScenarioConstants.ScaleOutReadinessPollIntervalMilliseconds;

            settings["AiKubernetesRuntimeHost:Enabled"] = "true";
            settings["AiKubernetesRuntimeHost:ClientMode"] = KubernetesSdkScenarioConstants.ClientMode;
            settings["AiKubernetesRuntimeHost:RequireRuntimeReadiness"] = "true";
            settings["AiKubernetesRuntimeHost:ReadinessTimeout"] = KubernetesSdkScenarioConstants.RuntimeReadinessTimeout;
            settings["AiKubernetesRuntimeHost:ReadinessPollInterval"] = KubernetesSdkScenarioConstants.RuntimeReadinessPollInterval;
            settings["AiKubernetesRuntimeHost:Namespace"] = KubernetesSdkScenarioConstants.Namespace;
            settings["AiKubernetesRuntimeHost:RuntimeImage"] = KubernetesSdkScenarioConstants.RuntimeImage;
            settings["AiKubernetesRuntimeHost:ImagePullPolicy"] = KubernetesSdkScenarioConstants.ImagePullPolicy;
            settings["AiKubernetesRuntimeHost:ContainerName"] = KubernetesSdkScenarioConstants.ContainerName;
            settings["AiKubernetesRuntimeHost:ContainerPort"] = KubernetesSdkScenarioConstants.ContainerPort;
            settings["AiKubernetesRuntimeHost:PodNamePrefix"] = KubernetesSdkScenarioConstants.PodNamePrefix;
            settings["AiKubernetesRuntimeHost:TransportName"] = "grpc";
            settings["AiKubernetesRuntimeHost:UseServicePerRuntime"] = "true";
            settings["AiKubernetesRuntimeHost:DeleteResourcesOnFailure"] = "true";
            settings["AiKubernetesRuntimeHost:StartupTimeout"] = KubernetesSdkScenarioConstants.StartupTimeout;
            settings["AiKubernetesRuntimeHost:UsePortForwardTransportEndpoint"] = "true";
            settings["AiKubernetesRuntimeHost:PortForwardLocalPort"] = "0";
            settings["AiKubernetesRuntimeHost:PublishNodePortTransportEndpoint"] = "true";
            settings["AiKubernetesRuntimeHost:NodePortHost"] = KubernetesSdkScenarioConstants.NodePortHost;
            settings["AiKubernetesRuntimeHost:KubectlPath"] = KubernetesSdkScenarioConstants.KubectlPath;

            settings["AiLocalRuntimeInstancePool:Enabled"] = "false";

            ApplyControlPlanePersistenceSettings(
                settings);

            settings["AiRuntimeInstanceRegistration:ProviderName"] = "grpc";
            settings["AiRuntimeInstanceRegistration:HeartbeatInterval"] = "00:00:05";
            settings["AiRuntimeInstanceRegistration:ProviderMetadata:controlPlaneId"] = controlPlaneId;
            settings["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "grpc";
            settings["AiRuntimeInstanceRegistration:ProviderMetadata:transport.name"] = "grpc";
            settings["AiRuntimeInstanceRegistration:Metadata:controlPlaneId"] = controlPlaneId;
            settings["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "grpc";
            settings["AiRuntimeInstanceRegistration:Metadata:transport.name"] = "grpc";
            settings["AiRuntimeInstanceRegistration:Metadata:hostType"] = "control-plane-with-grpc-kubernetes-host";
            settings["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-grpc-kubernetes-pod-crash-recovery";

            ApplyRuntimePodIdentitySettings(
                settings,
                controlPlaneId);

            ApplyRuntimePodConnectivitySettings(
                settings);

            ApplyRuntimePodPersistenceSettings(
                settings);

            ApplyRuntimePodGrpcTransportSettings(
                settings);
        }

        /// <summary>
        /// Aligns control-plane snapshot persistence with the MongoDB database
        /// used by Kubernetes runtime pods.
        /// </summary>
        /// <param name="settings">The settings dictionary to mutate.</param>
        private static void ApplyControlPlanePersistenceSettings(
            Dictionary<string, string?> settings)
        {
            settings["Mongo:DatabaseName"] =
                KubernetesSdkScenarioConstants.MongoDatabaseName;

            settings["AiEngine:Snapshots:Enabled"] = "true";
            settings["AiEngine:Snapshots:Mongo:Enabled"] = "true";
            settings["AiEngine:Snapshots:Mongo:DatabaseName"] =
                KubernetesSdkScenarioConstants.MongoDatabaseName;
            settings["AiEngine:Snapshots:Mongo:CollectionName"] =
                KubernetesSdkScenarioConstants.SnapshotCollectionName;

            settings["AiExecutionReplay:MetadataStore:Provider"] = "mongo";
            settings["AiExecutionReplay:MetadataStore:Mongo:DatabaseName"] =
                KubernetesSdkScenarioConstants.MongoDatabaseName;
            settings["AiExecutionReplay:MetadataStore:Mongo:CollectionName"] =
                "ai_execution_replay_metadata";

            settings["AiDecisionLedger:Provider"] = "mongo";
            settings["AiObservability:Ledger:Provider"] = "mongo";
        }

        /// <summary>
        /// Applies RuntimeInstanceOnly identity settings to Kubernetes runtime pods.
        /// </summary>
        /// <param name="settings">The settings dictionary to mutate.</param>
        /// <param name="controlPlaneId">The resolved logical control-plane identifier.</param>
        private static void ApplyRuntimePodIdentitySettings(
            Dictionary<string, string?> settings,
            string controlPlaneId)
        {
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:OPENAI_API_KEY"] = KubernetesSdkScenarioConstants.OpenAiApiKey;

            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__HeartbeatInterval"] = "00:00:05";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__ProviderName"] = "grpc";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__TransportName"] = "grpc";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__Role"] = "Runtime";

            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__ControlPlaneId"] = controlPlaneId;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AI_CONTROL_PLANE_ID"] = controlPlaneId;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:CONTROL_PLANE_ID"] = controlPlaneId;

            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__Metadata__controlPlaneId"] = controlPlaneId;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__Metadata__control-plane.id"] = controlPlaneId;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__Metadata__controlplane.id"] = controlPlaneId;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__Metadata__runtime.controlPlaneId"] = controlPlaneId;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__Metadata__provider.name"] = "grpc";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__Metadata__transport.name"] = "grpc";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__Metadata__host.creation.mode"] = KubernetesSdkScenarioConstants.HostCreationMode;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__Metadata__host.provider"] = KubernetesSdkScenarioConstants.HostProvider;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__Metadata__hostType"] = "runtime-instance-only-grpc-kubernetes";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__Metadata__deployment"] = "test-grpc-runtime-kubernetes-pod-crash-recovery";

            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__ProviderMetadata__controlPlaneId"] = controlPlaneId;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__ProviderMetadata__control-plane.id"] = controlPlaneId;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__ProviderMetadata__controlplane.id"] = controlPlaneId;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__ProviderMetadata__runtime.controlPlaneId"] = controlPlaneId;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__ProviderMetadata__provider.name"] = "grpc";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__ProviderMetadata__transport.name"] = "grpc";
        }

        /// <summary>
        /// Applies Redis and Mongo connectivity settings used from inside Kubernetes runtime pods.
        /// </summary>
        /// <param name="settings">The settings dictionary to mutate.</param>
        private static void ApplyRuntimePodConnectivitySettings(
            Dictionary<string, string?> settings)
        {
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:Redis__ConnectionString"] = KubernetesSdkScenarioConstants.RedisConnectionString;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:ConnectionStrings__Redis"] = KubernetesSdkScenarioConstants.RedisConnectionString;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:MultiplexedRbac__Redis__ConnectionString"] = KubernetesSdkScenarioConstants.RedisConnectionString;

            settings["AiKubernetesRuntimeHost:EnvironmentVariables:Mongo__ConnectionString"] = KubernetesSdkScenarioConstants.MongoConnectionString;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:MongoDb__ConnectionString"] = KubernetesSdkScenarioConstants.MongoConnectionString;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:ConnectionStrings__Mongo"] = KubernetesSdkScenarioConstants.MongoConnectionString;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:ConnectionStrings__MongoDb"] = KubernetesSdkScenarioConstants.MongoConnectionString;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiMongo__ConnectionString"] = KubernetesSdkScenarioConstants.MongoConnectionString;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiMongoDb__ConnectionString"] = KubernetesSdkScenarioConstants.MongoConnectionString;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiExecutionSnapshotStore__ConnectionString"] = KubernetesSdkScenarioConstants.MongoConnectionString;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiExecutionSnapshotStore__MongoConnectionString"] = KubernetesSdkScenarioConstants.MongoConnectionString;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiExecutionSnapshots__Mongo__ConnectionString"] = KubernetesSdkScenarioConstants.MongoConnectionString;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeExecution__Mongo__ConnectionString"] = KubernetesSdkScenarioConstants.MongoConnectionString;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimePersistence__Mongo__ConnectionString"] = KubernetesSdkScenarioConstants.MongoConnectionString;
        }

        /// <summary>
        /// Applies snapshot persistence settings used by Kubernetes runtime pods.
        /// </summary>
        /// <param name="settings">The settings dictionary to mutate.</param>
        private static void ApplyRuntimePodPersistenceSettings(
            Dictionary<string, string?> settings)
        {
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiExecutionSnapshotMongo__Enabled"] = "true";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiExecutionSnapshotMongo__ConnectionString"] = KubernetesSdkScenarioConstants.MongoConnectionString;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiExecutionSnapshotMongo__DatabaseName"] = KubernetesSdkScenarioConstants.MongoDatabaseName;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiExecutionSnapshotMongo__CollectionName"] = KubernetesSdkScenarioConstants.SnapshotCollectionName;

            settings["AiKubernetesRuntimeHost:EnvironmentVariables:Snapshots__Enabled"] = "true";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:Snapshots__Mongo__Enabled"] = "true";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:Snapshots__Mongo__ConnectionString"] = KubernetesSdkScenarioConstants.MongoConnectionString;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:Snapshots__Mongo__DatabaseName"] = KubernetesSdkScenarioConstants.MongoDatabaseName;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:Snapshots__Mongo__CollectionName"] = KubernetesSdkScenarioConstants.SnapshotCollectionName;

            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiEngine__Snapshots__Enabled"] = "true";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiEngine__Snapshots__Mongo__Enabled"] = "true";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiEngine__Snapshots__Mongo__ConnectionString"] = KubernetesSdkScenarioConstants.MongoConnectionString;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiEngine__Snapshots__Mongo__DatabaseName"] = KubernetesSdkScenarioConstants.MongoDatabaseName;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiEngine__Snapshots__Mongo__CollectionName"] = KubernetesSdkScenarioConstants.SnapshotCollectionName;

            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiExecutionReplay__MetadataStore__Provider"] = "mongo";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiExecutionReplay__MetadataStore__Mongo__DatabaseName"] = KubernetesSdkScenarioConstants.MongoDatabaseName;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiExecutionReplay__MetadataStore__Mongo__CollectionName"] = "ai_execution_replay_metadata";

            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiDecisionLedger__Provider"] = "mongo";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiObservability__Ledger__Provider"] = "mongo";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:Mongo__DatabaseName"] = KubernetesSdkScenarioConstants.MongoDatabaseName;
        }

        /// <summary>
        /// Applies gRPC transport settings used by Kubernetes runtime pods.
        /// </summary>
        /// <param name="settings">The settings dictionary to mutate.</param>
        private static void ApplyRuntimePodGrpcTransportSettings(
            Dictionary<string, string?> settings)
        {
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:Kestrel__EndpointDefaults__Protocols"] = KubernetesSdkScenarioConstants.KestrelEndpointProtocols;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:ASPNETCORE_URLS"] = KubernetesSdkScenarioConstants.AspNetCoreUrls;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:DOTNET_ENVIRONMENT"] = "Production";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:ASPNETCORE_ENVIRONMENT"] = "Production";

            settings["AiGrpcRuntimeInstanceProvider:DispatchTimeout"] = "00:00:30";
            settings["AiGrpcRuntimeInstanceProvider:EnableCircuitBreaker"] = "false";
            settings["AiGrpcRuntimeInstanceProvider:CircuitBreakerFailureThreshold"] = "100";
        }

        /// <summary>
        /// Writes temporary gRPC Kubernetes pod crash recovery settings diagnostics.
        /// </summary>
        /// <param name="settings">The settings dictionary.</param>
        private static void WritePodCrashRecoverySettingsDebug(
            Dictionary<string, string?> settings)
        {
            foreach (var setting in settings.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (setting.Key.Contains("Kubernetes", StringComparison.OrdinalIgnoreCase) ||
                    setting.Key.Contains("Provider", StringComparison.OrdinalIgnoreCase) ||
                    setting.Key.Contains("ScaleOut", StringComparison.OrdinalIgnoreCase) ||
                    setting.Key.Contains("Snapshot", StringComparison.OrdinalIgnoreCase) ||
                    setting.Key.Contains("Mongo", StringComparison.OrdinalIgnoreCase) ||
                    setting.Key.Contains("Redis", StringComparison.OrdinalIgnoreCase) ||
                    setting.Key.Contains("Tenant", StringComparison.OrdinalIgnoreCase) ||
                    setting.Key.Contains("Kestrel", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(
                        $"[GRPC K8S POD CRASH SETTINGS DEBUG] {setting.Key}='{setting.Value}'");
                }
            }
        }
    }
}