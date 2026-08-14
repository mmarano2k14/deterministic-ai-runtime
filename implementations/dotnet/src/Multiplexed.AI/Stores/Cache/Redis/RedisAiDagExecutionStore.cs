using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Execution.Engine.Models;
using Multiplexed.AI.Runtime.Execution.Retention.Models;
using Multiplexed.AI.Stores.Creation;
using StackExchange.Redis;

namespace Multiplexed.AI.Stores.Cache.Redis
{
    /// <summary>
    /// Redis-backed distributed DAG execution store.
    /// </summary>
    /// <remarks>
    /// This class is now a thin facade over specialized Redis DAG store services.
    ///
    /// Responsibilities are delegated to:
    /// - StateReader for record/state reads.
    /// - StateWriter for record/state writes, restore, and deletion.
    /// - ClaimService for step discovery and claim operations.
    /// - TransitionService for completion, failure, and finalization.
    /// - RecoveryService for timed-out running step recovery.
    ///
    /// DAG execution uses step-level Redis coordination, Lua-backed atomic mutations,
    /// deterministic step indexes, and explicit lease expiration timestamps.
    /// </remarks>
    public sealed class RedisAiDagExecutionStore : IAiDagExecutionStore, IAiExecutionCreateIfAbsentStore
    {
        private readonly IRedisDagStoreServices _services;
        private readonly IConnectionMultiplexer _multiplexer;

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisAiDagExecutionStore"/> class.
        /// </summary>
        /// <param name="services">The shared Redis DAG store services.</param>
        public RedisAiDagExecutionStore(
            IRedisDagStoreServices services)
        {
            ArgumentNullException.ThrowIfNull(services);

            _services = services;
            _multiplexer = services.Multiplexer;
        }

        /// <summary>
        /// Creates a new distributed DAG execution in Redis.
        /// </summary>
        /// <param name="record">The execution record to persist.</param>
        /// <param name="state">The initial execution state to persist.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public async Task CreateAsync(
            AiExecutionRecord record,
            AiExecutionState state,
            CancellationToken cancellationToken = default)
        {
            await _services.StateWriter.CreateAsync(record, state, cancellationToken);
        }

        /// <summary>
        /// Atomically creates one exact distributed DAG execution bundle when it is absent.
        /// </summary>
        /// <param name="record">The execution record carrying the exact identifier.</param>
        /// <param name="state">The initial distributed DAG state.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><c>true</c> when this caller created the execution; otherwise <c>false</c>.</returns>
        public Task<bool> TryCreateIfAbsentAsync(
            AiExecutionRecord record,
            AiExecutionState state,
            CancellationToken cancellationToken = default)
        {
            return _services.StateWriter.TryCreateIfAbsentAsync(record, state, cancellationToken);
        }

        /// <summary>
        /// Retrieves the execution record for the specified execution.
        /// </summary>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The execution record when found; otherwise <c>null</c>.</returns>
        public async Task<AiExecutionRecord?> GetRecordAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            return await _services.StateReader.GetRecordAsync(executionId, cancellationToken);
        }

        /// <summary>
        /// Reconstructs the distributed DAG execution state.
        /// </summary>
        /// <remarks>
        /// The reconstructed state combines the persisted state blob with indexed Redis step keys.
        /// Step keys remain the authoritative source for distributed DAG step lifecycle.
        /// </remarks>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The reconstructed execution state when found; otherwise <c>null</c>.</returns>
        public async Task<AiExecutionState?> GetStateAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            return await _services.StateReader.GetStateAsync(executionId, cancellationToken);
        }

        /// <summary>
        /// Saves the execution record independently from DAG step state.
        /// </summary>
        /// <param name="record">The execution record to persist.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public async Task SaveRecordAsync(
            AiExecutionRecord record,
            CancellationToken cancellationToken = default)
        {
            await _services.StateWriter.SaveRecordAsync(record, cancellationToken);
        }

        /// <summary>
        /// Saves the full distributed DAG execution state.
        /// </summary>
        /// <remarks>
        /// This path is intended for administrative persistence, restore, or recovery flows.
        /// Normal concurrent step progression should use claim, complete, fail, and recovery operations.
        /// </remarks>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <param name="state">The execution state to persist.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public async Task SaveStateAsync(
            string executionId,
            AiExecutionState state,
            CancellationToken cancellationToken = default)
        {
            await _services.StateWriter.SaveStateAsync(executionId, state, cancellationToken);
        }

        /// <summary>
        /// Deletes the execution record.
        /// </summary>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public async Task DeleteRecordAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            await _services.StateWriter.DeleteRecordAsync(executionId, cancellationToken);
        }

        /// <summary>
        /// Deletes the persisted DAG execution state.
        /// </summary>
        /// <remarks>
        /// In DAG mode, state includes the state blob, step keys, and step index.
        /// Deleting only step keys is not sufficient.
        /// </remarks>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public async Task DeleteStateAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            await _services.StateWriter.DeleteStateAsync(executionId, cancellationToken);
        }

        /// <summary>
        /// Deletes all distributed DAG step keys and the execution step index.
        /// </summary>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public async Task DeleteStepsAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            await _services.StateWriter.DeleteStepsAsync(executionId, cancellationToken);
        }

        /// <summary>
        /// Deletes the full distributed DAG execution bundle.
        /// </summary>
        /// <remarks>
        /// This removes the execution record, state blob, indexed step keys, and step index.
        /// </remarks>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public async Task DeleteExecutionBundleAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            await _services.StateWriter.DeleteExecutionBundleAsync(executionId, cancellationToken);
        }

        /// <summary>
        /// Attempts to atomically claim the next eligible DAG step.
        /// </summary>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <param name="workerId">The worker identifier requesting the claim.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The claimed step when a step was claimed; otherwise <c>null</c>.</returns>
        public async Task<AiClaimedStep?> TryClaimNextReadyStepAsync(
            string executionId,
            string workerId,
            CancellationToken cancellationToken = default)
        {
            return await _services.ClaimService.TryClaimNextReadyStepAsync(executionId, workerId, cancellationToken);
        }

        /// <summary>
        /// Attempts to atomically complete a claimed DAG step.
        /// </summary>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <param name="stepName">The step name to complete.</param>
        /// <param name="claimToken">The claim token that owns the running step.</param>
        /// <param name="result">The step result to persist.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><c>true</c> when the completion was accepted; otherwise <c>false</c>.</returns>
        public async Task<bool> TryCompleteStepAsync(
            string executionId,
            string stepName,
            string claimToken,
            AiStepResult result,
            CancellationToken cancellationToken = default)
        {
            return await _services.TransitionService.TryCompleteStepAsync(executionId, stepName, claimToken, result, cancellationToken);
        }

        /// <summary>
        /// Attempts to atomically fail a claimed DAG step.
        /// </summary>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <param name="stepName">The step name to fail.</param>
        /// <param name="claimToken">The claim token that owns the running step.</param>
        /// <param name="error">The failure message to persist.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><c>true</c> when the failure transition was accepted; otherwise <c>false</c>.</returns>
        public async Task<bool> TryFailStepAsync(
            string executionId,
            string stepName,
            string claimToken,
            string? error,
            CancellationToken cancellationToken = default)
        {
            return await _services.TransitionService.TryFailStepAsync(executionId, stepName, claimToken, error, cancellationToken);
        }

        /// <summary>
        /// Recovers timed-out running DAG steps.
        /// </summary>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The number of recovered steps.</returns>
        public async Task<int> RecoverTimedOutStepsAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            return await _services.RecoveryService.RecoverTimedOutStepsAsync(executionId, cancellationToken);
        }

        /// <summary>
        /// Attempts to atomically finalize the global execution record.
        /// </summary>
        /// <param name="request">The finalization request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><c>true</c> when finalization succeeded; otherwise <c>false</c>.</returns>
        public async Task<bool> TryFinalizeExecutionAsync(
            AiDagExecutionFinalizationRequest request,
            CancellationToken cancellationToken = default)
        {
            return await _services.TransitionService.TryFinalizeExecutionAsync(request, cancellationToken);
        }

        /// <summary>
        /// Restores an execution record and distributed DAG state.
        /// </summary>
        /// <param name="record">The execution record to restore.</param>
        /// <param name="state">The execution state to restore.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public async Task RestoreAsync(
            AiExecutionRecord record,
            AiExecutionState state,
            CancellationToken cancellationToken = default)
        {
            await _services.StateWriter.RestoreAsync(record, state, cancellationToken);
        }

        /// <summary>
        /// Deletes one hot DAG step and removes it from the execution step index.
        /// </summary>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <param name="stepName">The step name to delete.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public async Task DeleteStepAsync(
            string executionId,
            string stepName,
            CancellationToken cancellationToken = default)
        {
            await _services.StateWriter.DeleteStepAsync(executionId, stepName, cancellationToken);
        }

        /// <summary>
        /// Attempts to atomically claim multiple eligible DAG steps.
        /// </summary>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <param name="workerId">The worker identifier requesting the claims.</param>
        /// <param name="maxSteps">The maximum number of steps to claim.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The collection of successfully claimed DAG steps.</returns>
        public async Task<IReadOnlyList<AiClaimedStep>> TryClaimReadyStepsAsync(
            string executionId,
            string workerId,
            int maxSteps,
            CancellationToken cancellationToken = default)
        {
            return await _services.ClaimService.TryClaimReadyStepsAsync(executionId, workerId, maxSteps, cancellationToken);
        }

        /// <summary>
        /// Gets ready DAG steps without claiming them.
        /// </summary>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <param name="maxSteps">The maximum number of ready steps to return.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The collection of ready DAG steps.</returns>
        public async Task<IReadOnlyList<AiClaimedStep>> GetReadyStepsAsync(
            string executionId,
            int maxSteps,
            CancellationToken cancellationToken = default)
        {
            return await _services.ClaimService.GetReadyStepsAsync(executionId, maxSteps, cancellationToken);
        }

        /// <summary>
        /// Attempts to atomically claim one specific DAG step.
        /// </summary>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <param name="stepName">The step name to claim.</param>
        /// <param name="workerId">The worker identifier requesting the claim.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The claimed step when successful; otherwise <c>null</c>.</returns>
        public async Task<AiClaimedStep?> TryClaimStepAsync(
            string executionId,
            string stepName,
            string workerId,
            CancellationToken cancellationToken = default)
        {
            return await _services.ClaimService.TryClaimStepAsync(executionId, stepName, workerId, cancellationToken);
        }

        /// <summary>
        /// Applies atomic retention patches to hot DAG step state.
        /// </summary>
        /// <param name="executionId">
        /// The execution identifier.
        /// </param>
        /// <param name="candidates">
        /// The retention patch candidates to apply.
        /// </param>
        /// <param name="cancellationToken">
        /// A token used to cancel the operation.
        /// </param>
        /// <returns>
        /// The result of the retention patch operation.
        /// </returns>
        public async Task<AiRetentionPatchResult> TryApplyRetentionPatchAsync(
            string executionId,
            IReadOnlyCollection<AiRetentionPatchCandidate> candidates,
            CancellationToken cancellationToken = default)
        {
            return await _services.TransitionService.TryApplyRetentionPatchAsync(
                    executionId,
                    candidates,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Recovers all currently running steps as part of an explicit runtime
        /// execution recovery transition.
        /// </summary>
        /// <remarks>
        /// This operation bypasses lease expiration and must be called only after
        /// durable recovery pause ownership has been acquired.
        /// </remarks>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The number of running steps recovered.</returns>
        public async Task<int> RecoverRunningStepsForRecoveryAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            return await _services.RecoveryService
                .RecoverRunningStepsForRecoveryAsync(
                    executionId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}