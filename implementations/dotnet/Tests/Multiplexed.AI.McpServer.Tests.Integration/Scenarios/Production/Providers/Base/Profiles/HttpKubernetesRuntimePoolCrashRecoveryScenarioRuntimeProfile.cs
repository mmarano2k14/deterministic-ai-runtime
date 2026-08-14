using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Runners;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Provides the HTTP Kubernetes Runtime Pool profile for bounded production recovery scenarios.
    /// </summary>
    public sealed class HttpKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfile :
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
        /// Initializes the canonical three-Pod, three-runtime HTTP Runtime Pool profile.
        /// </summary>
        public HttpKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfile()
            : this(DefaultPlan)
        {
        }

        /// <summary>
        /// Initializes a bounded HTTP Runtime Pool profile for a parameterized production scenario.
        /// </summary>
        /// <param name="maximumPodCount">The maximum physical Pod count.</param>
        /// <param name="runtimeCountPerPod">The exact child-runtime count per Pod.</param>
        public HttpKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfile(
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

        private HttpKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfile(
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
        public string ProviderName => "http";

        /// <inheritdoc />
        public string ProviderLabel => "http-kubernetes-runtime-pool";

        /// <inheritdoc />
        public string LogPrefix => "HTTP KUBERNETES RUNTIME POOL";

        /// <inheritdoc />
        public string RequestedBy =>
            "http-kubernetes-runtime-pool-production-recovery-test";

        /// <inheritdoc />
        public string Source => "integration-test";

        /// <inheritdoc />
        public string PoolIdPrefix => "mcp-http-kubernetes-pool";

        /// <inheritdoc />
        public RuntimePoolCrashRecoveryScenarioPlan CrashRecoveryPlan => plan;

        /// <inheritdoc />
        public Dictionary<string, string?> BuildSettings(
            ProductionRuntimeScenarioDefinition scenario,
            string controlPlaneId,
            string runtimeHostAssemblyPath)
        {
            return HttpKubernetesRuntimePoolCrashRecoveryProductionScenarioSettingsBuilder.Build(
                scenario,
                controlPlaneId,
                runtimeHostAssemblyPath,
                this);
        }
    }
}
