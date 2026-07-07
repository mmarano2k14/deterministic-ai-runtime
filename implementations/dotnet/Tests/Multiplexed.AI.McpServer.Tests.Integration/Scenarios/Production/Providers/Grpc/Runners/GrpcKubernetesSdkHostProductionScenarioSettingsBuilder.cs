using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Runners
{
    /// <summary>
    /// Builds MCP host settings for production gRPC Kubernetes SDK host-manager scenarios.
    /// </summary>
    /// <remarks>
    /// This builder reuses the fake Kubernetes host-manager settings and switches only the Kubernetes
    /// host client mode to the real Kubernetes SDK client.
    ///
    /// gRPC remains the runtime provider and command transport.
    /// Kubernetes owns runtime host lifecycle creation.
    /// Runtime registry readiness remains disabled for this first SDK lifecycle proof.
    /// </remarks>
    internal static class GrpcKubernetesSdkHostProductionScenarioSettingsBuilder
    {
        /// <summary>
        /// Builds the complete settings dictionary for a production gRPC Kubernetes SDK host-manager scenario.
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
                GrpcKubernetesHostProductionScenarioSettingsBuilder.Build(
                    scenario,
                    controlPlaneId,
                    runtimeHostAssemblyPath);

            ApplyKubernetesSdkSettings(
                settings);

            return settings;
        }

        private static void ApplyKubernetesSdkSettings(
            Dictionary<string, string?> settings)
        {
            settings["AiGrpcRuntimeScaleOut:Enabled"] = "true";
            settings["AiGrpcRuntimeScaleOut:Mode"] = "HostManager";
            settings["AiGrpcRuntimeScaleOut:HostCreationMode"] = "Kubernetes";
            settings["AiGrpcRuntimeScaleOut:RequireReadiness"] = "false";

            settings["AiKubernetesRuntimeHost:Enabled"] = "true";
            settings["AiKubernetesRuntimeHost:ClientMode"] = "KubernetesSdk";
            settings["AiKubernetesRuntimeHost:RequireRuntimeReadiness"] = "false";
            settings["AiKubernetesRuntimeHost:Namespace"] = "ai-runtime";
            settings["AiKubernetesRuntimeHost:RuntimeImage"] = "multiplexed-ai-runtime:k8s-debug-003";
            settings["AiKubernetesRuntimeHost:ImagePullPolicy"] = "Never";
            settings["AiKubernetesRuntimeHost:ContainerName"] = "runtime-instance";
            settings["AiKubernetesRuntimeHost:ContainerPort"] = "8080";
            settings["AiKubernetesRuntimeHost:PodNamePrefix"] = "rt";
            settings["AiKubernetesRuntimeHost:TransportName"] = "grpc";
            settings["AiKubernetesRuntimeHost:UseServicePerRuntime"] = "true";
            settings["AiKubernetesRuntimeHost:DeleteResourcesOnFailure"] = "true";
            settings["AiKubernetesRuntimeHost:StartupTimeout"] = "00:00:30";
            settings["AiKubernetesRuntimeHost:ReadinessTimeout"] = "00:00:30";
            settings["AiKubernetesRuntimeHost:ReadinessPollInterval"] = "00:00:00.500";

            // Control-plane/test host only. This does not disable the pool inside the Kubernetes pod.
            settings["AiLocalRuntimeInstancePool:Enabled"] = "false";

            settings["OPENAI_API_KEY"] = "demo-local-kubernetes-not-used";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:OPENAI_API_KEY"] = "demo-local-kubernetes-not-used";

            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__ProviderName"] = "grpc";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__TransportName"] = "grpc";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__Role"] = "Runtime";
            settings["AiKubernetesRuntimeHost:EnvironmentVariables:AiRuntimeInstanceRegistration__HeartbeatInterval"] = "00:00:05";

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