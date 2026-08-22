namespace Multiplexed.AI.Runtime.ControlPlane.Observability
{
    /// <summary>
    /// Identifies one centralized observability projection surface.
    /// </summary>
    public enum AiEngineEventProjectionTarget
    {
        /// <summary>
        /// Decision Ledger projection.
        /// </summary>
        Ledger = 0,

        /// <summary>
        /// Runtime recovery Forensics projection.
        /// </summary>
        RecoveryForensics = 1,

        /// <summary>
        /// Execution Forensics projection.
        /// </summary>
        ExecutionForensics = 2,

        /// <summary>
        /// Runtime Lifecycle Journal projection.
        /// </summary>
        LifecycleJournal = 3,

        /// <summary>
        /// Metrics projection.
        /// </summary>
        Metrics = 4,

        /// <summary>
        /// Structured logging projection.
        /// </summary>
        Logging = 5,

        /// <summary>
        /// Realtime observation projection.
        /// </summary>
        Realtime = 6
    }
}
