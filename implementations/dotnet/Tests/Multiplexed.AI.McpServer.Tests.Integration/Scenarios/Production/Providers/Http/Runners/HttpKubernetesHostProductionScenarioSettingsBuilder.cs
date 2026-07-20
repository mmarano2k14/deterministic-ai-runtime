using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Runners
{
    /// <summary>
    /// Builds MCP host settings for production HTTP Kubernetes host-manager scenarios.
    /// </summary>
    /// <remarks>
    /// This builder intentionally reuses the HTTP process-host production settings builder
    /// for shared queue, admission, tenant runtime settings, persistence, replay, tracing,
    /// provider configuration, and control-plane HTTP mode.
    ///
    /// Only the runtime host lifecycle provider is changed here:
    ///
    /// <code>
    /// AiHttpRuntimeScaleOut:Mode             -> HostManager
    /// AiHttpRuntimeScaleOut:HostCreationMode -> Kubernetes
    /// AiKubernetesRuntimeHost:ClientMode     -> Fake
    /// ProviderName                           -> http
    /// TransportName                          -> http
    /// host.provider                          -> kubernetes
    /// </code>
    ///
    /// Kubernetes owns runtime lifecycle creation.
    /// HTTP remains the runtime command transport.
    /// </remarks>
    internal static class HttpKubernetesHostProductionScenarioSettingsBuilder
    {
        /// <summary>
        /// Builds the complete settings dictionary for a production HTTP Kubernetes host-manager scenario.
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
                HttpProcessHostProductionScenarioSettingsBuilder.Build(
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
        /// Applies Kubernetes host-manager settings while preserving HTTP as the runtime provider and transport.
        /// </summary>
        /// <param name="settings">The settings dictionary to mutate.</param>
        private static void ApplyKubernetesHostManagerSettings(
            Dictionary<string, string?> settings)
        {
            settings["AiRuntimeScaleOutRequestWatcher:Enabled"] = "true";

            settings["AiGrpcRuntimeScaleOut:Enabled"] = "false";

            settings["AiHttpRuntimeScaleOut:Enabled"] = "true";
            settings["AiHttpRuntimeScaleOut:Mode"] = "HostManager";
            settings["AiHttpRuntimeScaleOut:HostCreationMode"] = "Kubernetes";
            settings["AiHttpRuntimeScaleOut:RequireReadiness"] = "false";

            settings["AiHttpRuntimeScaleOut:ReadinessTimeoutSeconds"] = "30";
            settings["AiHttpRuntimeScaleOut:ReadinessPollIntervalMilliseconds"] = "100";
            settings["AiHttpRuntimeScaleOut:DefaultRuntimeInstanceIdPrefix"] = "http-kubernetes-runtime";
            settings["AiHttpRuntimeScaleOut:EndpointTemplate"] = "http://127.0.0.1:8080/{runtimeInstanceId}";

            settings["AiKubernetesRuntimeHost:Enabled"] = "true";
            settings["AiKubernetesRuntimeHost:ClientMode"] = "Fake";
            settings["AiKubernetesRuntimeHost:RequireRuntimeReadiness"] = "false";
            settings["AiKubernetesRuntimeHost:Namespace"] = "ai-runtime";
            settings["AiKubernetesRuntimeHost:RuntimeImage"] = KubernetesSdkScenarioConstants.RuntimeImage;
            settings["AiKubernetesRuntimeHost:ImagePullPolicy"] = "IfNotPresent";
            settings["AiKubernetesRuntimeHost:ContainerName"] = "runtime-instance";
            settings["AiKubernetesRuntimeHost:ContainerPort"] = "8080";
            settings["AiKubernetesRuntimeHost:PodNamePrefix"] = "rt";
            settings["AiKubernetesRuntimeHost:TransportName"] = "http";
            settings["AiKubernetesRuntimeHost:UseServicePerRuntime"] = "true";
            settings["AiKubernetesRuntimeHost:DeleteResourcesOnFailure"] = "true";
            settings["AiKubernetesRuntimeHost:StartupTimeout"] = "00:00:30";
            settings["AiKubernetesRuntimeHost:ReadinessTimeout"] = "00:00:30";
            settings["AiKubernetesRuntimeHost:ReadinessPollInterval"] = "00:00:00.100";

            settings["AiKubernetesRuntimeHost:PublishNodePortTransportEndpoint"] = "true";
            settings["AiKubernetesRuntimeHost:NodePortHost"] = "192.168.49.2";
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
                    setting.Key.Contains("HttpRuntimeScaleOut", StringComparison.OrdinalIgnoreCase) ||
                    setting.Key.Contains("Provider", StringComparison.OrdinalIgnoreCase) ||
                    setting.Key.Contains("ScaleOut", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(
                        $"[HTTP KUBERNETES SETTINGS DEBUG] {setting.Key}='{setting.Value}'");
                }
            }
        }
    }
}