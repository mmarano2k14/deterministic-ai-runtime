using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Runners;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Provides the HTTP runtime profile for process-host production scenarios.
    /// </summary>
    internal sealed class HttpProcessHostScenarioRuntimeProfile : IProcessHostScenarioRuntimeProfile
    {
        /// <inheritdoc />
        public string ProviderName => "http";

        /// <inheritdoc />
        public string ProviderLabel => "http-process-host";

        /// <inheritdoc />
        public string LogPrefix => "HTTP PROCESS HOST";

        /// <inheritdoc />
        public string RequestedBy => "http-process-host-real-runtime-crash-recovery-test";

        /// <inheritdoc />
        public string Source => "integration-test";

        /// <inheritdoc />
        public Dictionary<string, string?> BuildSettings(
            ProductionRuntimeScenarioDefinition scenario,
            string controlPlaneId,
            string runtimeHostAssemblyPath)
        {
            return HttpProcessHostProductionScenarioSettingsBuilder.Build(
                scenario,
                controlPlaneId,
                runtimeHostAssemblyPath);
        }
    }
}