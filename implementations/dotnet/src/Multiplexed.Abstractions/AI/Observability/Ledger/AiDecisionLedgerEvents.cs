namespace Multiplexed.Abstractions.AI.Observability.Ledger
{
    /// <summary>
    /// Provides stable decision ledger event type constants grouped by runtime domain.
    /// </summary>
    /// <remarks>
    /// Event types are represented as string constants to keep the ledger extensible
    /// without creating a very large event enum.
    /// </remarks>
    public static class AiDecisionLedgerEvents
    {
        /// <summary>
        /// Provides execution lifecycle event types.
        /// </summary>
        public static class Execution
        {
            /// <summary>
            /// Indicates that an execution was created.
            /// </summary>
            public const string Created = "execution.created";

            /// <summary>
            /// Indicates that an execution was started.
            /// </summary>
            public const string Started = "execution.started";

            /// <summary>
            /// Indicates that an execution completed successfully.
            /// </summary>
            public const string Completed = "execution.completed";

            /// <summary>
            /// Indicates that an execution failed.
            /// </summary>
            public const string Failed = "execution.failed";

            /// <summary>
            /// Indicates that an execution was cancelled.
            /// </summary>
            public const string Cancelled = "execution.cancelled";

            /// <summary>
            /// Indicates that an execution was finalized.
            /// </summary>
            public const string Finalized = "execution.finalized";
        }

        /// <summary>
        /// Provides controller run lifecycle event types.
        /// </summary>
        public static class Run
        {
            /// <summary>
            /// Indicates that a run was queued.
            /// </summary>
            public const string Queued = "run.queued";

            /// <summary>
            /// Indicates that a run was dequeued.
            /// </summary>
            public const string Dequeued = "run.dequeued";

            /// <summary>
            /// Indicates that a run started.
            /// </summary>
            public const string Started = "run.started";

            /// <summary>
            /// Indicates that a run completed.
            /// </summary>
            public const string Completed = "run.completed";

            /// <summary>
            /// Indicates that a run released runtime capacity while its execution remains durably waiting.
            /// </summary>
            public const string Suspended = "run.suspended";

            /// <summary>
            /// Indicates that a run failed.
            /// </summary>
            public const string Failed = "run.failed";

            /// <summary>
            /// Indicates that a run was cancelled.
            /// </summary>
            public const string Cancelled = "run.cancelled";
        }

        /// <summary>
        /// Provides queue lifecycle event types.
        /// </summary>
        public static class Queue
        {
            /// <summary>
            /// Indicates that the runtime queue was paused.
            /// </summary>
            public const string Paused = "queue.paused";

            /// <summary>
            /// Indicates that the runtime queue was resumed.
            /// </summary>
            public const string Resumed = "queue.resumed";
        }

        /// <summary>
        /// Provides DAG scheduling event types.
        /// </summary>
        public static class Dag
        {
            /// <summary>
            /// Indicates that a DAG step became ready.
            /// </summary>
            public const string StepBecameReady = "dag.step_became_ready";

            /// <summary>
            /// Indicates that a DAG step was blocked.
            /// </summary>
            public const string StepBlocked = "dag.step_blocked";

            /// <summary>
            /// Indicates that a DAG step was unblocked.
            /// </summary>
            public const string StepUnblocked = "dag.step_unblocked";

            /// <summary>
            /// Indicates that a DAG step was skipped.
            /// </summary>
            public const string StepSkipped = "dag.step_skipped";
        }

        /// <summary>
        /// Provides distributed claim and lease event types.
        /// </summary>
        public static class Claim
        {
            /// <summary>
            /// Indicates that a claim was attempted.
            /// </summary>
            public const string Attempted = "claim.attempted";

            /// <summary>
            /// Indicates that a claim was acquired.
            /// </summary>
            public const string Acquired = "claim.acquired";

            /// <summary>
            /// Indicates that a claim was denied.
            /// </summary>
            public const string Denied = "claim.denied";

            /// <summary>
            /// Indicates that a claim expired.
            /// </summary>
            public const string Expired = "claim.expired";

            /// <summary>
            /// Indicates that a claim was released.
            /// </summary>
            public const string Released = "claim.released";

            /// <summary>
            /// Indicates that a claim lease was renewed.
            /// </summary>
            public const string LeaseRenewed = "claim.lease_renewed";

            /// <summary>
            /// Indicates that a claim lease expired.
            /// </summary>
            public const string LeaseExpired = "claim.lease_expired";
        }

        /// <summary>
        /// Provides step execution event types.
        /// </summary>
        public static class Step
        {
            /// <summary>
            /// Indicates that a step started.
            /// </summary>
            public const string Started = "step.started";

            /// <summary>
            /// Indicates that a step completed.
            /// </summary>
            public const string Completed = "step.completed";

            /// <summary>
            /// Indicates that a step failed.
            /// </summary>
            public const string Failed = "step.failed";

            /// <summary>
            /// Indicates that a step voluntarily entered a durable external wait.
            /// </summary>
            public const string Parked = "step.parked";

            /// <summary>
            /// Indicates that a step timed out.
            /// </summary>
            public const string TimedOut = "step.timed_out";
        }

        /// <summary>
        /// Provides recovery event types.
        /// </summary>
        public static class Recovery
        {
            /// <summary>
            /// Indicates that a recoverable condition was detected.
            /// </summary>
            public const string Detected = "recovery.detected";

            /// <summary>
            /// Indicates that recovery was applied.
            /// </summary>
            public const string Applied = "recovery.applied";

            /// <summary>
            /// Indicates that a step was recovered.
            /// </summary>
            public const string StepRecovered = "recovery.step_recovered";

            /// <summary>
            /// Indicates that an execution was recovered.
            /// </summary>
            public const string ExecutionRecovered = "recovery.execution_recovered";
        }

        /// <summary>
        /// Provides retry event types.
        /// </summary>
        public static class Retry
        {
            /// <summary>
            /// Indicates that retry eligibility was evaluated.
            /// </summary>
            public const string Evaluated = "retry.evaluated";

            /// <summary>
            /// Indicates that retry was scheduled.
            /// </summary>
            public const string Scheduled = "retry.scheduled";

            /// <summary>
            /// Indicates that retry was denied.
            /// </summary>
            public const string Denied = "retry.denied";

            /// <summary>
            /// Indicates that a retry attempt started.
            /// </summary>
            public const string AttemptStarted = "retry.attempt_started";

            /// <summary>
            /// Indicates that a retry attempt completed.
            /// </summary>
            public const string AttemptCompleted = "retry.attempt_completed";

            /// <summary>
            /// Indicates that the retry budget was exhausted.
            /// </summary>
            public const string BudgetExhausted = "retry.budget_exhausted";
        }

        /// <summary>
        /// Provides policy evaluation event types.
        /// </summary>
        public static class Policy
        {
            /// <summary>
            /// Indicates that a policy was evaluated.
            /// </summary>
            public const string Evaluated = "policy.evaluated";

            /// <summary>
            /// Indicates that a policy allowed the operation.
            /// </summary>
            public const string Allowed = "policy.allowed";

            /// <summary>
            /// Indicates that a policy denied the operation.
            /// </summary>
            public const string Denied = "policy.denied";

            /// <summary>
            /// Indicates that a policy was skipped.
            /// </summary>
            public const string Skipped = "policy.skipped";

            /// <summary>
            /// Indicates that a policy failed.
            /// </summary>
            public const string Failed = "policy.failed";
        }

        /// <summary>
        /// Provides concurrency and throttling event types.
        /// </summary>
        public static class Concurrency
        {
            /// <summary>
            /// Indicates that concurrency was evaluated.
            /// </summary>
            public const string Evaluated = "concurrency.evaluated";

            /// <summary>
            /// Indicates that concurrency allowed the operation.
            /// </summary>
            public const string Allowed = "concurrency.allowed";

            /// <summary>
            /// Indicates that concurrency denied the operation.
            /// </summary>
            public const string Denied = "concurrency.denied";

            /// <summary>
            /// Indicates that throttling was applied.
            /// </summary>
            public const string ThrottleApplied = "concurrency.throttle_applied";

            /// <summary>
            /// Indicates that a concurrency lease was acquired.
            /// </summary>
            public const string LeaseAcquired = "concurrency.lease_acquired";

            /// <summary>
            /// Indicates that a concurrency lease was released.
            /// </summary>
            public const string LeaseReleased = "concurrency.lease_released";

            /// <summary>
            /// Indicates that a concurrency lease expired.
            /// </summary>
            public const string LeaseExpired = "concurrency.lease_expired";
        }

        /// <summary>
        /// Provides execution control state event types.
        /// </summary>
        public static class Control
        {
            /// <summary>
            /// Indicates that pause was requested.
            /// </summary>
            public const string PauseRequested = "control.pause_requested";

            /// <summary>
            /// Indicates that the execution was paused.
            /// </summary>
            public const string Paused = "control.paused";

            /// <summary>
            /// Indicates that resume was requested.
            /// </summary>
            public const string ResumeRequested = "control.resume_requested";

            /// <summary>
            /// Indicates that the execution was resumed.
            /// </summary>
            public const string Resumed = "control.resumed";

            /// <summary>
            /// Indicates that cancellation was requested.
            /// </summary>
            public const string CancelRequested = "control.cancel_requested";

            /// <summary>
            /// Indicates that cancellation was observed by the runtime.
            /// </summary>
            public const string CancelObserved = "control.cancel_observed";

            /// <summary>
            /// Indicates that execution control state changed.
            /// </summary>
            public const string StateChanged = "control.state_changed";
        }

        /// <summary>
        /// Provides human input event types.
        /// </summary>
        public static class HumanInput
        {
            /// <summary>
            /// Indicates that human input was requested.
            /// </summary>
            public const string Requested = "human_input.requested";

            /// <summary>
            /// Indicates that human input was submitted.
            /// </summary>
            public const string Submitted = "human_input.submitted";

            /// <summary>
            /// Indicates that human input was rejected.
            /// </summary>
            public const string Rejected = "human_input.rejected";

            /// <summary>
            /// Indicates that human input expired.
            /// </summary>
            public const string Expired = "human_input.expired";

            /// <summary>
            /// Indicates that execution is waiting for human input.
            /// </summary>
            public const string Waiting = "human_input.waiting";
        }

        /// <summary>
        /// Provides retention event types.
        /// </summary>
        public static class Retention
        {
            /// <summary>
            /// Indicates that retention was evaluated.
            /// </summary>
            public const string Evaluated = "retention.evaluated";

            /// <summary>
            /// Indicates that retention was triggered.
            /// </summary>
            public const string Triggered = "retention.triggered";

            /// <summary>
            /// Indicates that retention was skipped.
            /// </summary>
            public const string Skipped = "retention.skipped";

            /// <summary>
            /// Indicates that a payload or state was compacted.
            /// </summary>
            public const string Compacted = "retention.compacted";

            /// <summary>
            /// Indicates that hot state was evicted.
            /// </summary>
            public const string Evicted = "retention.evicted";
        }

        /// <summary>
        /// Provides payload event types.
        /// </summary>
        public static class Payload
        {
            /// <summary>
            /// Indicates that a payload was externalized.
            /// </summary>
            public const string Externalized = "payload.externalized";

            /// <summary>
            /// Indicates that a payload was rehydrated.
            /// </summary>
            public const string Rehydrated = "payload.rehydrated";

            /// <summary>
            /// Indicates that payload resolution failed.
            /// </summary>
            public const string ResolutionFailed = "payload.resolution_failed";
        }

        /// <summary>
        /// Provides snapshot event types.
        /// </summary>
        public static class Snapshot
        {
            /// <summary>
            /// Indicates that a snapshot was created.
            /// </summary>
            public const string Created = "snapshot.created";

            /// <summary>
            /// Indicates that a snapshot was loaded.
            /// </summary>
            public const string Loaded = "snapshot.loaded";

            /// <summary>
            /// Indicates that snapshot restore was requested.
            /// </summary>
            public const string RestoreRequested = "snapshot.restore_requested";

            /// <summary>
            /// Indicates that snapshot restore completed.
            /// </summary>
            public const string RestoreCompleted = "snapshot.restore_completed";
        }

        /// <summary>
        /// Provides storage event types.
        /// </summary>
        public static class Storage
        {
            /// <summary>
            /// Indicates that runtime state was persisted.
            /// </summary>
            public const string StatePersisted = "storage.state_persisted";

            /// <summary>
            /// Indicates that runtime state persistence failed.
            /// </summary>
            public const string StatePersistenceFailed = "storage.state_persistence_failed";
        }

        /// <summary>
        /// Provides replay event types.
        /// </summary>
        public static class Replay
        {
            /// <summary>
            /// Indicates that replay was requested.
            /// </summary>
            public const string Requested = "replay.requested";

            /// <summary>
            /// Indicates that replay started.
            /// </summary>
            public const string Started = "replay.started";

            /// <summary>
            /// Indicates that replay completed.
            /// </summary>
            public const string Completed = "replay.completed";

            /// <summary>
            /// Indicates that replay failed.
            /// </summary>
            public const string Failed = "replay.failed";

            /// <summary>
            /// Indicates that replay comparison completed.
            /// </summary>
            public const string ComparisonCompleted = "replay.comparison_completed";

            /// <summary>
            /// Indicates that replay convergence proof started.
            /// </summary>
            public const string ConvergenceProofStarted = "replay.convergence_proof_started";

            /// <summary>
            /// Indicates that replay convergence proof completed.
            /// </summary>
            public const string ConvergenceProofCompleted = "replay.convergence_proof_completed";

            /// <summary>
            /// Indicates that replay convergence proof failed.
            /// </summary>
            public const string ConvergenceProofFailed = "replay.convergence_proof_failed";
        }

        /// <summary>
        /// Provides finalization event types.
        /// </summary>
        public static class Finalization
        {
            /// <summary>
            /// Indicates that finalization started.
            /// </summary>
            public const string Started = "finalization.started";

            /// <summary>
            /// Indicates that finalization completed.
            /// </summary>
            public const string Completed = "finalization.completed";

            /// <summary>
            /// Indicates that finalization failed.
            /// </summary>
            public const string Failed = "finalization.failed";

            /// <summary>
            /// Indicates that cancellation finalization override was applied.
            /// </summary>
            public const string CancellationOverrideApplied = "finalization.cancellation_override_applied";

            /// <summary>
            /// Indicates that a distributed finalization attempt lost the optimistic
            /// finalization race because another worker already finalized or updated
            /// the execution.
            /// </summary>
            /// <remarks>
            /// This is not a system failure. It is an expected distributed coordination
            /// outcome when multiple workers observe terminal convergence at the same time.
            /// Only one worker can successfully finalize the execution.
            /// </remarks>
            public const string RaceLost = "finalization.race_lost";
        }
    }
}