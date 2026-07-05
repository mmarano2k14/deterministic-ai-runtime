using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Runners
{
    /// <summary>
    /// Builds MCP host settings for production gRPC Kubernetes host-manager scenarios.
    /// </summary>
    /// <remarks>
    /// This builder intentionally reuses the gRPC process-host production settings builder
    /// for shared queue, admission, tenant runtime settings, persistence, replay, tracing,
    /// provider configuration, and control-plane gRPC mode.
    ///
    /// Only the runtime host lifecycle provider is changed here:
    ///
    /// <code>
    /// AiGrpcRuntimeScaleOut:Mode             -> HostManager
    /// AiGrpcRuntimeScaleOut:HostCreationMode -> Kubernetes
    /// AiKubernetesRuntimeHost:ClientMode     -> Fake
    /// ProviderName                           -> grpc
    /// TransportName                          -> grpc
    /// host.provider                          -> kubernetes
    /// </code>
    ///
    /// Kubernetes owns runtime lifecycle creation.
    /// gRPC remains the runtime command transport.
    /// </remarks>
    internal static class GrpcKubernetesHostProductionScenarioSettingsBuilder
    {
        /// <summary>
        /// Builds the complete settings dictionary for a production gRPC Kubernetes host-manager scenario.
        /// </summary>
        /// <param name="scenario">The production runtime scenario definition.</param>
        /// <param name="controlPlaneId">The resolved control-plane identifier.</param>
        /// <param name="runtimeHostAssemblyPath">The runtime host assembly path kept for compatibility with shared process-host settings.</param>
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
                GrpcProcessHostProductionScenarioSettingsBuilder.Build(
                    scenario,
                    controlPlaneId,
                    runtimeHostAssemblyPath);

            ApplyKubernetesHostManagerSettings(
                settings);

            WriteKubernetesSettingsDebug(
                settings);

            return settings;
        }

        /// <summary>
        /// Applies Kubernetes host-manager settings while preserving gRPC as the runtime provider and transport.
        /// </summary>
        /// <param name="settings">The settings dictionary to mutate.</param>
        private static void ApplyKubernetesHostManagerSettings(
            Dictionary<string, string?> settings)
        {
            settings["AiRuntimeScaleOutRequestWatcher:Enabled"] = "true";

            settings["AiHttpRuntimeScaleOut:Enabled"] = "false";

            settings["AiGrpcRuntimeScaleOut:Enabled"] = "true";
            settings["AiGrpcRuntimeScaleOut:Mode"] = "HostManager";
            settings["AiGrpcRuntimeScaleOut:HostCreationMode"] = "Kubernetes";
            settings["AiGrpcRuntimeScaleOut:RequireReadiness"] = "false";

            settings["AiGrpcRuntimeScaleOut:ReadinessTimeoutSeconds"] = "30";
            settings["AiGrpcRuntimeScaleOut:ReadinessPollIntervalMilliseconds"] = "100";
            settings["AiGrpcRuntimeScaleOut:DefaultRuntimeInstanceIdPrefix"] = "grpc-kubernetes-runtime";
            settings["AiGrpcRuntimeScaleOut:EndpointTemplate"] = "http://127.0.0.1:8080/{runtimeInstanceId}";

            settings["AiKubernetesRuntimeHost:Enabled"] = "true";
            settings["AiKubernetesRuntimeHost:ClientMode"] = "Fake";
            settings["AiKubernetesRuntimeHost:RequireRuntimeReadiness"] = "false";
            settings["AiKubernetesRuntimeHost:Namespace"] = "ai-runtime";
            settings["AiKubernetesRuntimeHost:RuntimeImage"] = "multiplexed-ai-runtime:test";
            settings["AiKubernetesRuntimeHost:ImagePullPolicy"] = "IfNotPresent";
            settings["AiKubernetesRuntimeHost:ContainerName"] = "runtime-instance";
            settings["AiKubernetesRuntimeHost:ContainerPort"] = "8080";
            settings["AiKubernetesRuntimeHost:PodNamePrefix"] = "grpc-kubernetes-runtime";
            settings["AiKubernetesRuntimeHost:TransportName"] = "grpc";
            settings["AiKubernetesRuntimeHost:UseServicePerRuntime"] = "true";
            settings["AiKubernetesRuntimeHost:DeleteResourcesOnFailure"] = "true";
            settings["AiKubernetesRuntimeHost:StartupTimeout"] = "00:00:30";
            settings["AiKubernetesRuntimeHost:ReadinessTimeout"] = "00:00:30";
            settings["AiKubernetesRuntimeHost:ReadinessPollInterval"] = "00:00:00.100";
        }

        /// <summary>
        /// Writes temporary Kubernetes settings diagnostics for host-manager scale-out debugging.
        /// </summary>
        /// <param name="settings">The settings dictionary.</param>
        private static void WriteKubernetesSettingsDebug(
            Dictionary<string, string?> settings)
        {
            foreach (var setting in settings.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (setting.Key.Contains("Kubernetes", StringComparison.OrdinalIgnoreCase) ||
                    setting.Key.Contains("GrpcRuntimeScaleOut", StringComparison.OrdinalIgnoreCase) ||
                    setting.Key.Contains("Provider", StringComparison.OrdinalIgnoreCase) ||
                    setting.Key.Contains("ScaleOut", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(
                        $"[GRPC KUBERNETES SETTINGS DEBUG] {setting.Key}='{setting.Value}'");
                }
            }
        }
    }
}