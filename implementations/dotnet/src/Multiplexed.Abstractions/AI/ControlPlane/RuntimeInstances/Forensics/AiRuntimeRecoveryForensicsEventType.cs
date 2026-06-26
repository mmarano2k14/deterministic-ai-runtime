namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Defines known runtime recovery forensics event types.
    /// </summary>
    public static class AiRuntimeRecoveryForensicsEventType
    {
        /// <summary>
        /// Runtime failure was detected.
        /// </summary>
        public const string RuntimeFailureDetected = "runtime.failure.detected";

        /// <summary>
        /// Runtime health was suppressed or marked unsafe.
        /// </summary>
        public const string RuntimeHealthSuppressed = "runtime.health.suppressed";

        /// <summary>
        /// Runtime capacity was removed or suppressed.
        /// </summary>
        public const string RuntimeCapacityRemoved = "runtime.capacity.removed";

        /// <summary>
        /// A recovery candidate was detected.
        /// </summary>
        public const string ExecutionRecoveryCandidateDetected = "execution.recovery.candidate.detected";

        /// <summary>
        /// A shared run was requeued for resume.
        /// </summary>
        public const string SharedRunRequeuedForResume = "shared.run.requeued.for.resume";

        /// <summary>
        /// A failed local run was marked requeued for recovery.
        /// </summary>
        public const string FailedLocalRunMarkedRequeuedForRecovery = "failed.local.run.marked.requeued.for.recovery";

        /// <summary>
        /// A replacement runtime was selected.
        /// </summary>
        public const string ReplacementRuntimeSelected = "replacement.runtime.selected";

        /// <summary>
        /// A replacement local run was registered.
        /// </summary>
        public const string ReplacementLocalRunRegistered = "replacement.local.run.registered";

        /// <summary>
        /// Resume context was seeded.
        /// </summary>
        public const string ResumeContextSeeded = "resume.context.seeded";

        /// <summary>
        /// DAG resume started.
        /// </summary>
        public const string DagResumeStarted = "dag.resume.started";

        /// <summary>
        /// DAG resume completed.
        /// </summary>
        public const string DagResumeCompleted = "dag.resume.completed";

        /// <summary>
        /// Execution recovery completed.
        /// </summary>
        public const string ExecutionRecoveryCompleted = "execution.recovery.completed";

        /// <summary>
        /// Execution recovery failed.
        /// </summary>
        public const string ExecutionRecoveryFailed = "execution.recovery.failed";
    }
}