using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Runners;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Profiles
{
    /// <summary>
    /// Provides the bounded gRPC Kubernetes Runtime Pool profile used by the five-scenario Pod-failure proof.
    /// </summary>
    public sealed class GrpcKubernetesRuntimePoolPodFailureP5ScenarioRuntimeProfile :
        IRuntimePoolCrashRecoveryScenarioRuntimeProfile
    {
        private static readonly RuntimePoolCrashRecoveryScenarioPlan Plan =
            RuntimePoolCrashRecoveryScenarioPlan.CreatePodFailureOnly(
                initialPodCount: 2,
                maximumPodCount: 2,
                initialRuntimeCountPerPod: 3,
                maximumRuntimeCountPerPod: 3);

        /// <inheritdoc />
        public AiRuntimeCapacityTopologyMode CapacityTopologyMode =>
            AiRuntimeCapacityTopologyMode.KubernetesPool;

        /// <inheritdoc />
        public AiRuntimeHostCreationMode HostCreationMode =>
            AiRuntimeHostCreationMode.KubernetesPool;

        /// <inheritdoc />
        public string ProviderName => "grpc";

        /// <inheritdoc />
        public string ProviderLabel =>
            "grpc-kubernetes-runtime-pool-pod-failure-p5";

        /// <inheritdoc />
        public string LogPrefix =>
            "GRPC KUBERNETES RUNTIME POOL POD FAILURE P5";

        /// <inheritdoc />
        public string RequestedBy =>
            "grpc-kubernetes-runtime-pool-pod-failure-p5-test";

        /// <inheritdoc />
        public string Source => "integration-test";

        /// <inheritdoc />
        public string PoolIdPrefix =>
            "mcp-grpc-kubernetes-pool-pod-failure-p5";

        /// <inheritdoc />
        public RuntimePoolCrashRecoveryScenarioPlan CrashRecoveryPlan => Plan;

        /// <inheritdoc />
        public Dictionary<string, string?> BuildSettings(
            ProductionRuntimeScenarioDefinition scenario,
            string controlPlaneId,
            string runtimeHostAssemblyPath)
        {
            return GrpcKubernetesRuntimePoolCrashRecoveryProductionScenarioSettingsBuilder.Build(
                scenario,
                controlPlaneId,
                runtimeHostAssemblyPath,
                this);
        }
    }
}
