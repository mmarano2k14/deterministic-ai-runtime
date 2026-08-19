using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations.Persistence;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Snapshots;
using Multiplexed.AI.Runtime.Execution.Engine.Core;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Completion
{
    /// <summary>
    /// Commits the authoritative durable outcome of a terminal child DAG execution.
    /// </summary>
    /// <remarks>
    /// Child execution state remains owned by the normal execution engine. This coordinator only projects a terminal
    /// child outcome into the parent-child relation and makes parent continuation durably pending. Duplicate identical
    /// completion is idempotent; a conflicting result digest is treated as a correctness failure.
    /// </remarks>
    public sealed class AiChildExecutionCompletionCoordinator
    {
        private readonly IAiChildExecutionRelationStore relationStore;
        private readonly IAiDagExecutionEngineServices engineServices;
        private readonly AiChildDagSnapshotService snapshotService;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiChildExecutionCompletionCoordinator"/> class.
        /// </summary>
        /// <param name="relationStore">The authoritative parent-child relation store.</param>
        /// <param name="engineServices">The existing DAG engine services used to read authoritative child state.</param>
        /// <param name="snapshotService">The immutable child DAG snapshot service.</param>
        public AiChildExecutionCompletionCoordinator(
            IAiChildExecutionRelationStore relationStore,
            IAiDagExecutionEngineServices engineServices,
            AiChildDagSnapshotService snapshotService)
        {
            this.relationStore = relationStore ?? throw new ArgumentNullException(nameof(relationStore));
            this.engineServices = engineServices ?? throw new ArgumentNullException(nameof(engineServices));
            this.snapshotService = snapshotService ?? throw new ArgumentNullException(nameof(snapshotService));
        }

        /// <summary>
        /// Commits terminal child execution state when the supplied execution belongs to a child relation.
        /// </summary>
        /// <param name="childExecutionId">The exact child execution identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// The authoritative completed relation when the execution is a terminal child; otherwise
        /// <see langword="null"/> when the execution is not a child or has not yet become terminal.
        /// </returns>
        public async Task<AiChildExecutionRelation?> CompleteIfTerminalAsync(
            string childExecutionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(childExecutionId);

            var relation = await this.relationStore
                .GetByChildExecutionIdAsync(childExecutionId, cancellationToken)
                .ConfigureAwait(false);

            if (relation is null)
            {
                return null;
            }

            var record = this.engineServices.DagStore is not null
                ? await this.engineServices.DagStore.GetRecordAsync(childExecutionId, cancellationToken).ConfigureAwait(false)
                : await this.engineServices.Store.GetRecordAsync(childExecutionId, cancellationToken).ConfigureAwait(false);

            if (record is null || !record.IsTerminal)
            {
                return null;
            }

            var state = this.engineServices.DagStore is not null
                ? await this.engineServices.DagStore.GetStateAsync(childExecutionId, cancellationToken).ConfigureAwait(false)
                : await this.engineServices.Store.GetStateAsync(childExecutionId, cancellationToken).ConfigureAwait(false);

            if (state is null)
            {
                throw new InvalidOperationException(
                    $"Terminal child execution '{childExecutionId}' does not have authoritative execution state.");
            }

            var childResult = await this.snapshotService
                .FreezeChildResultAsync(state, childExecutionId, cancellationToken)
                .ConfigureAwait(false);

            var failureReason = ResolveFailureReason(record, state);

            if (relation.Status == AiChildExecutionRelationStatus.Completed)
            {
                EnsureEquivalentCompletion(relation, childResult.ContentHash, failureReason);
                return relation;
            }

            if (relation.Status is not AiChildExecutionRelationStatus.ChildAllocated and
                not AiChildExecutionRelationStatus.Waiting)
            {
                throw new InvalidOperationException(
                    $"Child execution '{childExecutionId}' reached terminal state while relation status is '{relation.Status}'.");
            }

            var expectedStatus = relation.Status;
            relation.Status = AiChildExecutionRelationStatus.Completed;
            relation.ChildResult = childResult;
            relation.ChildFailureReason = failureReason;
            relation.CompletedAtUtc = DateTimeOffset.UtcNow;
            relation.ContinuationStatus = AiChildContinuationStatus.Pending;
            relation.ParentContinuationScheduledAtUtc = null;
            relation.ParentContinuationScheduledStepVersion = null;
            relation.ParentResumedAtUtc = null;

            var committed = await this.relationStore
                .TryReplaceAsync(relation, expectedStatus, cancellationToken)
                .ConfigureAwait(false);

            if (committed)
            {
                return relation;
            }

            var winner = await this.relationStore
                .GetByChildExecutionIdAsync(childExecutionId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Child completion CAS for execution '{childExecutionId}' lost and the authoritative relation could not be reloaded.");

            EnsureEquivalentCompletion(winner, childResult.ContentHash, failureReason);
            return winner;
        }

        /// <summary>
        /// Resolves a stable terminal failure description without changing the child execution model.
        /// </summary>
        /// <param name="record">The terminal child execution record.</param>
        /// <param name="state">The terminal child execution state.</param>
        /// <returns>The durable failure reason, or <see langword="null"/> for successful completion.</returns>
        private static string? ResolveFailureReason(
            AiExecutionRecord record,
            AiExecutionState state)
        {
            if (record.Status == AiExecutionStatus.Completed)
            {
                return null;
            }

            if (record.Status == AiExecutionStatus.Cancelled)
            {
                return "Child execution was cancelled.";
            }

            var failures = state.Steps.Values
                .Where(step => step.Status == AiStepExecutionStatus.Failed)
                .OrderBy(step => step.StepName, StringComparer.Ordinal)
                .Select(step => string.IsNullOrWhiteSpace(step.Error)
                    ? $"{step.StepName}: failed"
                    : $"{step.StepName}: {step.Error}")
                .ToArray();

            return failures.Length == 0
                ? "Child execution failed."
                : string.Join(" | ", failures);
        }

        /// <summary>
        /// Verifies that duplicate completion carries exactly the already committed authoritative outcome.
        /// </summary>
        /// <param name="relation">The already completed durable relation.</param>
        /// <param name="resultDigest">The duplicate result digest.</param>
        /// <param name="failureReason">The duplicate failure reason.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when duplicate completion conflicts with the already committed result.
        /// </exception>
        private static void EnsureEquivalentCompletion(
            AiChildExecutionRelation relation,
            string? resultDigest,
            string? failureReason)
        {
            if (relation.Status != AiChildExecutionRelationStatus.Completed || relation.ChildResult is null)
            {
                throw new InvalidOperationException(
                    $"Child relation '{relation.ChildInvocationKey}' did not converge to a completed authoritative outcome.");
            }

            if (!string.Equals(
                    relation.ChildResult.ContentHash,
                    resultDigest,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    relation.ChildFailureReason,
                    failureReason,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Conflicting duplicate completion detected for child execution '{relation.ChildExecutionId}'. The authoritative child result must never be overwritten.");
            }
        }
    }
}
