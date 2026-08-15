using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations.Persistence;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Completion;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Continuation;
using Multiplexed.AI.Runtime.Execution.Engine.Core;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Reconciliation
{
    /// <summary>
    /// Reconciles child completion, parent continuation liveness, and defensive parent-park consistency from durable state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reconciler is the liveness guarantee. It does not rely on an in-memory child-completion signal: incomplete
    /// relations are checked against normal child execution state, completed Pending continuations are durably
    /// scheduled, and Scheduled continuations are safely re-enqueued until parent step state proves resumed progress.
    /// </para>
    /// <para>
    /// It also detects the dangerous known-relation state where a parent step is already
    /// <c>WaitingForExternal</c> while the relation is still <c>ChildAllocated</c>. Repair timing is derived from the
    /// existing persisted step claim timeout plus a proportional safety margin; no independent hard-coded grace
    /// timeout is introduced.
    /// </para>
    /// </remarks>
    public sealed class AiChildContinuationReconciler
    {
        private readonly IAiChildExecutionRelationStore relationStore;
        private readonly AiChildExecutionCompletionCoordinator completionCoordinator;
        private readonly AiChildContinuationCoordinator continuationCoordinator;
        private readonly IAiDagExecutionEngineServices engineServices;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiChildContinuationReconciler"/> class.
        /// </summary>
        /// <param name="relationStore">The authoritative child relation store.</param>
        /// <param name="completionCoordinator">The child completion coordinator.</param>
        /// <param name="continuationCoordinator">The parent continuation coordinator.</param>
        /// <param name="engineServices">The existing DAG engine services, including runtime logging.</param>
        public AiChildContinuationReconciler(
            IAiChildExecutionRelationStore relationStore,
            AiChildExecutionCompletionCoordinator completionCoordinator,
            AiChildContinuationCoordinator continuationCoordinator,
            IAiDagExecutionEngineServices engineServices)
        {
            this.relationStore = relationStore ?? throw new ArgumentNullException(nameof(relationStore));
            this.completionCoordinator = completionCoordinator ?? throw new ArgumentNullException(nameof(completionCoordinator));
            this.continuationCoordinator = continuationCoordinator ?? throw new ArgumentNullException(nameof(continuationCoordinator));
            this.engineServices = engineServices ?? throw new ArgumentNullException(nameof(engineServices));
        }

        /// <summary>
        /// Runs one durable reconciliation iteration.
        /// </summary>
        /// <param name="batchSize">The maximum number of relations read from each query family.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Summary counters for the completed iteration.</returns>
        public async Task<AiChildContinuationReconciliationResult> ReconcileAsync(
            int batchSize,
            CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

            var incomplete = await this.relationStore
                .ListIncompleteAsync(batchSize, cancellationToken)
                .ConfigureAwait(false);

            var completedCount = 0;
            foreach (var relation in incomplete)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (string.IsNullOrWhiteSpace(relation.ChildExecutionId))
                    {
                        continue;
                    }

                    var completed = await this.completionCoordinator
                        .CompleteIfTerminalAsync(relation.ChildExecutionId, cancellationToken)
                        .ConfigureAwait(false);

                    if (completed is not null)
                    {
                        completedCount++;
                        await this.continuationCoordinator
                            .EnqueueContinuationAsync(completed.ToInvocationIdentity(), cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    this.engineServices.Logger.Engine.LogWarning(
                        $"Child completion reconciliation failed. " +
                        $"ChildInvocationKey='{relation.ChildInvocationKey}', " +
                        $"ChildExecutionId='{relation.ChildExecutionId ?? string.Empty}', " +
                        $"ExceptionType='{exception.GetType().FullName}', Message='{exception.Message}'.");
                }
            }

            var continuationCandidates = await this.relationStore
                .ListContinuationCandidatesAsync(batchSize, cancellationToken)
                .ConfigureAwait(false);

            foreach (var relation in continuationCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (relation.ContinuationStatus == AiChildContinuationStatus.Pending)
                    {
                        await this.continuationCoordinator
                            .EnqueueContinuationAsync(relation.ToInvocationIdentity(), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else if (relation.ContinuationStatus == AiChildContinuationStatus.Scheduled)
                    {
                        await this.continuationCoordinator
                            .ReconcileScheduledAsync(relation, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    this.engineServices.Logger.Engine.LogWarning(
                        $"Parent continuation reconciliation failed. " +
                        $"ChildInvocationKey='{relation.ChildInvocationKey}', " +
                        $"ParentExecutionId='{relation.ParentExecutionId}', " +
                        $"ContinuationStatus='{relation.ContinuationStatus}', " +
                        $"ExceptionType='{exception.GetType().FullName}', Message='{exception.Message}'.");
                }
            }

            var nowUtc = DateTimeOffset.UtcNow;
            var parkCandidates = await this.relationStore
                .ListParkConsistencyCandidatesAsync(nowUtc, batchSize, cancellationToken)
                .ConfigureAwait(false);

            var parkRepairCount = 0;
            foreach (var relation in parkCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (await this.continuationCoordinator
                            .ReconcileParkConsistencyAsync(relation, nowUtc, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        parkRepairCount++;
                        this.engineServices.Logger.Engine.LogWarning(
                            $"Detected and re-drove parent park inconsistency. " +
                            $"ChildInvocationKey='{relation.ChildInvocationKey}', " +
                            $"ParentExecutionId='{relation.ParentExecutionId}', " +
                            $"ParentCallSiteId='{relation.ParentCallSiteId}'.");
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    this.engineServices.Logger.Engine.LogWarning(
                        $"Parent park consistency reconciliation failed. " +
                        $"ChildInvocationKey='{relation.ChildInvocationKey}', " +
                        $"ParentExecutionId='{relation.ParentExecutionId}', " +
                        $"ParentCallSiteId='{relation.ParentCallSiteId}', " +
                        $"ExceptionType='{exception.GetType().FullName}', Message='{exception.Message}'.");
                }
            }

            return new AiChildContinuationReconciliationResult
            {
                IncompleteRelationCount = incomplete.Count,
                CompletedRelationCount = completedCount,
                ContinuationCandidateCount = continuationCandidates.Count,
                ParkConsistencyCandidateCount = parkCandidates.Count,
                ParkRepairEnqueueCount = parkRepairCount
            };
        }
    }
}
