using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.Observability.Events;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations.Persistence;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.Rbac.Core.ExecutionContext;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Continuation
{
    /// <summary>
    /// Owns durable parent continuation scheduling and convergence after authoritative child completion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Continuation follows the durable lifecycle Pending -&gt; Scheduled -&gt; Resumed. Scheduling is a CAS;
    /// <see cref="AiChildContinuationStatus.Scheduled"/> remains safely re-enqueueable until the parent child-call-site
    /// reaches a terminal step state after the scheduling boundary. Ready/running/retry progress proves that a physical
    /// continuation was accepted, but not that its delivery completed, so those states remain reconcilable.
    /// </para>
    /// <para>
    /// This component never uses crash-recovery ownership. The parent is re-driven under its existing execution
    /// identifier through the normal external-wait continuation path.
    /// </para>
    /// </remarks>
    public sealed class AiChildContinuationCoordinator
    {
        private readonly IAiChildExecutionRelationStore relationStore;
        private readonly IAiControlPlaneIdResolver controlPlaneIdResolver;
        private readonly IAiDagExecutionEngineServices engineServices;
        private readonly AiChildContinuationScheduler scheduler;
        private readonly IAiControlPlaneObserver observer;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiChildContinuationCoordinator"/> class.
        /// </summary>
        /// <param name="relationStore">The authoritative child relation store.</param>
        /// <param name="controlPlaneIdResolver">The existing logical control-plane identifier resolver.</param>
        /// <param name="engineServices">The existing DAG engine services used to inspect parent execution state.</param>
        /// <param name="scheduler">The narrow existing-queue continuation scheduler.</param>
        public AiChildContinuationCoordinator(
            IAiChildExecutionRelationStore relationStore,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            IAiDagExecutionEngineServices engineServices,
            AiChildContinuationScheduler scheduler,
            IAiControlPlaneObserver? observer = null)
        {
            this.relationStore = relationStore ?? throw new ArgumentNullException(nameof(relationStore));
            this.controlPlaneIdResolver = controlPlaneIdResolver ?? throw new ArgumentNullException(nameof(controlPlaneIdResolver));
            this.engineServices = engineServices ?? throw new ArgumentNullException(nameof(engineServices));
            this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            this.observer = observer ?? new NoopAiControlPlaneObserver();
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

            await EnsureCurrentControlPlaneAuthorityAsync(relation, cancellationToken).ConfigureAwait(false);

            if (relation.Status != AiChildExecutionRelationStatus.Completed)
            {
                throw new InvalidOperationException(
                    $"Parent continuation requires an authoritative completed child relation. CurrentStatus='{relation.Status}'.");
            }

            if (relation.ContinuationStatus is AiChildContinuationStatus.Resumed or AiChildContinuationStatus.Suppressed)
            {
                return relation;
            }

            if (relation.ContinuationStatus == AiChildContinuationStatus.Pending)
            {
                var (parentRecordAtScheduling, parentStateAtScheduling) = await LoadParentAsync(relation, cancellationToken).ConfigureAwait(false);
                if (parentRecordAtScheduling.IsTerminal)
                {
                    return await SuppressContinuationAsync(
                            relation,
                            parentRecordAtScheduling,
                            AiChildContinuationStatus.Pending,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

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

                if (committed)
                {
                    await this.observer
                        .RecordAsync(
                            AiChildDagEngineEventFactory.Create(
                                relation,
                                AiEngineEvents.ChildDag.ContinuationScheduled,
                                relation.ChildInvocationKey,
                                continuationId: string.Concat("child-continuation:", relation.ChildInvocationKey),
                                timestampUtc: relation.ParentContinuationScheduledAtUtc),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    relation = await this.relationStore
                        .GetAsync(identity, cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidOperationException(
                            "Parent continuation scheduling CAS lost and the authoritative relation could not be reloaded.");
                }
            }

            if (relation.ContinuationStatus is AiChildContinuationStatus.Resumed or AiChildContinuationStatus.Suppressed)
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

            await EnsureCurrentControlPlaneAuthorityAsync(relation, cancellationToken).ConfigureAwait(false);

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
                // that the scheduled continuation was durably consumed. Any other terminal parent state suppresses the
                // continuation permanently so the durable poller cannot enqueue work for a parent that can no longer run.
                if (parentRecord.Status is AiExecutionStatus.Completed or AiExecutionStatus.Failed &&
                    step.Status is AiStepExecutionStatus.Completed or AiStepExecutionStatus.Failed &&
                    HasDurableParentProgressAfterScheduling(relation, step))
                {
                    return await MarkResumedAsync(relation, parentRecord, cancellationToken).ConfigureAwait(false);
                }

                return await SuppressContinuationAsync(
                        relation,
                        parentRecord,
                        AiChildContinuationStatus.Scheduled,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (step.Status == AiStepExecutionStatus.WaitingForExternal)
            {
                await ExecuteWithParentExecutionContextAsync(
                        parentRecord,
                        token => this.scheduler.EnqueueContinuationAsync(relation, parentRecord, token),
                        cancellationToken)
                    .ConfigureAwait(false);

                return relation;
            }

            if (step.Status is AiStepExecutionStatus.Ready or
                AiStepExecutionStatus.Running or
                AiStepExecutionStatus.WaitingForRetry)
            {
                if (HasDurableParentProgressAfterScheduling(relation, step))
                {
                    // ResumeExternalWaitingStepAsync advances WaitingForExternal -> Ready before the physical
                    // continuation run is durably finished. That acceptance boundary must not consume the durable
                    // Scheduled relation: the same physical attempt can still fail after binding the parent ExecutionId.
                    // Re-submit the same deterministic continuation identity. The shared controller converges a healthy
                    // dispatched attempt as a no-op and requeues the exact item when its bound local run is failed.
                    await ExecuteWithParentExecutionContextAsync(
                            parentRecord,
                            token => this.scheduler.EnqueueContinuationAsync(relation, parentRecord, token),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                // Signal-before-wait race: without monotonic post-schedule progress the original parent invocation may
                // still own the step. In either case Scheduled remains the liveness authority until terminal call-site
                // state proves that the continuation was actually consumed.
                return relation;
            }

            if (step.Status is AiStepExecutionStatus.Completed or AiStepExecutionStatus.Failed)
            {
                if (HasDurableParentProgressAfterScheduling(relation, step))
                {
                    return await MarkResumedAsync(relation, parentRecord, cancellationToken).ConfigureAwait(false);
                }

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

            await EnsureCurrentControlPlaneAuthorityAsync(relation, cancellationToken).ConfigureAwait(false);

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

            await ExecuteWithParentExecutionContextAsync(
                    parentRecord,
                    token => this.scheduler.EnqueueParkRepairAsync(relation, parentRecord, token),
                    cancellationToken)
                .ConfigureAwait(false);

            return true;
        }

        /// <summary>
        /// Verifies that the current logical control plane owns durable reconciliation authority for the relation.
        /// </summary>
        /// <param name="relation">The authoritative child relation.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that completes when reconciliation authority is confirmed.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the relation has no durable control-plane authority or belongs to another logical control plane.
        /// </exception>
        private async Task EnsureCurrentControlPlaneAuthorityAsync(
            AiChildExecutionRelation relation,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(relation.ControlPlaneId))
            {
                throw new InvalidOperationException(
                    $"Child relation '{relation.ChildInvocationKey}' does not contain the durable logical control-plane authority required for reconciliation.");
            }

            var currentControlPlaneId = await this.controlPlaneIdResolver
                .ResolveAsync(cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(currentControlPlaneId))
            {
                throw new InvalidOperationException(
                    "Child continuation reconciliation requires a non-empty current logical control-plane identifier.");
            }

            if (!string.Equals(relation.ControlPlaneId, currentControlPlaneId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Child relation '{relation.ChildInvocationKey}' belongs to logical control plane '{relation.ControlPlaneId}' and cannot be reconciled by '{currentControlPlaneId}'.");
            }
        }

        /// <summary>
        /// Executes one background parent re-drive inside the durable RBAC execution context captured by the parent.
        /// </summary>
        /// <remarks>
        /// Foreground submissions continue to use the request-scoped RBAC context. Durable continuation reconciliation
        /// has no ambient MCP request, so it restores the parent snapshot only for the scheduler call and always restores
        /// the previously active context afterward. The downstream shared runtime controller therefore keeps its normal
        /// fail-closed context mapping behavior without any Child DAG-specific bypass.
        /// </remarks>
        /// <param name="parentRecord">The authoritative parent execution record.</param>
        /// <param name="operation">The background operation to execute inside the restored parent context.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task ExecuteWithParentExecutionContextAsync(
            AiExecutionRecord parentRecord,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(parentRecord);
            ArgumentNullException.ThrowIfNull(operation);

            var snapshot = parentRecord.ExecutionContextSnapshot
                ?? throw new InvalidOperationException(
                    $"Parent execution '{parentRecord.ExecutionId}' does not contain the durable execution context snapshot required for background continuation dispatch.");

            if (string.IsNullOrWhiteSpace(snapshot.TenantId))
            {
                throw new InvalidOperationException(
                    $"Parent execution '{parentRecord.ExecutionId}' contains a durable execution context snapshot without TenantId.");
            }

            var accessor = this.engineServices.Accessor;
            var previousContext = accessor.Current;
            var restoredContext = ExecutionContextSnapshotMapper.ToExecutionContext(snapshot);

            accessor.Set(restoredContext);

            try
            {
                await operation(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (previousContext is null)
                {
                    accessor.Clear();
                }
                else
                {
                    accessor.Set(previousContext);
                }
            }
        }

        /// <summary>
        /// Durably suppresses a continuation when the parent execution is already terminal.
        /// </summary>
        /// <param name="relation">The completed child relation.</param>
        /// <param name="parentRecord">The authoritative terminal parent record.</param>
        /// <param name="expectedContinuationStatus">The continuation status expected by the compare-and-swap.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The authoritative suppressed or otherwise converged relation.</returns>
        private async Task<AiChildExecutionRelation> SuppressContinuationAsync(
            AiChildExecutionRelation relation,
            AiExecutionRecord parentRecord,
            AiChildContinuationStatus expectedContinuationStatus,
            CancellationToken cancellationToken)
        {
            if (!parentRecord.IsTerminal)
            {
                throw new InvalidOperationException(
                    $"Parent execution '{parentRecord.ExecutionId}' must be terminal before child continuation can be suppressed.");
            }

            var suppressionReason =
                $"Parent execution reached terminal status '{parentRecord.Status}' before child continuation could be consumed.";

            await ExecuteWithParentExecutionContextAsync(
                    parentRecord,
                    token => this.scheduler.CancelContinuationDeliveryAsync(relation, suppressionReason, token),
                    cancellationToken)
                .ConfigureAwait(false);

            relation.ContinuationStatus = AiChildContinuationStatus.Suppressed;
            relation.ParentContinuationSuppressedAtUtc = DateTimeOffset.UtcNow;
            relation.ParentContinuationSuppressionReason = suppressionReason;
            relation.ParentResumedAtUtc = null;

            var committed = await this.relationStore
                .TryReplaceContinuationAsync(
                    relation,
                    expectedContinuationStatus,
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
                    "Parent continuation suppression CAS lost and the authoritative relation could not be reloaded.");

            if (winner.ContinuationStatus is AiChildContinuationStatus.Suppressed or AiChildContinuationStatus.Resumed)
            {
                return winner;
            }

            if (winner.ContinuationStatus == AiChildContinuationStatus.Scheduled &&
                expectedContinuationStatus == AiChildContinuationStatus.Pending)
            {
                return await ReconcileScheduledAsync(winner, cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"Parent continuation suppression CAS lost to incompatible status '{winner.ContinuationStatus}'.");
        }

        /// <summary>
        /// Marks a durable Scheduled continuation as Resumed after terminal parent call-site state proves consumption.
        /// </summary>
        /// <param name="relation">The authoritative completed/scheduled child relation.</param>
        /// <param name="parentRecord">The authoritative parent execution record whose call-site consumed the continuation.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The authoritative resumed relation.</returns>
        private async Task<AiChildExecutionRelation> MarkResumedAsync(
            AiChildExecutionRelation relation,
            AiExecutionRecord parentRecord,
            CancellationToken cancellationToken)
        {
            var cancellationReason =
                $"Parent execution '{parentRecord.ExecutionId}' child call-site '{relation.ParentCallSiteId}' reached terminal step state after scheduled continuation consumption.";

            await ExecuteWithParentExecutionContextAsync(
                    parentRecord,
                    token => this.scheduler.CancelContinuationDeliveryAsync(relation, cancellationReason, token),
                    cancellationToken)
                .ConfigureAwait(false);

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
                var continuationId = string.Concat("child-continuation:", relation.ChildInvocationKey);

                await this.observer
                    .RecordAsync(
                        AiChildDagEngineEventFactory.Create(
                            relation,
                            AiEngineEvents.ChildDag.ContinuationConsumed,
                            continuationId,
                            continuationId: continuationId,
                            timestampUtc: relation.ParentResumedAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false);

                await this.observer
                    .RecordAsync(
                        AiChildDagEngineEventFactory.Create(
                            relation,
                            AiEngineEvents.ChildDag.ParentContinuationResumed,
                            relation.ParentExecutionId,
                            continuationId: continuationId,
                            timestampUtc: relation.ParentResumedAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false);

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
