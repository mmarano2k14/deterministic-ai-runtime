using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Runners;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Runners
{
    /// <summary>
    /// Builds MCP host settings for production gRPC process-host scenarios.
    /// </summary>
    /// <remarks>
    /// This builder intentionally reuses the HTTP process-host production settings builder
    /// for shared queue, recovery, admission, tenant runtime settings, persistence,
    /// replay, tracing, and process-host configuration.
    ///
    /// Only the remote runtime transport contract is changed here:
    ///
    /// <code>
    /// ControlPlaneWithHttpRuntimeInstances  -> ControlPlaneWithGrpcRuntimeInstances
    /// provider.name                         -> grpc
    /// transport.name                        -> grpc
    /// AiHttpRuntimeScaleOut                 -> disabled
    /// AiGrpcRuntimeScaleOut                 -> HostManager / Process
    /// RuntimeInstanceOnly child transport   -> grpc
    /// RuntimeInstanceOnly Kestrel transport -> HTTP/2
    /// </code>
    ///
    /// This avoids duplicating the large HTTP production scenario configuration while
    /// keeping HTTP and gRPC behavior isolated.
    /// </remarks>
    internal static class GrpcProcessHostProductionScenarioSettingsBuilder
    {
        /// <summary>
        /// Builds the complete settings dictionary for a production gRPC process-host scenario.
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
                HttpProcessHostProductionScenarioSettingsBuilder.Build(
                    scenario,
                    controlPlaneId,
                    runtimeHostAssemblyPath);

            ApplyGrpcControlPlaneMode(
                settings,
                controlPlaneId);

            ApplyGrpcScaleOutSettings(
                settings);

            ApplyGrpcRuntimeProcessTransportSettings(
                settings);

            ApplyGrpcProviderSettings(
                settings);

            WriteGrpcSettingsDebug(
                settings);

            return settings;
        }

        /// <summary>
        /// Applies the gRPC control-plane mode and control-plane runtime registration metadata.
        /// </summary>
        /// <param name="settings">The settings dictionary to mutate.</param>
        /// <param name="controlPlaneId">The control-plane identifier.</param>
        private static void ApplyGrpcControlPlaneMode(
            Dictionary<string, string?> settings,
            string controlPlaneId)
        {
            settings["AiMcpHost:Mode"] = "ControlPlaneWithGrpcRuntimeInstances";

            settings["AiRuntimeInstanceRegistration:ProviderName"] = "grpc";
            settings["AiRuntimeInstanceRegistration:ProviderMetadata:controlPlaneId"] = controlPlaneId;
            settings["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "grpc";
            settings["AiRuntimeInstanceRegistration:ProviderMetadata:transport.name"] = "grpc";
            settings["AiRuntimeInstanceRegistration:Metadata:controlPlaneId"] = controlPlaneId;
            settings["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "grpc";
            settings["AiRuntimeInstanceRegistration:Metadata:transport.name"] = "grpc";
            settings["AiRuntimeInstanceRegistration:Metadata:hostType"] = "control-plane-with-grpc-process-host";
            settings["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-grpc-process-host";
        }

        /// <summary>
        /// Applies gRPC scale-out settings for real RuntimeInstanceOnly process creation.
        /// </summary>
        /// <param name="settings">The settings dictionary to mutate.</param>
        private static void ApplyGrpcScaleOutSettings(
            Dictionary<string, string?> settings)
        {
            settings["AiRuntimeScaleOutRequestWatcher:Enabled"] = "true";

            settings["AiHttpRuntimeScaleOut:Enabled"] = "false";

            settings["AiGrpcRuntimeScaleOut:Enabled"] = "true";
            settings["AiGrpcRuntimeScaleOut:Mode"] = "HostManager";
            settings["AiGrpcRuntimeScaleOut:HostCreationMode"] = "Process";
            settings["AiGrpcRuntimeScaleOut:RequireReadiness"] = "true";
            settings["AiGrpcRuntimeScaleOut:ReadinessTimeoutSeconds"] = "30";
            settings["AiGrpcRuntimeScaleOut:ReadinessPollIntervalMilliseconds"] = "100";
            settings["AiGrpcRuntimeScaleOut:DefaultRuntimeInstanceIdPrefix"] = "grpc-runtime";
            settings["AiGrpcRuntimeScaleOut:EndpointTemplate"] = "http://127.0.0.1:{port}/{runtimeInstanceId}";
        }

        /// <summary>
        /// Applies child RuntimeInstanceOnly environment variables required to expose gRPC command transport.
        /// </summary>
        /// <param name="settings">The settings dictionary to mutate.</param>
        private static void ApplyGrpcRuntimeProcessTransportSettings(
            Dictionary<string, string?> settings)
        {
            ApplyProcessEnvironmentSetting(
                settings,
                "AiRuntimeInstanceRegistration:ProviderName",
                "grpc");

            ApplyProcessEnvironmentSetting(
                settings,
                "AiRuntimeInstanceRegistration:ProviderMetadata:provider.name",
                "grpc");

            ApplyProcessEnvironmentSetting(
                settings,
                "AiRuntimeInstanceRegistration:ProviderMetadata:transport.name",
                "grpc");

            ApplyProcessEnvironmentSetting(
                settings,
                "AiRuntimeInstanceRegistration:Metadata:provider.name",
                "grpc");

            ApplyProcessEnvironmentSetting(
                settings,
                "AiRuntimeInstanceRegistration:Metadata:transport.name",
                "grpc");

            ApplyProcessEnvironmentSetting(
                settings,
                "AiRuntimeInstanceRegistration:Metadata:hostType",
                "runtime-instance-only-grpc");

            ApplyProcessEnvironmentSetting(
                settings,
                "AiRuntimeInstanceRegistration:Metadata:deployment",
                "test-grpc-runtime-process");

            ApplyProcessEnvironmentSetting(
                settings,
                "Kestrel:EndpointDefaults:Protocols",
                "Http2");
        }

        /// <summary>
        /// Applies gRPC provider settings.
        /// </summary>
        /// <param name="settings">The settings dictionary to mutate.</param>
        private static void ApplyGrpcProviderSettings(
            Dictionary<string, string?> settings)
        {
            settings["AiGrpcRuntimeInstanceProvider:DispatchTimeout"] = "00:00:30";
            settings["AiGrpcRuntimeInstanceProvider:EnableCircuitBreaker"] = "false";
            settings["AiGrpcRuntimeInstanceProvider:CircuitBreakerFailureThreshold"] = "100";
        }

        /// <summary>
        /// Applies a setting to RuntimeInstanceOnly child process environment variables.
        /// </summary>
        /// <param name="settings">The settings dictionary to mutate.</param>
        /// <param name="key">The colon-separated configuration key.</param>
        /// <param name="value">The setting value.</param>
        private static void ApplyProcessEnvironmentSetting(
            Dictionary<string, string?> settings,
            string key,
            string value)
        {
            settings[$"AiRuntimeProcessHostCreation:EnvironmentVariables:{key.Replace(":", "__", StringComparison.Ordinal)}"] = value;
        }

        /// <summary>
        /// Writes temporary gRPC settings diagnostics for process-host scale-out debugging.
        /// </summary>
        /// <param name="settings">The settings dictionary.</param>
        private static void WriteGrpcSettingsDebug(
            Dictionary<string, string?> settings)
        {
            foreach (var setting in settings.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (setting.Key.Contains("Provider", StringComparison.OrdinalIgnoreCase) ||
                    setting.Key.Contains("ScaleOut", StringComparison.OrdinalIgnoreCase) ||
                    setting.Key.Contains("Tenant", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(setting.Value, "http", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(
                        $"[GRPC SETTINGS DEBUG] {setting.Key}='{setting.Value}'");
                }
            }
        }
    }
}