namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Defines the additional bounded-topology contract required by Runtime Pool crash-recovery scenarios.
    /// </summary>
    public interface IRuntimePoolCrashRecoveryScenarioRuntimeProfile :
        IKubernetesRuntimePoolScenarioRuntimeProfile
    {
        /// <summary>
        /// Gets the bounded topology and ordered physical-failure plan exercised by the scenario.
        /// </summary>
        RuntimePoolCrashRecoveryScenarioPlan CrashRecoveryPlan { get; }
    }
}
