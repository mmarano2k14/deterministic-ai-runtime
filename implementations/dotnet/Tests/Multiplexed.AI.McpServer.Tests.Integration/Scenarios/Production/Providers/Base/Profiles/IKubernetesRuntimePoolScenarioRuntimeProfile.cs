using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Defines the topology and provider contract shared by nominal and crash-recovery Kubernetes Runtime Pool scenarios.
    /// </summary>
    public interface IKubernetesRuntimePoolScenarioRuntimeProfile :
        IProcessHostScenarioRuntimeProfile
    {
        /// <summary>
        /// Gets the first-class capacity topology exercised by the scenario.
        /// </summary>
        AiRuntimeCapacityTopologyMode CapacityTopologyMode { get; }

        /// <summary>
        /// Gets the stable prefix used to create scenario-isolated Runtime Pool identities.
        /// </summary>
        string PoolIdPrefix { get; }

        /// <summary>
        /// Gets the bounded Kubernetes Runtime Pool topology exercised by the scenario.
        /// </summary>
        KubernetesRuntimePoolScenarioTopology Topology { get; }

        /// <summary>
        /// Gets a value indicating whether DAG execution resume must be forced for the scenario.
        /// </summary>
        bool EnableDagExecutionResume { get; }
    }
}
