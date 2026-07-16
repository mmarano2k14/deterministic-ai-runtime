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
        HybridSignals = 1
    }
}