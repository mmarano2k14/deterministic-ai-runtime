using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Runners;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Runners;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.ProcessHostPool
{
    /// <summary>
    /// Describes the transport-specific values used by the shared multi-host ProcessPool proof.
    /// </summary>
    public sealed record ProcessHostPoolProductionScenarioProfile
    {
        private readonly Func<
            ProductionRuntimeScenarioDefinition,
            string,
            string,
            Dictionary<string, string?>> controlPlaneSettingsBuilder;

        private ProcessHostPoolProductionScenarioProfile(
            string providerName,
            string logPrefix,
            string poolIdPrefix,
            Func<
                ProductionRuntimeScenarioDefinition,
                string,
                string,
                Dictionary<string, string?>> controlPlaneSettingsBuilder)
        {
            this.ProviderName = providerName;
            this.LogPrefix = logPrefix;
            this.PoolIdPrefix = poolIdPrefix;
            this.controlPlaneSettingsBuilder = controlPlaneSettingsBuilder;
        }

        /// <summary>
        /// Gets the remote runtime provider and transport name.
        /// </summary>
        public string ProviderName { get; }

        /// <summary>
        /// Gets the test-output prefix.
        /// </summary>
        public string LogPrefix { get; }

        /// <summary>
        /// Gets the logical pool identifier prefix.
        /// </summary>
        public string PoolIdPrefix { get; }

        /// <summary>
        /// Gets a value indicating whether parent Process Hosts expose an HTTP/2 endpoint.
        /// </summary>
        public bool RequiresHttp2 =>
            StringComparer.OrdinalIgnoreCase.Equals(
                this.ProviderName,
                "grpc");

        /// <summary>
        /// Builds the transport-specific MCP control-plane settings.
        /// </summary>
        public Dictionary<string, string?> BuildControlPlaneSettings(
            ProductionRuntimeScenarioDefinition scenario,
            string controlPlaneId,
            string runtimeHostAssemblyPath)
        {
            return this.controlPlaneSettingsBuilder(
                scenario,
                controlPlaneId,
                runtimeHostAssemblyPath);
        }

        /// <summary>
        /// Creates the gRPC profile.
        /// </summary>
        public static ProcessHostPoolProductionScenarioProfile CreateGrpc()
        {
            return new ProcessHostPoolProductionScenarioProfile(
                providerName: "grpc",
                logPrefix: "GRPC PROCESS HOST POOL",
                poolIdPrefix: "mcp-grpc-process-host-pool",
                GrpcProcessHostProductionScenarioSettingsBuilder.Build);
        }

        /// <summary>
        /// Creates the HTTP profile.
        /// </summary>
        public static ProcessHostPoolProductionScenarioProfile CreateHttp()
        {
            return new ProcessHostPoolProductionScenarioProfile(
                providerName: "http",
                logPrefix: "HTTP PROCESS HOST POOL",
                poolIdPrefix: "mcp-http-process-host-pool",
                HttpProcessHostProductionScenarioSettingsBuilder.Build);
        }
    }
}
