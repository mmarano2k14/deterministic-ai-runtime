using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Runners
{
    /// <summary>
    /// Builds MCP host settings for the real HTTP Kubernetes Runtime Pool crash-recovery scenario.
    /// </summary>
    internal static class HttpKubernetesRuntimePoolCrashRecoveryProductionScenarioSettingsBuilder
    {
        /// <summary>
        /// Builds the complete settings dictionary for the bounded Runtime Pool crash-recovery scenario.
        /// </summary>
        /// <param name="scenario">The production runtime scenario definition.</param>
        /// <param name="controlPlaneId">The resolved control-plane identifier.</param>
        /// <param name="runtimeHostAssemblyPath">The runtime host assembly path retained by the shared settings contract.</param>
        /// <param name="profile">The Runtime Pool crash-recovery scenario profile.</param>
        /// <returns>The complete MCP host settings dictionary.</returns>
        public static Dictionary<string, string?> Build(
            ProductionRuntimeScenarioDefinition scenario,
            string controlPlaneId,
            string runtimeHostAssemblyPath,
            IRuntimePoolCrashRecoveryScenarioRuntimeProfile profile)
        {
            return HttpKubernetesRuntimePoolProductionScenarioSettingsBuilder.Build(
                scenario,
                controlPlaneId,
                runtimeHostAssemblyPath,
                profile);
        }
    }
}
