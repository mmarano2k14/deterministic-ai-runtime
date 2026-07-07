using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Runners;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Provides the gRPC runtime profile for Kubernetes SDK host-manager production scenarios.
    /// </summary>
    public sealed class GrpcKubernetesSdkHostScenarioRuntimeProfile : IProcessHostScenarioRuntimeProfile
    {
        /// <inheritdoc />
        public string ProviderName => "grpc";

        /// <inheritdoc />
        public string ProviderLabel => "gk8s";

        /// <inheritdoc />
        public string LogPrefix => "GRPC K8S SDK HOST";

        /// <inheritdoc />
        public string RequestedBy => "grpc-kubernetes-sdk-host-manager-scaleout-test";

        /// <inheritdoc />
        public string Source => "integration-test";

        /// <inheritdoc />
        public Dictionary<string, string?> BuildSettings(
            ProductionRuntimeScenarioDefinition scenario,
            string controlPlaneId,
            string runtimeHostAssemblyPath)
        {
            return GrpcKubernetesSdkHostProductionScenarioSettingsBuilder.Build(
                scenario,
                controlPlaneId,
                runtimeHostAssemblyPath);
        }
    }
}