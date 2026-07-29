using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Defines the additional bounded-topology contract required by Runtime Pool crash-recovery scenarios.
    /// </summary>
    public interface IRuntimePoolCrashRecoveryScenarioRuntimeProfile :
        IProcessHostScenarioRuntimeProfile
    {
        /// <summary>
        /// Gets the first-class capacity topology exercised by the scenario.
        /// </summary>
        AiRuntimeCapacityTopologyMode CapacityTopologyMode =>
            AiRuntimeCapacityTopologyMode.Unspecified;

        /// <summary>
        /// Gets the stable prefix used to create scenario-isolated Runtime Pool identities.
        /// </summary>
        string PoolIdPrefix { get; }

        /// <summary>
        /// Gets the bounded topology and ordered physical-failure plan exercised by the scenario.
        /// </summary>
        RuntimePoolCrashRecoveryScenarioPlan CrashRecoveryPlan { get; }
    }
}
