using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Runners;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Profiles
{
    /// <summary>
    /// Provides the gRPC runtime profile for process-host production scenarios.
    /// </summary>
    public sealed class GrpcProcessHostScenarioRuntimeProfile : IProcessHostScenarioRuntimeProfile
    {
        /// <inheritdoc />
        public string ProviderName => "grpc";

        /// <inheritdoc />
        public string ProviderLabel => "grpc-process-host";

        /// <inheritdoc />
        public string LogPrefix => "GRPC PROCESS HOST";

        /// <inheritdoc />
        public string RequestedBy => "grpc-process-host-real-runtime-crash-recovery-test";

        /// <inheritdoc />
        public string Source => "integration-test";

        /// <inheritdoc />
        public AiRuntimeHostCreationMode HostCreationMode => AiRuntimeHostCreationMode.Process;

        /// <inheritdoc />
        public Dictionary<string, string?> BuildSettings(
            ProductionRuntimeScenarioDefinition scenario,
            string controlPlaneId,
            string runtimeHostAssemblyPath)
        {
            return GrpcProcessHostProductionScenarioSettingsBuilder.Build(
                scenario,
                controlPlaneId,
                runtimeHostAssemblyPath);
        }
    }
}