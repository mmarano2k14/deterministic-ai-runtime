namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery
{
    /// <summary>
    /// Defines metadata keys used to propagate durable runtime execution recovery context.
    /// </summary>
    /// <remarks>
    /// These keys identify recovery correlation and failed execution context only.
    /// They do not define recovery behavior, authority, or recovery mode values.
    /// </remarks>
    public static class AiRuntimeRecoveryMetadataKeys
    {
        /// <summary>
        /// Gets the metadata key that identifies the recovery forensics record.
        /// </summary>
        public const string ForensicsId = "recovery.forensicsId";

        /// <summary>
        /// Gets the metadata key that identifies the runtime failure incident.
        /// </summary>
        public const string FailureIncidentId = "recovery.failureIncidentId";

        /// <summary>
        /// Gets the metadata key that identifies the recovery Ledger entry.
        /// </summary>
        public const string LedgerEntryId = "recovery.ledgerEntryId";

        /// <summary>
        /// Gets the metadata key that identifies the recovery correlation id.
        /// </summary>
        public const string CorrelationId = "recovery.correlationId";

        /// <summary>
        /// Gets the metadata key that identifies the recovery causation id.
        /// </summary>
        public const string CausationId = "recovery.causationId";

        /// <summary>
        /// Gets the metadata key that identifies the recovery mode.
        /// </summary>
        public const string Mode = "recovery.mode";

        /// <summary>
        /// Gets the metadata key that identifies the recovery reason.
        /// </summary>
        public const string Reason = "recovery.reason";

        /// <summary>
        /// Gets the metadata key that identifies the execution being recovered.
        /// </summary>
        public const string FailedExecutionId = "recovery.failedExecutionId";

        /// <summary>
        /// Gets the metadata key that identifies the failed runtime instance.
        /// </summary>
        public const string FailedRuntimeInstanceId = "recovery.failedRuntimeInstanceId";

        /// <summary>
        /// Gets the metadata key that identifies the failed local run.
        /// </summary>
        public const string FailedLocalRunId = "recovery.failedLocalRunId";

        /// <summary>
        /// Gets the metadata key that indicates whether execution is being resumed through recovery.
        /// </summary>
        public const string Resume = "recovery.resume";

        /// <summary>
        /// Gets the metadata key that identifies the execution associated with the recovery resume context.
        /// </summary>
        public const string ExecutionId = "recovery.execution.id";

        /// <summary>
        /// Gets the metadata key that identifies the owner of the recovery operation.
        /// </summary>
        public const string OwnerId = "recovery.owner.id";

        /// <summary>
        /// Gets the metadata key that indicates a replacement request caused by recovery.
        /// </summary>
        public const string Replacement = "recovery.replacement";

        /// <summary>
        /// Gets the metadata key carrying the replacement runtime instance identifier.
        /// </summary>
        public const string ReplacementRuntimeInstanceId = "replacement.runtimeInstanceId";

        /// <summary>
        /// Gets the metadata key carrying the replacement local run identifier.
        /// </summary>
        public const string ReplacementLocalRunId = "replacement.localRunId";

        /// <summary>
        /// Gets the metadata key carrying the replacement execution identifier.
        /// </summary>
        public const string ReplacementExecutionId = "replacement.executionId";

        /// <summary>
        /// Gets the metadata key carrying the failed runtime instance identifier in recovery transition metadata.
        /// </summary>
        public const string TransitionFailedRuntimeInstanceId = "failed.runtimeInstanceId";

        /// <summary>
        /// Gets the metadata key carrying the failed local run identifier in recovery transition metadata.
        /// </summary>
        public const string TransitionFailedLocalRunId = "failed.localRunId";

        /// <summary>
        /// Gets the metadata key carrying the execution-context key used to resume recovery.
        /// </summary>
        public const string ResumeContextKey = "resume.contextKey";

        /// <summary>
        /// Gets the metadata key identifying the source of the recovery resume context.
        /// </summary>
        public const string ResumeSource = "resume.source";

        /// <summary>
        /// Gets the internal Event Manager projection key carrying the recovery forensics identifier.
        /// </summary>
        public const string ProjectionForensicsId = "recovery.projection.forensicsId";

        /// <summary>
        /// Gets the internal Event Manager projection key carrying the recovery outcome.
        /// </summary>
        public const string ProjectionOutcome = "recovery.projection.outcome";

        /// <summary>
        /// Gets the internal Event Manager projection key carrying the recovery reason.
        /// </summary>
        public const string ProjectionReason = "recovery.projection.reason";

        /// <summary>
        /// Gets the internal Event Manager projection key carrying the shared run identifier.
        /// </summary>
        public const string ProjectionSharedRunId = "recovery.projection.sharedRunId";

        /// <summary>
        /// Gets the internal Event Manager projection key carrying the local run identifier.
        /// </summary>
        public const string ProjectionLocalRunId = "recovery.projection.localRunId";
    }
}
