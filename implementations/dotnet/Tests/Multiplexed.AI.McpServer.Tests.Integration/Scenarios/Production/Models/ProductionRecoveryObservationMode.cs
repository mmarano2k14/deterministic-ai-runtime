namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Models
{
    /// <summary>
    /// Defines how production recovery scenarios observe runtime convergence.
    /// </summary>
    public enum ProductionRecoveryObservationMode
    {
        /// <summary>
        /// Uses the existing durable-store polling behavior.
        /// </summary>
        Polling = 0,

        /// <summary>
        /// Uses targeted runtime signals with durable-store fallback polling.
        /// </summary>
        HybridSignals = 1,

        /// <summary>
        /// Uses canonical engine events through the deterministic lifecycle observer,
        /// then verifies the resulting durable runtime state without replacing the
        /// historical polling mode used by existing scenarios.
        /// </summary>
        EventDriven = 2
    }
}