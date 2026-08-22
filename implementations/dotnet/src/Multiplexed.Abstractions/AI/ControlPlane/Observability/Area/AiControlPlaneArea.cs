namespace Multiplexed.Abstractions.AI.ControlPlane.Observability.Area
{
    /// <summary>
    /// Defines logical control-plane areas.
    /// </summary>
    public enum AiControlPlaneArea
    {
        /// <summary>
        /// Replay, audit, restore, report, ledger, and timeline operations.
        /// </summary>
        Replay = 0,

        /// <summary>
        /// Execution pause, resume, cancel, status, and human input operations.
        /// </summary>
        ExecutionControl = 1,

        /// <summary>
        /// Run enqueue, cancel, status, pause queue, and resume queue operations.
        /// </summary>
        RunControl = 2,

        /// <summary>
        /// Runtime instance registration, heartbeat, status, and health operations.
        /// </summary>
        InstanceRegistry = 3,

        /// <summary>
        /// Run admission, slot selection, capacity checks, and assignment decisions.
        /// </summary>
        Admission = 4,

        /// <summary>
        /// Shared/global queue operations above local runtime queues.
        /// </summary>
        SharedQueue = 5,

        /// <summary>
        /// Shared runtime controller decisions such as assignment, reassignment, and failover.
        /// </summary>
        SharedController = 6,

        /// <summary>
        /// Runtime-aware scale-out and scale-in decisions.
        /// </summary>
        Scaling = 7,

        /// <summary>
        /// Runtime execution recovery, failed instance reconciliation, requeue, restore, and forensic repair operations.
        /// </summary>
        Recovery = 8,

        /// <summary>
        /// Recursive Child DAG execution, completion, and durable parent continuation operations.
        /// </summary>
        ChildDag = 9,

        /// <summary>
        /// Canonical policy evaluation and policy decision observation.
        /// </summary>
        Policy = 10
    }
}