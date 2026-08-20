namespace Multiplexed.Abstractions.AI.Execution.Instance.Worker
{
    /// <summary>
    /// Defines the canonical metadata keys used to describe one durable external-wait continuation.
    /// </summary>
    /// <remarks>
    /// These keys are shared by continuation scheduling, runtime queue acceptance, execution binding,
    /// and diagnostic ledger metadata. Their physical values are durable semantic contracts and must
    /// not be redeclared by individual runtime components.
    /// </remarks>
    public static class AiRuntimeExternalWaitMetadataKeys
    {
        /// <summary>
        /// Gets the metadata key indicating whether the runtime request represents an external-wait continuation.
        /// </summary>
        public const string Continuation = "external.wait.continuation";

        /// <summary>
        /// Gets the metadata key containing the deterministic external-wait continuation identifier.
        /// </summary>
        public const string ContinuationId = "external.wait.continuation.id";

        /// <summary>
        /// Gets the metadata key containing the durable execution identifier resumed by the continuation.
        /// </summary>
        public const string ExecutionId = "external.wait.execution.id";

        /// <summary>
        /// Gets the metadata key containing the exact DAG step waiting for the external continuation.
        /// </summary>
        public const string Step = "external.wait.step";
    }
}
