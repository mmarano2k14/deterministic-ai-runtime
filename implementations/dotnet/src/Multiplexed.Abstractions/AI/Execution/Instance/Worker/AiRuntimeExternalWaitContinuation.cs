using Multiplexed.Abstractions.AI.Execution;

namespace Multiplexed.Abstractions.AI.Execution.Instance.Worker
{
    /// <summary>
    /// Identifies one normal continuation of an existing DAG step that is durably waiting for an external condition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This contract is deliberately narrower than crash-recovery resume. It may reactivate only one known step
    /// on one existing execution and is valid only while that step is in
    /// <see cref="AiStepExecutionStatus.WaitingForExternal"/>.
    /// </para>
    /// <para>
    /// <see cref="ContinuationId"/> must be stable across duplicate physical delivery so all re-drive attempts can be
    /// correlated to one logical continuation without reusing crash-recovery ownership metadata. Shared and local
    /// runtime run identifiers remain physical delivery attempts and are not part of the continuation identity.
    /// </para>
    /// </remarks>
    public sealed class AiRuntimeExternalWaitContinuation
    {
        /// <summary>
        /// Gets the existing durable execution identifier to continue.
        /// </summary>
        public required string ExecutionId { get; init; }

        /// <summary>
        /// Gets the exact DAG step name that is waiting for the external condition.
        /// </summary>
        public required string StepName { get; init; }

        /// <summary>
        /// Gets the deterministic continuation identity used for idempotent physical redelivery.
        /// </summary>
        public required string ContinuationId { get; init; }
    }
}
