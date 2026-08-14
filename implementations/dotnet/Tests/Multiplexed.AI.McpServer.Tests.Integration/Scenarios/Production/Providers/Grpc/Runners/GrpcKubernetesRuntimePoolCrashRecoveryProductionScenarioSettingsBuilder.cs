using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.KubernetesPool;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Runners
{
    /// <summary>
    /// Builds MCP host settings for the real gRPC Kubernetes Runtime Pool crash-recovery scenario.
    /// </summary>
    internal static class GrpcKubernetesRuntimePoolCrashRecoveryProductionScenarioSettingsBuilder
    {
        private const string ScaleOutSectionName =
            "AiGrpcRuntimeScaleOut";

        private const int FirstChildTransportPort =
            19080;

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

            return KubernetesRuntimePoolProductionScenarioSettingsComposer.Apply(
                settings,
                poolId,
                profile,
                ScaleOutSectionName,
                FirstChildTransportPort);
        }
    }
}
