using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Runners;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Provides the focused HTTP Kubernetes Runtime Pool profile used by Child DAG production proofs.
    /// </summary>
    public sealed class HttpKubernetesRuntimePoolChildDagScenarioRuntimeProfile :
        IKubernetesRuntimePoolScenarioRuntimeProfile
    {
        private readonly KubernetesRuntimePoolScenarioTopology topology;

        /// <summary>
        /// Initializes the Child DAG Runtime Pool profile.
        /// </summary>
        /// <param name="maximumPodCount">The maximum Pod capacity allowed during the focused proof.</param>
        /// <remarks>
        /// Nominal and child-failure proofs keep the default one-Pod topology. The final parent-failure proof permits
        /// one additional Pod so C1 can own distinct capacity before the original parent Pod is destroyed.
        /// </remarks>
        public HttpKubernetesRuntimePoolChildDagScenarioRuntimeProfile(
            int maximumPodCount = 1)
        {
            this.topology = new KubernetesRuntimePoolScenarioTopology(
                initialPodCount: 1,
                maximumPodCount: maximumPodCount,
                initialRuntimeCountPerPod: 1,
                maximumRuntimeCountPerPod: 1);
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
            "http-kubernetes-runtime-pool-child-dag-test";

        /// <inheritdoc />
        public string Source => "integration-test";

        /// <inheritdoc />
        public string PoolIdPrefix => "mcp-http-kubernetes-pool";

        /// <inheritdoc />
        public KubernetesRuntimePoolScenarioTopology Topology => this.topology;

        /// <inheritdoc />
        public bool EnableDagExecutionResume => false;

        /// <inheritdoc />
        public Dictionary<string, string?> BuildSettings(
            ProductionRuntimeScenarioDefinition scenario,
            string controlPlaneId,
            string runtimeHostAssemblyPath)
        {
            return HttpKubernetesRuntimePoolProductionScenarioSettingsBuilder.Build(
                scenario,
                controlPlaneId,
                runtimeHostAssemblyPath,
                this);
        }
    }
}
