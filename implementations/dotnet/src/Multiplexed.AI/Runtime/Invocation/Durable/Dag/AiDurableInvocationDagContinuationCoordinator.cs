using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.Stores;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime;

namespace Multiplexed.AI.Runtime.Invocation.Durable.Dag
{
    /// <summary>
    /// Reconciles authoritative journal and DAG state. Queue acceptance and Ready/Running
    /// are not application evidence. No claims, retries or parent state are mutated here.
    /// </summary>
    public sealed class AiDurableInvocationDagContinuationCoordinator
    {
        private readonly AiDurableInvocationJournal _journal;
        private readonly IAiExecutionStore _store;
        private readonly IAiDagExecutionStore? _dagStore;
        private readonly IAiExecutionStepResolver _steps;
        private readonly AiDurableInvocationDagBinding _binding;
        private readonly IAiControlPlaneIdResolver _controlPlane;
        private readonly IExecutionContextAccessor _accessor;
        private readonly AiDurableInvocationDagContinuationScheduler _scheduler;
        public AiDurableInvocationDagContinuationCoordinator(AiDurableInvocationJournal journal, IAiExecutionStore store,
            IAiExecutionStepResolver steps, AiDurableInvocationDagBinding binding, IAiControlPlaneIdResolver controlPlane,
            IExecutionContextAccessor accessor, AiDurableInvocationDagContinuationScheduler scheduler, IAiDagExecutionStore? dagStore = null)
        {
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _steps = steps ?? throw new ArgumentNullException(nameof(steps));
            _binding = binding ?? throw new ArgumentNullException(nameof(binding));
            _controlPlane = controlPlane ?? throw new ArgumentNullException(nameof(controlPlane));
            _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            _dagStore = dagStore;
        }

        public async Task<AiDurableInvocationRecord?> ReconcileAsync(AiDurableInvocationScope scope,
            AiDurableInvocationIdentity identity, CancellationToken cancellationToken = default)
        {
            AiDurableInvocationValidation.ValidateAddress(scope, identity);
            if (identity.Generation != 0) throw new InvalidOperationException("This DAG bridge supports the initial logical generation only.");
            if (await _controlPlane.ResolveAsync(cancellationToken).ConfigureAwait(false) != scope.ControlPlaneId)
                throw new InvalidOperationException("The current logical control plane does not own this invocation.");
            var invocation = await _journal.GetAsync(scope, identity, cancellationToken).ConfigureAwait(false);
            if (invocation is null || !AiDurableInvocationValidation.Terminal(invocation) ||
                invocation.ContinuationStatus is AiDurableInvocationContinuationStatus.Applied or AiDurableInvocationContinuationStatus.Suppressed)
                return invocation;
            var parent = await (_dagStore is null ? _store.GetRecordAsync(identity.ExecutionId, cancellationToken) :
                _dagStore.GetRecordAsync(identity.ExecutionId, cancellationToken)).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The durable parent record is unavailable; continuation intent is retained.");
            var snapshot = parent.ExecutionContextSnapshot;
            if (parent.ExecutionId != identity.ExecutionId || snapshot?.TenantId != scope.TenantId ||
                snapshot.TenantGroupId != scope.TenantGroupId || string.IsNullOrWhiteSpace(snapshot.ContextKey))
                throw new InvalidOperationException("The durable parent snapshot does not match invocation ownership.");
            var previous = _accessor.Current;
            _accessor.Set(ExecutionContextSnapshotMapper.ToExecutionContext(snapshot));
            try
            {
                var pinned = await _binding.ReadAsync(parent, scope, identity.StepName, cancellationToken).ConfigureAwait(false);
                AiDurableInvocationDagBinding.RequireTarget(pinned, invocation.Definition.Target);
                var state = await (_dagStore is null ? _store.GetStateAsync(identity.ExecutionId, cancellationToken) :
                    _dagStore.GetStateAsync(identity.ExecutionId, cancellationToken)).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The durable parent state is unavailable; continuation intent is retained.");
                if (state.ExecutionId != identity.ExecutionId)
                    throw new InvalidOperationException("The loaded DAG state belongs to a different execution.");
                var step = await _steps.GetStepAsync(identity.ExecutionId, identity.StepName, state, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The complete call-site state is unavailable, including its archived payload.");
                if (step.StepName != identity.StepName)
                    throw new InvalidOperationException("The loaded call site has a different identity.");
                var applied = AiDurableInvocationResultMapper.IsApplied(invocation, step);
                if (parent.IsTerminal)
                {
                    // Cancel before ack: a storage/queue failure must leave a reconciliation candidate.
                    await _scheduler.CancelAsync(invocation, cancellationToken).ConfigureAwait(false);
                    return await _journal.AcknowledgeContinuationAsync(scope, identity,
                        new AiDurableInvocationContinuationAcknowledgement(invocation.OperationId, invocation.ResultSha256!,
                            applied ? AiDurableInvocationContinuationStatus.Applied : AiDurableInvocationContinuationStatus.Suppressed,
                            applied ? "Exact result receipt and terminal parent observed." : "Parent terminal without an applicable exact result receipt."),
                        cancellationToken).ConfigureAwait(false);
                }
                if ((step.Status is AiStepExecutionStatus.Completed or AiStepExecutionStatus.Failed) && !applied)
                    throw new InvalidOperationException("Terminal call-site state lacks the exact result receipt; no acknowledgement or redirection is allowed.");
                if (invocation.ContinuationStatus == AiDurableInvocationContinuationStatus.Pending)
                {
                    // An early result may arrive while the original call still owns Running. Do not
                    // interpret that status as a consumed signal or try to create another operation.
                    if (step.Status != AiStepExecutionStatus.WaitingForExternal && !applied) return invocation;
                    invocation = await _journal.MarkContinuationScheduledAsync(scope, identity, cancellationToken).ConfigureAwait(false)
                        ?? await _journal.GetAsync(scope, identity, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("Invocation disappeared during continuation scheduling.");
                }
                if (invocation.ContinuationStatus != AiDurableInvocationContinuationStatus.Scheduled) return invocation;
                if (step.Status is not (AiStepExecutionStatus.WaitingForExternal or AiStepExecutionStatus.Ready or
                    AiStepExecutionStatus.Running or AiStepExecutionStatus.WaitingForRetry or
                    AiStepExecutionStatus.Completed or AiStepExecutionStatus.Failed))
                    throw new InvalidOperationException("The call-site state cannot accept an external-wait continuation.");
                // Keep the convergence obligation until the parent is terminal. A persisted result
                // alone must not strand downstream work after a crash before execution finalization.
                await _scheduler.EnqueueAsync(invocation, parent, cancellationToken).ConfigureAwait(false);
                return invocation;
            }
            finally
            {
                if (previous is null) _accessor.Clear(); else _accessor.Set(previous);
            }
        }
    }
}
