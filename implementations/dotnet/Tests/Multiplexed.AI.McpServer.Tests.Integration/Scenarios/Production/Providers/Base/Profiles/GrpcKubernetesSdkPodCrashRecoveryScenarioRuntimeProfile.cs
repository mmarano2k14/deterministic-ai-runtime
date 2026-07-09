using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Runners;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Provides the gRPC Kubernetes SDK runtime profile for production pod crash recovery scenarios.
    /// </summary>
    public sealed class GrpcKubernetesSdkPodCrashRecoveryScenarioRuntimeProfile : IProcessHostScenarioRuntimeProfile
    {
        /// <inheritdoc />
        public string ProviderName => "grpc";

        /// <inheritdoc />
        public string ProviderLabel => "gk8s-crash";

        /// <inheritdoc />
        public string LogPrefix => "GRPC K8S SDK POD CRASH RECOVERY";

        /// <inheritdoc />
        public string RequestedBy => "grpc-kubernetes-sdk-pod-crash-recovery-test";

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
            return GrpcKubernetesSdkPodCrashRecoveryProductionScenarioSettingsBuilder.Build(
                scenario,
                controlPlaneId,
                runtimeHostAssemblyPath); 
        }
    }
}