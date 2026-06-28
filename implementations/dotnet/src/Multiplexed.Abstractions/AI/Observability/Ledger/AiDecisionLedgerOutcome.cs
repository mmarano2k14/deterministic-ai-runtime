namespace Multiplexed.Abstractions.AI.Observability.Ledger
{
    /// <summary>
    /// Defines the outcome of a decision ledger event.
    /// </summary>
    /// <remarks>
    /// Outcomes are intentionally small and reusable across event categories.
    /// The specific event meaning is defined by the event type string.
    /// </remarks>
    public enum AiDecisionLedgerOutcome
    {
        /// <summary>
        /// No explicit outcome was assigned.
        /// </summary>
        None,

        /// <summary>
        /// The runtime operation started.
        /// </summary>
        Started,

        /// <summary>
        /// The runtime operation completed.
        /// </summary>
        Completed,

        /// <summary>
        /// The runtime operation completed with validation, diagnostic, or partial-state issues.
        /// </summary>
        /// <remarks>
        /// This outcome is used when an operation reached a completed state but produced warnings,
        /// partial results, degraded diagnostics, or non-fatal issues that should remain visible
        /// in ledger timelines and replay/forensics views.
        /// </remarks>
        CompletedWithIssues,

        /// <summary>
        /// The runtime operation failed.
        /// </summary>
        Failed,

        /// <summary>
        /// The runtime operation succeeded.
        /// </summary>
        Succeeded,

        /// <summary>
        /// The runtime decision allowed the operation.
        /// </summary>
        Allowed,

        /// <summary>
        /// The runtime decision denied the operation.
        /// </summary>
        Denied,

        /// <summary>
        /// The runtime decision skipped the operation.
        /// </summary>
        Skipped,

        /// <summary>
        /// The runtime decision blocked the operation.
        /// </summary>
        Blocked,

        /// <summary>
        /// The runtime operation was cancelled.
        /// </summary>
        Cancelled,

        /// <summary>
        /// The runtime operation or lease expired.
        /// </summary>
        Expired,

        /// <summary>
        /// The runtime decision was applied.
        /// </summary>
        Applied,

        /// <summary>
        /// The runtime decision was triggered.
        /// </summary>
        Triggered,

        /// <summary>
        /// The runtime lease or resource was released.
        /// </summary>
        Released,

        /// <summary>
        /// The runtime data was persisted.
        /// </summary>
        Persisted,

        /// <summary>
        /// The runtime data was loaded.
        /// </summary>
        Loaded,

        /// <summary>
        /// The operation produced a ready state and may continue execution.
        /// </summary>
        /// <remarks>
        /// This outcome is primarily used for orchestration and DAG scheduling events,
        /// such as when a step becomes ready after dependency completion.
        /// </remarks>  
        Ready,

    }
}