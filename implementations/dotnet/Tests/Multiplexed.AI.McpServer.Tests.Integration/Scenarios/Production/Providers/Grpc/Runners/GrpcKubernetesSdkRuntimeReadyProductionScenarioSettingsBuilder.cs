using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Runners
{
    /// <summary>
    /// Builds MCP host settings for production gRPC Kubernetes SDK runtime-readiness scenarios.
    /// </summary>
    /// <remarks>
    /// This scenario keeps gRPC as the runtime provider and command transport.
    /// Kubernetes owns runtime host lifecycle creation.
    /// Runtime readiness is enabled to prove the Kubernetes pod starts, registers capacity,
    /// and becomes visible to the control plane before scale-out is considered fulfilled.
    /// </remarks>
    internal static class GrpcKubernetesSdkRuntimeReadyProductionScenarioSettingsBuilder
    {
        /// <summary>
        /// Builds the complete settings dictionary for a production gRPC Kubernetes SDK runtime-readiness scenario.
        /// </summary>
        /// <param name="scenario">The production runtime scenario definition.</param>
        /// <param name="controlPlaneId">The resolved control-plane identifier.</param>
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

            ApplyRuntimeReadinessSettings(
                settings,
                controlPlaneId);

            Console.WriteLine(
                "[RUNTIME READY BUILDER FINAL] ScenarioDebug='{0}', HostCreationMode='{1}', ClientMode='{2}', GrpcRequireReadiness='{3}', KubernetesRequireRuntimeReadiness='{4}', RuntimeImage='{5}', ImagePullPolicy='{6}'",
                settings.GetValueOrDefault("ScenarioDebug:Profile"),
                settings.GetValueOrDefault("AiGrpcRuntimeScaleOut:HostCreationMode"),
                settings.GetValueOrDefault("AiKubernetesRuntimeHost:ClientMode"),
                settings.GetValueOrDefault("AiGrpcRuntimeScaleOut:RequireReadiness"),
                settings.GetValueOrDefault("AiKubernetesRuntimeHost:RequireRuntimeReadiness"),
                settings.GetValueOrDefault("AiKubernetesRuntimeHost:RuntimeImage"),
                settings.GetValueOrDefault("AiKubernetesRuntimeHost:ImagePullPolicy"));

            return settings;
        }

        /// <summary>
        /// Applies Kubernetes SDK runtime readiness settings while preserving gRPC provider and transport semantics.
        /// </summary>
        /// <param name="settings">The settings dictionary to mutate.</param>
        /// <param name="controlPlaneId">The resolved control-plane identifier.</param>
        private static void ApplyRuntimeReadinessSettings(
            Dictionary<string, string?> settings,
            string controlPlaneId)
        {
            settings["ScenarioDebug:Profile"] = "GrpcKubernetesSdkRuntimeReady";

            settings["AiGrpcRuntimeScaleOut:Enabled"] = "true";
            settings["AiGrpcRuntimeScaleOut:Mode"] = "HostManager";
            settings["AiGrpcRuntimeScaleOut:HostCreationMode"] = "Kubernetes";
            settings["AiGrpcRuntimeScaleOut:RequireReadiness"] = "true";

            settings["AiGrpcRuntimeScaleOut:ReadinessTimeoutSeconds"] = "60";
            settings["AiGrpcRuntimeScaleOut:ReadinessPollIntervalMilliseconds"] = "1000";
            settings["AiKubernetesRuntimeHost:ReadinessTimeout"] = "00:00:59";
            settings["AiKubernetesRuntimeHost:ReadinessPollInterval"] = "00:00:01";

            settings["AiKubernetesRuntimeHost:Enabled"] = "true";
            settings["AiKubernetesRuntimeHost:ClientMode"] = "KubernetesSdk";
            settings["AiKubernetesRuntimeHost:RequireRuntimeReadiness"] = "true";
            settings["AiKubernetesRuntimeHost:Namespace"] = "ai-runtime";
            settings["AiKubernetesRuntimeHost:RuntimeImage"] = "multiplexed-ai-runtime:k8s-debug-008";
            settings["AiKubernetesRuntimeHost:ImagePullPolicy"] = "Never";
            settings["AiKubernetesRuntimeHost:ContainerName"] = "runtime-instance";
            settings["AiKubernetesRuntimeHost:ContainerPort"] = "8080";
            settings["AiKubernetesRuntimeHost:PodNamePrefix"] = "rt";
            settings["AiKubernetesRuntimeHost:TransportName"] = "grpc";
            settings["AiKubernetesRuntimeHost:UseServicePerRuntime"] = "true";
            settings["AiKubernetesRuntimeHost:DeleteResourcesOnFailure"] = "true";
            settings["AiKubernetesRuntimeHost:StartupTimeout"] = "00:00:30";


            

            settings["AiLocalRuntimeInstancePool:Enabled"] = "false";

            settings["AiRuntimeInstanceRegistration:ProviderName"] = "grpc";
            settings["AiRuntimeInstanceRegistration:HeartbeatInterval"] = "00:00:05";
            settings["AiRuntimeInstanceRegistration:ProviderMetadata:controlPlaneId"] = controlPlaneId;
            settings["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "grpc";
            settings["AiRuntimeInstanceRegistration:ProviderMetadata:transport.name"] = "grpc";
            settings["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "grpc";

            settings["AiKubernetesRuntimeHost:EnvironmentVariables:OPENAI_API_KEY"] = "demo-local-kubernetes-not-used";

            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__HeartbeatInterval"] = "00:00:05";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__ProviderName"] = "grpc";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__TransportName"] = "grpc";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__Role"] = "Runtime";

            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__Metadata__controlPlaneId"] = controlPlaneId;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__Metadata__control-plane.id"] = controlPlaneId;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__Metadata__controlplane.id"] = controlPlaneId;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__Metadata__runtime.controlPlaneId"] = controlPlaneId;

            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__ProviderMetadata__controlPlaneId"] = controlPlaneId;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__ProviderMetadata__control-plane.id"] = controlPlaneId;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__ProviderMetadata__controlplane.id"] = controlPlaneId;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__ProviderMetadata__runtime.controlPlaneId"] = controlPlaneId;
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__ProviderMetadata__provider.name"] = "grpc";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__ProviderMetadata__transport.name"] = "grpc";

            settings["AiKubernetesRuntimeHost:EnvironmentVariables:Redis__ConnectionString"] = "host.minikube.internal:6379,abortConnect=false";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:ConnectionStrings__Redis"] = "host.minikube.internal:6379,abortConnect=false";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:MultiplexedRbac__Redis__ConnectionString"] = "host.minikube.internal:6379,abortConnect=false";

            settings["AiKubernetesRuntimeHost:EnvironmentVariables:Mongo__ConnectionString"] = "mongodb://host.minikube.internal:27017/?directConnection=true";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:MongoDb__ConnectionString"] = "mongodb://host.minikube.internal:27017/?directConnection=true";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:ConnectionStrings__Mongo"] = "mongodb://host.minikube.internal:27017/?directConnection=true";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:ConnectionStrings__MongoDb"] = "mongodb://host.minikube.internal:27017/?directConnection=true";
        }
    }
}