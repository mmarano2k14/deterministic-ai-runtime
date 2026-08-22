using Multiplexed.Abstractions.AI.Observability.Events;
namespace Multiplexed.Abstractions.AI.Observability.Ledger
{
    /// <summary>
    /// Defines the high-level category of a decision ledger entry.
    /// </summary>
    /// <remarks>
    /// Categories are intentionally broad and stable. Specific runtime events are represented
    /// by string constants from <see cref="AiEngineEvents"/> instead of a very large enum.
    /// </remarks>
    public enum AiDecisionLedgerCategory
    {
        /// <summary>
        /// Represents execution lifecycle decisions.
        /// </summary>
        Execution,

        /// <summary>
        /// Represents controller run lifecycle decisions.
        /// </summary>
        Run,

        /// <summary>
        /// Represents queue lifecycle decisions.
        /// </summary>
        Queue,

        /// <summary>
        /// Represents DAG scheduling and dependency decisions.
        /// </summary>
        Dag,

        /// <summary>
        /// Represents distributed claim and lease decisions.
        /// </summary>
        Claim,

        /// <summary>
        /// Represents step execution decisions.
        /// </summary>
        Step,

        /// <summary>
        /// Represents recovery decisions.
        /// </summary>
        Recovery,

        /// <summary>
        /// Represents retry decisions.
        /// </summary>
        Retry,

        /// <summary>
        /// Represents policy evaluation decisions.
        /// </summary>
        Policy,

        /// <summary>
        /// Represents concurrency and throttling decisions.
        /// </summary>
        Concurrency,

        /// <summary>
        /// Represents execution control state decisions.
        /// </summary>
        Control,

        /// <summary>
        /// Represents human input decisions.
        /// </summary>
        HumanInput,

        /// <summary>
        /// Represents retention, compaction, and eviction decisions.
        /// </summary>
        Retention,

        /// <summary>
        /// Represents payload externalization and rehydration decisions.
        /// </summary>
        Payload,

        /// <summary>
        /// Represents snapshot creation, loading, and restore decisions.
        /// </summary>
        Snapshot,

        /// <summary>
        /// Represents durable or hot-state storage decisions.
        /// </summary>
        Storage,

        /// <summary>
        /// Represents replay decisions.
        /// </summary>
        Replay,

        /// <summary>
        /// Represents execution finalization decisions.
        /// </summary>
        Finalization,

        /// <summary>
        /// Represents runtime instance registration, heartbeat, health, draining, and lifecycle decisions.
        /// </summary>
        RuntimeInstance,

        /// <summary>
        /// Represents run admission, capacity selection, routing, and assignment decisions.
        /// </summary>
        Admission,

        /// <summary>
        /// Represents shared runtime controller assignment, reassignment, failover, and ownership decisions.
        /// </summary>
        SharedController,

        /// <summary>
        /// Represents runtime-aware scale-out and scale-in decisions.
        /// </summary>
        Scaling,





    }
}