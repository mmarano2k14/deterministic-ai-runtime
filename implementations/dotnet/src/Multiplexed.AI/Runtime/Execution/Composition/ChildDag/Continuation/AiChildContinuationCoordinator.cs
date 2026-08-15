using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations.Persistence;
using Multiplexed.AI.Runtime.Execution.Engine.Core;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Continuation
{
    /// <summary>
    /// Owns durable parent continuation scheduling and convergence after authoritative child completion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Continuation follows the durable lifecycle Pending -&gt; Scheduled -&gt; Resumed. Scheduling is a CAS;
    /// <see cref="AiChildContinuationStatus.Scheduled"/> remains safely re-enqueueable until parent step state proves
    /// durable progress beyond <see cref="AiStepExecutionStatus.WaitingForExternal"/>.
    /// </para>
    /// <para>
    /// This component never uses crash-recovery ownership. The parent is re-driven under its existing execution
    /// identifier through the normal external-wait continuation path.
    /// </para>
    /// </remarks>
    public sealed class AiChildContinuationCoordinator
    {
        private readonly IAiChildExecutionRelationStore relationStore;
        private readonly IAiDagExecutionEngineServices engineServices;
        private readonly AiChildContinuationScheduler scheduler;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiChildContinuationCoordinator"/> class.
        /// </summary>
        /// <param name="relationStore">The authoritative child relation store.</param>
        /// <param name="engineServices">The existing DAG engine services used to inspect parent execution state.</param>
        /// <param name="scheduler">The narrow existing-queue continuation scheduler.</param>
        public AiChildContinuationCoordinator(
            IAiChildExecutionRelationStore relationStore,
            IAiDagExecutionEngineServices engineServices,
            AiChildContinuationScheduler scheduler)
        {
            this.relationStore = relationStore ?? throw new ArgumentNullException(nameof(relationStore));
            this.engineServices = engineServices ?? throw new ArgumentNullException(nameof(engineServices));
            this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        }

        /// <summary>
        /// Durably schedules a completed child continuation and enqueues or converges it against current parent state.
        /// </summary>
        /// <param name="identity">The authoritative child invocation identity.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The authoritative relation after scheduling/reconciliation.</returns>
        public async Task<AiChildExecutionRelation> EnqueueContinuationAsync(
            AiChildInvocationIdentity identity,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(identity);

            var relation = await this.relationStore
                .GetAsync(identity, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Parent continuation cannot be scheduled before the authoritative child relation exists.");

            if (relation.Status != AiChildExecutionRelationStatus.Completed)
            {
                throw new InvalidOperationException(
                    $"Parent continuation requires an authoritative completed child relation. CurrentStatus='{relation.Status}'.");
            }

            if (relation.ContinuationStatus == AiChildContinuationStatus.Resumed)
            {
                return relation;
            }

            if (relation.ContinuationStatus == AiChildContinuationStatus.Pending)
            {
                var (_, parentStateAtScheduling) = await LoadParentAsync(relation, cancellationToken).ConfigureAwait(false);
                if (!parentStateAtScheduling.Steps.TryGetValue(relation.ParentCallSiteId, out var parentStepAtScheduling))
                {
                    throw new InvalidOperationException(
                        $"Parent execution '{relation.ParentExecutionId}' does not contain child call-site step '{relation.ParentCallSiteId}'.");
                }

                relation.ContinuationStatus = AiChildContinuationStatus.Scheduled;
                relation.ParentContinuationScheduledAtUtc = DateTimeOffset.UtcNow;
                relation.ParentContinuationScheduledStepVersion = parentStepAtScheduling.Version;
                relation.ParentResumedAtUtc = null;

                var committed = await this.relationStore
                    .TryReplaceContinuationAsync(
                        relation,
                        AiChildContinuationStatus.Pending,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!committed)
                {
                    relation = await this.relationStore
                        .GetAsync(identity, cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidOperationException(
                            "Parent continuation scheduling CAS lost and the authoritative relation could not be reloaded.");
                }
            }

            if (relation.ContinuationStatus == AiChildContinuationStatus.Resumed)
            {
                return relation;
            }

            if (relation.ContinuationStatus != AiChildContinuationStatus.Scheduled)
            {
                throw new InvalidOperationException(
                    $"Completed child relation has unsupported continuation status '{relation.ContinuationStatus}'.");
            }

            return await ReconcileScheduledAsync(relation, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Reconciles one durable Scheduled continuation with current parent execution/step state.
        /// </summary>
        /// <param name="relation">The authoritative completed/scheduled relation.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The authoritative relation after any resumed-state CAS.</returns>
        public async Task<AiChildExecutionRelation> ReconcileScheduledAsync(
            AiChildExecutionRelation relation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(relation);

            if (relation.Status != AiChildExecutionRelationStatus.Completed ||
                relation.ContinuationStatus != AiChildContinuationStatus.Scheduled)
            {
                throw new InvalidOperationException(
                    $"Scheduled continuation reconciliation requires Completed/Scheduled state. RelationStatus='{relation.Status}', ContinuationStatus='{relation.ContinuationStatus}'.");
            }

            var (parentRecord, parentState) = await LoadParentAsync(relation, cancellationToken).ConfigureAwait(false);

            if (!parentState.Steps.TryGetValue(relation.ParentCallSiteId, out var step))
            {
                throw new InvalidOperationException(
                    $"Parent execution '{relation.ParentExecutionId}' does not contain child call-site step '{relation.ParentCallSiteId}'.");
            }

            if (parentRecord.IsTerminal)
            {
                // A continuation can finish the whole parent before the poller observes the intermediate Ready/Running
                // states. Completed/Failed parent + terminal call-site + monotonic step-version progress therefore proves
                // that the scheduled continuation was durably consumed. Cancellation remains deferred to the explicit
                // terminal/orphan semantics because it can occur independently of continuation consumption.
                if (parentRecord.Status is AiExecutionStatus.Completed or AiExecutionStatus.Failed &&
                    step.Status is AiStepExecutionStatus.Completed or AiStepExecutionStatus.Failed &&
                    HasDurableParentProgressAfterScheduling(relation, step))
                {
                    return await MarkResumedAsync(relation, cancellationToken).ConfigureAwait(false);
                }

                return relation;
            }

            if (step.Status == AiStepExecutionStatus.WaitingForExternal)
            {
                await this.scheduler
                    .EnqueueContinuationAsync(relation, parentRecord, cancellationToken)
                    .ConfigureAwait(false);

                return relation;
            }

            if (IsContinuationProgressStatus(step.Status))
            {
                if (HasDurableParentProgressAfterScheduling(relation, step))
                {
                    return await MarkResumedAsync(relation, cancellationToken).ConfigureAwait(false);
                }

                // Signal-before-wait race: the child may complete and schedule while the original parent invocation is
                // still Running (or otherwise has pre-schedule state). That state does not prove the continuation was
                // consumed. Keep Scheduled durable and let the original invocation or the poller converge later.
                return relation;
            }

            throw new InvalidOperationException(
                $"Scheduled parent continuation '{relation.ChildInvocationKey}' cannot reconcile from parent step status '{step.Status}'.");
        }

        /// <summary>
        /// Detects and safely re-drives the dangerous state where the parent step is parked but the relation still
        /// remains ChildAllocated after the existing claim/lease timing window plus a safety margin.
        /// </summary>
        /// <param name="relation">The suspicious ChildAllocated relation.</param>
        /// <param name="nowUtc">The reconciliation timestamp.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><c>true</c> when a repair re-drive was enqueued; otherwise <c>false</c>.</returns>
        public async Task<bool> ReconcileParkConsistencyAsync(
            AiChildExecutionRelation relation,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(relation);

            if (relation.Status != AiChildExecutionRelationStatus.ChildAllocated)
            {
                return false;
            }

            var (parentRecord, parentState) = await LoadParentAsync(relation, cancellationToken).ConfigureAwait(false);
            if (parentRecord.IsTerminal)
            {
                return false;
            }

            if (!parentState.Steps.TryGetValue(relation.ParentCallSiteId, out var step))
            {
                throw new InvalidOperationException(
                    $"Parent execution '{relation.ParentExecutionId}' does not contain child call-site step '{relation.ParentCallSiteId}'.");
            }

            if (step.Status != AiStepExecutionStatus.WaitingForExternal)
            {
                return false;
            }

            if (!relation.ChildAllocatedAtUtc.HasValue)
            {
                throw new InvalidOperationException(
                    $"ChildAllocated relation '{relation.ChildInvocationKey}' does not contain ChildAllocatedAtUtc.");
            }

            if (!step.ClaimTimeoutSeconds.HasValue || step.ClaimTimeoutSeconds.Value <= 0)
            {
                // Without the existing durable lease timing, the reconciler cannot invent a safe grace boundary.
                return false;
            }

            var claimWindow = TimeSpan.FromSeconds(step.ClaimTimeoutSeconds.Value);
            var safetyMargin = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, claimWindow.Ticks / 4));
            var grace = claimWindow + safetyMargin;

            if (nowUtc - relation.ChildAllocatedAtUtc.Value < grace)
            {
                return false;
            }

            await this.scheduler
                .EnqueueParkRepairAsync(relation, parentRecord, cancellationToken)
                .ConfigureAwait(false);

            return true;
        }

        /// <summary>
        /// Marks a durable Scheduled continuation as Resumed after parent step state proves progress.
        /// </summary>
        private async Task<AiChildExecutionRelation> MarkResumedAsync(
            AiChildExecutionRelation relation,
            CancellationToken cancellationToken)
        {
            relation.ContinuationStatus = AiChildContinuationStatus.Resumed;
            relation.ParentResumedAtUtc = DateTimeOffset.UtcNow;

            var committed = await this.relationStore
                .TryReplaceContinuationAsync(
                    relation,
                    AiChildContinuationStatus.Scheduled,
                    cancellationToken)
                .ConfigureAwait(false);

            if (committed)
            {
                return relation;
            }

            var winner = await this.relationStore
                .GetAsync(relation.ToInvocationIdentity(), cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Parent continuation resumed CAS lost and the authoritative relation could not be reloaded.");

            if (winner.ContinuationStatus != AiChildContinuationStatus.Resumed)
            {
                throw new InvalidOperationException(
                    $"Parent continuation resumed CAS lost to incompatible status '{winner.ContinuationStatus}'.");
            }

            return winner;
        }

        /// <summary>
        /// Loads the authoritative parent execution record and state without introducing a second execution reader.
        /// </summary>
        private async Task<(AiExecutionRecord Record, AiExecutionState State)> LoadParentAsync(
            AiChildExecutionRelation relation,
            CancellationToken cancellationToken)
        {
            AiExecutionRecord? record;
            AiExecutionState? state;

            if (this.engineServices.DagStore is not null)
            {
                record = await this.engineServices.DagStore
                    .GetRecordAsync(relation.ParentExecutionId, cancellationToken)
                    .ConfigureAwait(false);
                state = await this.engineServices.DagStore
                    .GetStateAsync(relation.ParentExecutionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                record = await this.engineServices.Store
                    .GetRecordAsync(relation.ParentExecutionId, cancellationToken)
                    .ConfigureAwait(false);
                state = await this.engineServices.Store
                    .GetStateAsync(relation.ParentExecutionId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (record is null || state is null)
            {
                throw new InvalidOperationException(
                    $"Parent execution '{relation.ParentExecutionId}' or its durable state could not be resolved for child continuation '{relation.ChildInvocationKey}'.");
            }

            if (record.ExecutionMode != AiExecutionMode.Dag)
            {
                throw new InvalidOperationException(
                    $"Parent execution '{relation.ParentExecutionId}' is not a DAG execution.");
            }

            return (record, state);
        }

        /// <summary>
        /// Determines whether one parent step status can represent progress after an external-wait continuation.
        /// </summary>
        private static bool IsContinuationProgressStatus(AiStepExecutionStatus status)
        {
            return status is AiStepExecutionStatus.Ready or
                AiStepExecutionStatus.Running or
                AiStepExecutionStatus.WaitingForRetry or
                AiStepExecutionStatus.Completed or
                AiStepExecutionStatus.Failed;
        }

        /// <summary>
        /// Determines whether the parent step has durably mutated beyond the continuation scheduling boundary.
        /// </summary>
        /// <remarks>
        /// Step status alone is insufficient because the child can complete while the original parent invocation is
        /// still Running. The monotonic durable step version closes that signal-before-wait race without relying on
        /// cross-runtime wall-clock ordering.
        /// </remarks>
        private static bool HasDurableParentProgressAfterScheduling(
            AiChildExecutionRelation relation,
            AiStepState step)
        {
            return relation.ParentContinuationScheduledStepVersion.HasValue &&
                   step.Version > relation.ParentContinuationScheduledStepVersion.Value;
        }
    }
}
