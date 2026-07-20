namespace Multiplexed.Abstractions.AI.ControlPlane.Signals
{
    /// <summary>
    /// Identifies a lightweight runtime state-change signal.
    /// </summary>
    public enum AiRuntimeSignalType
    {
        /// <summary>
        /// No signal type has been specified.
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// Durable DAG execution progress has changed.
        /// </summary>
        DagProgressChanged = 1,

        /// <summary>
        /// Durable ownership of a shared run has been assigned to a runtime instance.
        /// </summary>
        SharedRunDispatched = 2
    }
}