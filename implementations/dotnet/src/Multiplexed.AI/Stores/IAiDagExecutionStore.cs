using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Execution.Engine.Models;
using Multiplexed.AI.Runtime.Execution.Retention.Models;

namespace Multiplexed.AI.Stores
{
    /// <summary>
    /// Distributed DAG execution store.
    ///
    /// This store enables:
    /// - step-level claiming
    /// - concurrent execution across workers
    /// - atomic step transitions
    /// - recovery of timed-out claims
    /// - administrative persistence and cleanup of distributed DAG execution state
    /// </summary>
    public interface IAiDagExecutionStore
    {
        Task CreateAsync(
            AiExecutionRecord record,
            AiExecutionState state,
            CancellationToken cancellationToken = default);

        Task<AiExecutionRecord?> GetRecordAsync(
            string executionId,
            CancellationToken cancellationToken = default);

        Task<AiExecutionState?> GetStateAsync(
            string executionId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Saves the global execution record independently.
        ///
        /// This is intended for administrative persistence or lifecycle updates
        /// that do not require re-writing all distributed step entries.
        /// </summary>
        Task SaveRecordAsync(
            AiExecutionRecord record,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Saves the full distributed DAG state by replacing the current indexed steps
        /// for the specified execution.
        ///
        /// This is intended for administrative persistence, repair, or recovery flows,
        /// not for normal concurrent worker progression.
        /// </summary>
        Task SaveStateAsync(
            string executionId,
            AiExecutionState state,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes the global execution record.
        ///
        /// This operation should be idempotent.
        /// </summary>
        Task DeleteRecordAsync(
            string executionId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes the global execution state.
        ///
        /// This operation should be idempotent.
        /// </summary>
        Task DeleteStateAsync(
            string executionId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes all indexed distributed DAG steps for the execution,
        /// including the step index itself.
        ///
        /// This operation should be idempotent.
        /// </summary>
        Task DeleteStepsAsync(
            string executionId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes the full distributed DAG execution bundle owned by this store.
        ///
        /// This includes the global execution record and all indexed step entries.
        /// This operation should be idempotent.
        /// </summary>
        Task DeleteExecutionBundleAsync(
            string executionId,
            CancellationToken cancellationToken = default);

        Task<AiClaimedStep?> TryClaimNextReadyStepAsync(
            string executionId,
            string workerId,
            CancellationToken cancellationToken = default);

        Task<bool> TryCompleteStepAsync(
            string executionId,
            string stepName,
            string claimToken,
            AiStepResult result,
            CancellationToken cancellationToken = default);

        Task<bool> TryFailStepAsync(
            string executionId,
            string stepName,
            string claimToken,
            string? error,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Atomically parks a claimed running step while it waits for an external durable condition.
        /// </summary>
        /// <remarks>
        /// The transition is accepted only when the step is running and the supplied claim token
        /// owns the current claim. Implementations must clear claim ownership without incrementing
        /// business retry or infrastructure recovery counters.
        /// </remarks>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="stepName">The claimed step name.</param>
        /// <param name="claimToken">The claim token that owns the running step.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><c>true</c> when the park transition was committed; otherwise <c>false</c>.</returns>
        Task<bool> TryParkStepAsync(
            string executionId,
            string stepName,
            string claimToken,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Atomically reactivates one step whose external durable condition has been satisfied.
        /// </summary>
        /// <remarks>
        /// The transition is valid only from <see cref="AiStepExecutionStatus.WaitingForExternal"/> to
        /// <see cref="AiStepExecutionStatus.Ready"/>. Implementations must not increment retry or recovery counters
        /// and must not use crash-recovery ownership semantics.
        /// </remarks>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="stepName">The waiting step name.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><c>true</c> when the waiting step was reactivated; otherwise <c>false</c>.</returns>
        Task<bool> TryResumeExternalWaitingStepAsync(
            string executionId,
            string stepName,
            CancellationToken cancellationToken = default);

        Task<int> RecoverTimedOutStepsAsync(
            string executionId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Recovers all currently running DAG steps as part of an explicit
        /// runtime execution recovery transition.
        /// </summary>
        /// <remarks>
        /// This operation must be called only after durable recovery pause
        /// ownership has been acquired for the execution.
        ///
        /// Unlike timed-out recovery, this operation does not wait for claim
        /// lease expiration. Every running step is returned to the ready state,
        /// claim ownership is cleared, and infrastructure recovery count is
        /// incremented.
        ///
        /// The operation must be atomic and idempotent.
        /// </remarks>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The number of running steps recovered.</returns>
        Task<int> RecoverRunningStepsForRecoveryAsync(
            string executionId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Attempts to atomically finalize the global execution record after convergence evaluation.
        ///
        /// This operation is terminal-only and must never be used to project non-terminal states
        /// such as <see cref="AiExecutionStatus.Running"/> or <see cref="AiExecutionStatus.Waiting"/>.
        ///
        /// GUARANTEES:
        /// - the execution record must exist
        /// - the <see cref="AiExecutionRecord.ExecutionStepKey"/> must match the expected value
        /// - terminal states are monotonic and cannot be downgraded
        /// - concurrent finalization attempts are resolved atomically
        ///
        /// RETURNS:
        /// - <c>true</c> if this worker successfully finalized the execution
        /// - <c>false</c> if another worker already finalized the execution,
        ///   if the optimistic execution key did not match,
        ///   or if the record was already terminal
        /// </summary>
        /// <param name="request">The terminal finalization request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>True if finalization succeeded; otherwise false.</returns>
        Task<bool> TryFinalizeExecutionAsync(
            AiDagExecutionFinalizationRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Restores an execution record and state without concurrency checks.
        /// 
        /// PURPOSE:
        /// - Used by replay systems to rehydrate execution state after crash/restart
        /// - Bypasses optimistic concurrency mechanisms
        /// 
        /// BEHAVIOR:
        /// - Must overwrite existing record/state if present
        /// - Must be idempotent
        /// </summary>
        Task RestoreAsync(
            AiExecutionRecord record,
            AiExecutionState state,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes one hot DAG step from the distributed step store and removes it from the execution step index.
        /// </summary>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="stepName">The step name to remove from hot distributed state.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        Task DeleteStepAsync(
            string executionId,
            string stepName,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Attempts to atomically claim multiple ready DAG steps for execution.
        ///
        /// PURPOSE:
        /// - Supports bounded parallel DAG execution.
        /// - Allows multiple independent ready steps to be claimed in a single operation.
        /// - Preserves distributed execution safety across multiple runtime instances.
        ///
        /// BEHAVIOR:
        /// - Only steps in a ready state may be claimed.
        /// - Dependency validation must occur before a step is claimed.
        /// - Successfully claimed steps transition to a running state.
        /// - Each claimed step receives a unique distributed claim token.
        ///
        /// IMPORTANT:
        /// - The implementation must remain atomic for each claimed step.
        /// - Distributed claim ownership must be enforced.
        /// - Returned steps are expected to be executed by the caller.
        /// </summary>
        /// <param name="executionId">
        /// The unique execution identifier.
        /// </param>
        /// <param name="workerId">
        /// The unique worker identifier requesting the claims.
        /// </param>
        /// <param name="maxSteps">
        /// The maximum number of ready steps to claim.
        /// </param>
        /// <param name="cancellationToken">
        /// The cancellation token.
        /// </param>
        /// <returns>
        /// The collection of successfully claimed DAG steps.
        /// </returns>
        Task<IReadOnlyList<AiClaimedStep>> TryClaimReadyStepsAsync(
            string executionId,
            string workerId,
            int maxSteps,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets ready DAG steps that may be evaluated for distributed claim acquisition.
        /// </summary>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="maxSteps">The maximum number of ready steps to return.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The ready DAG steps.</returns>
        Task<IReadOnlyList<AiClaimedStep>> GetReadyStepsAsync(
            string executionId,
            int maxSteps,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Attempts to atomically claim a specific ready DAG step.
        /// </summary>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="stepName">The step name to claim.</param>
        /// <param name="workerId">The worker identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// The claimed step when successful; otherwise <c>null</c>.
        /// </returns>
        Task<AiClaimedStep?> TryClaimStepAsync(
            string executionId,
            string stepName,
            string workerId,
            CancellationToken cancellationToken = default);


        /// <summary>
        /// Applies a distributed-safe atomic retention patch to the hot execution state.
        /// </summary>
        /// <param name="executionId">
        /// The execution identifier.
        /// </param>
        /// <param name="candidates">
        /// The retention patch candidates to evaluate against the current stored execution state.
        /// </param>
        /// <param name="cancellationToken">
        /// A token used to cancel the operation.
        /// </param>
        /// <returns>
        /// The result of the atomic retention patch operation.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Implementations must not overwrite the full execution state.
        /// Instead, they must atomically inspect and patch only the selected step entries.
        /// </para>
        ///
        /// <para>
        /// A candidate may be applied only when the current stored step still matches the expected
        /// status, claim token, and worker id provided by the candidate.
        /// </para>
        ///
        /// <para>
        /// For <see cref="AiRetentionPatchAction.Compact"/>, the implementation should preserve
        /// the step shell in hot state while removing or replacing heavy inline payload data with
        /// an archived payload reference.
        /// </para>
        ///
        /// <para>
        /// For <see cref="AiRetentionPatchAction.Evict"/>, the implementation should remove the
        /// step from hot state only when it is still terminal and unclaimed.
        /// </para>
        ///
        /// <para>
        /// If a candidate is no longer safe to modify, the implementation must skip it rather than
        /// throwing or replacing state owned by another worker.
        /// </para>
        /// </remarks>
        Task<AiRetentionPatchResult> TryApplyRetentionPatchAsync(
            string executionId,
            IReadOnlyCollection<AiRetentionPatchCandidate> candidates,
            CancellationToken cancellationToken = default);
    }
}