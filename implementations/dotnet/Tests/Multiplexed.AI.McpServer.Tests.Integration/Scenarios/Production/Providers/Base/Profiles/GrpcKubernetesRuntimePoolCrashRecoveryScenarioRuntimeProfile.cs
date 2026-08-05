using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Runners;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Profiles
{
    /// <summary>
    /// Provides the gRPC Kubernetes Runtime Pool profile for the all-in-one crash-recovery scenario.
    /// </summary>
    public sealed class GrpcKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfile :
        IRuntimePoolCrashRecoveryScenarioRuntimeProfile
    {
        private static readonly RuntimePoolCrashRecoveryScenarioPlan DefaultPlan =
            RuntimePoolCrashRecoveryScenarioPlan.CreateAllInOne(
                initialPodCount: 3,
                maximumPodCount: 3,
                initialRuntimeCountPerPod: 3,
                maximumRuntimeCountPerPod: 3);

        private readonly RuntimePoolCrashRecoveryScenarioPlan plan;

        /// <summary>
        /// Initializes the canonical three-Pod, three-runtime crash-recovery profile.
        /// </summary>
        public GrpcKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfile()
            : this(DefaultPlan)
        {
        }

        /// <summary>
        /// Initializes a bounded Runtime Pool profile for a parameterized
        /// machine-limit scenario.
        /// </summary>
        /// <param name="maximumPodCount">The maximum physical Pod count.</param>
        /// <param name="runtimeCountPerPod">The exact child-runtime count per Pod.</param>
        public GrpcKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfile(
            int maximumPodCount,
            int runtimeCountPerPod)
            : this(
                RuntimePoolCrashRecoveryScenarioPlan.CreateAllInOne(
                    initialPodCount: maximumPodCount,
                    maximumPodCount: maximumPodCount,
                    initialRuntimeCountPerPod: runtimeCountPerPod,
                    maximumRuntimeCountPerPod: runtimeCountPerPod))
        {
        }

        private GrpcKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfile(
            RuntimePoolCrashRecoveryScenarioPlan plan)
        {
            ArgumentNullException.ThrowIfNull(plan);
            this.plan = plan;
        }

        /// <inheritdoc />
        public AiRuntimeCapacityTopologyMode CapacityTopologyMode =>
            AiRuntimeCapacityTopologyMode.KubernetesPool;

        /// <inheritdoc />
        public AiRuntimeHostCreationMode HostCreationMode =>
            AiRuntimeHostCreationMode.KubernetesPool;

        /// <inheritdoc />
        public string ProviderName => "grpc";

        /// <inheritdoc />
        public string ProviderLabel => "grpc-kubernetes-runtime-pool";

        /// <inheritdoc />
        public string LogPrefix => "GRPC KUBERNETES RUNTIME POOL";

        /// <inheritdoc />
        public string RequestedBy =>
            "grpc-kubernetes-runtime-pool-all-in-one-crash-recovery-test";

        /// <inheritdoc />
        public string Source => "integration-test";

        /// <inheritdoc />
        public string PoolIdPrefix => "mcp-grpc-kubernetes-pool";

        /// <inheritdoc />
        public RuntimePoolCrashRecoveryScenarioPlan CrashRecoveryPlan => plan;

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
