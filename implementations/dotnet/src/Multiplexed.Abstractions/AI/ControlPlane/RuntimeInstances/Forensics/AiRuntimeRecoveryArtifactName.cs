namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Defines known artifact names used in runtime recovery forensics reports.
    /// </summary>
    public static class AiRuntimeRecoveryArtifactName
    {
        /// <summary>
        /// Durable execution identifier.
        /// </summary>
        public const string DurableExecutionId = "DurableExecutionId";

        /// <summary>
        /// Durable DAG execution record.
        /// </summary>
        public const string DagExecutionRecord = "DagExecutionRecord";

        /// <summary>
        /// Durable DAG state.
        /// </summary>
        public const string DagState = "DagState";

        /// <summary>
        /// Completed DAG steps.
        /// </summary>
        public const string CompletedDagSteps = "CompletedDagSteps";

        /// <summary>
        /// Execution context snapshot.
        /// </summary>
        public const string ExecutionContextSnapshot = "ExecutionContextSnapshot";

        /// <summary>
        /// Rehydrated RBAC execution context.
        /// </summary>
        public const string RehydratedRbacContext = "RehydratedRbacContext";

        /// <summary>
        /// Shared run metadata.
        /// </summary>
        public const string SharedRunMetadata = "SharedRunMetadata";

        /// <summary>
        /// Recovery metadata.
        /// </summary>
        public const string RecoveryMetadata = "RecoveryMetadata";

        /// <summary>
        /// Replacement runtime instance.
        /// </summary>
        public const string ReplacementRuntimeInstance = "ReplacementRuntimeInstance";

        /// <summary>
        /// Replacement local runtime run identifier.
        /// </summary>
        public const string ReplacementLocalRunId = "ReplacementLocalRunId";

        /// <summary>
        /// Replacement local queue item.
        /// </summary>
        public const string ReplacementLocalQueueItem = "ReplacementLocalQueueItem";

        /// <summary>
        /// Runtime run execution index entry.
        /// </summary>
        public const string RuntimeRunExecutionIndexEntry = "RuntimeRunExecutionIndexEntry";

        /// <summary>
        /// New dispatch assignment.
        /// </summary>
        public const string DispatchAssignment = "DispatchAssignment";

        /// <summary>
        /// New worker claim token.
        /// </summary>
        public const string NewClaimToken = "NewClaimToken";

        /// <summary>
        /// New worker lease.
        /// </summary>
        public const string NewLease = "NewLease";

        /// <summary>
        /// Failed runtime local queue memory.
        /// </summary>
        public const string FailedRuntimeLocalQueueMemory = "FailedRuntimeLocalQueueMemory";

        /// <summary>
        /// Failed runtime process memory.
        /// </summary>
        public const string FailedRuntimeProcessMemory = "FailedRuntimeProcessMemory";

        /// <summary>
        /// Old worker ownership.
        /// </summary>
        public const string OldWorkerOwnership = "OldWorkerOwnership";

        /// <summary>
        /// Old claim token.
        /// </summary>
        public const string OldClaimToken = "OldClaimToken";

        /// <summary>
        /// Old worker lease.
        /// </summary>
        public const string OldLease = "OldLease";

        /// <summary>
        /// Old local runtime run as active work.
        /// </summary>
        public const string OldLocalRunAsActiveWork = "OldLocalRunAsActiveWork";
    }
}