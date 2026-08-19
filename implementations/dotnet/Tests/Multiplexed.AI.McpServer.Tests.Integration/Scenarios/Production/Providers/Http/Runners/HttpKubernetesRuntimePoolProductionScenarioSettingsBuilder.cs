using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.KubernetesPool;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Runners
{
    /// <summary>
    /// Builds MCP host settings for HTTP Kubernetes Runtime Pool production scenarios.
    /// </summary>
    internal static class HttpKubernetesRuntimePoolProductionScenarioSettingsBuilder
    {
        private const string ScaleOutSectionName =
            "AiHttpRuntimeScaleOut";

        private const int FirstChildTransportPort =
            18080;

        /// <summary>
        /// Builds the complete settings dictionary for the bounded Runtime Pool scenario.
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
            IKubernetesRuntimePoolScenarioRuntimeProfile profile)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeHostAssemblyPath);
            ArgumentNullException.ThrowIfNull(profile);

            var settings =
                HttpProcessHostProductionScenarioSettingsBuilder.Build(
                    scenario,
                    controlPlaneId,
                    runtimeHostAssemblyPath);

            var poolId =
                RuntimePoolCrashRecoveryScenarioIdentity.CreatePoolId(
                    profile.PoolIdPrefix,
                    controlPlaneId);

            return KubernetesRuntimePoolProductionScenarioSettingsComposer.Apply(
                settings,
                poolId,
                profile,
                ScaleOutSectionName,
                FirstChildTransportPort);
        }
    }
}
