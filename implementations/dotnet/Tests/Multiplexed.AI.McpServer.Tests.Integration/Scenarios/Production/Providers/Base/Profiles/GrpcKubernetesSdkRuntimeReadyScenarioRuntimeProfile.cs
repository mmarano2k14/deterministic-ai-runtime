using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Runners;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Provides the gRPC runtime profile for Kubernetes SDK runtime-readiness production scenarios.
    /// </summary>
    public sealed class GrpcKubernetesSdkRuntimeReadyScenarioRuntimeProfile : IProcessHostScenarioRuntimeProfile
    {
        /// <inheritdoc />
        public string ProviderName => "grpc";

        /// <inheritdoc />
        public string ProviderLabel => "gk8s-ready";

        /// <inheritdoc />
        public string LogPrefix => "GRPC K8S SDK RUNTIME READY";

        /// <inheritdoc />
        public string RequestedBy => "grpc-kubernetes-sdk-runtime-ready-test";

        /// <inheritdoc />
        public string Source => "integration-test";

        /// <inheritdoc />
        public AiRuntimeHostCreationMode HostCreationMode => AiRuntimeHostCreationMode.Kubernetes;

        /// <inheritdoc />
        public Dictionary<string, string?> BuildSettings(
            ProductionRuntimeScenarioDefinition scenario,
            string controlPlaneId,
            string runtimeHostAssemblyPath)
        {
            return GrpcKubernetesSdkRuntimeReadyProductionScenarioSettingsBuilder.Build(
                scenario,
                controlPlaneId,
                runtimeHostAssemblyPath);
        }
    }
}