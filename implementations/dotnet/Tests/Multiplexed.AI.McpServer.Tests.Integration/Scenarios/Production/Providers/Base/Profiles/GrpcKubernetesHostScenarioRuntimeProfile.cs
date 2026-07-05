using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Runners;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Provides the gRPC runtime profile for Kubernetes host-manager production scenarios.
    /// </summary>
    /// <remarks>
    /// This profile proves that Kubernetes can own runtime lifecycle creation while gRPC remains
    /// the runtime provider and command transport.
    /// </remarks>
    public sealed class GrpcKubernetesHostScenarioRuntimeProfile : IProcessHostScenarioRuntimeProfile
    {
        /// <inheritdoc />
        public string ProviderName => "grpc";

        /// <inheritdoc />
        public string ProviderLabel => "grpc-kubernetes-host";

        /// <inheritdoc />
        public string LogPrefix => "GRPC KUBERNETES HOST";

        /// <inheritdoc />
        public string RequestedBy => "grpc-kubernetes-host-manager-scaleout-test";

        /// <inheritdoc />
        public string Source => "integration-test";

        /// <inheritdoc />
        public Dictionary<string, string?> BuildSettings(
            ProductionRuntimeScenarioDefinition scenario,
            string controlPlaneId,
            string runtimeHostAssemblyPath)
        {
            return GrpcKubernetesHostProductionScenarioSettingsBuilder.Build(
                scenario,
                controlPlaneId,
                runtimeHostAssemblyPath);
        }
    }
}