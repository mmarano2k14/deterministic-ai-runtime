using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Durable.Dag;
using Multiplexed.AI.Stores;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime;

namespace Multiplexed.AI.Runtime.Publication
{
    /// <summary>
    /// Restores the original execution owner, applies the existing execute capability and
    /// materializes only that call site's exact published code. No worker principal or grants are invented.
    /// </summary>
    public sealed class AiWorkerPublicationPreparer : IAiWorkerInvocationPreparer
    {
        private readonly IAiExecutionStore _executions;
        private readonly IAiDagExecutionStore? _dag;
        private readonly IExecutionContextAccessor _accessor;
        private readonly IAiControlPlaneIdResolver _controlPlane;
        private readonly AiDurableInvocationDagBinding _binding;
        private readonly AiPublicationIdentity _identity;
        private readonly AiPublicationOptions _options;
        private readonly AiImmutablePublicationStore _publications;
        public AiWorkerPublicationPreparer(IAiExecutionStore executions, IExecutionContextAccessor accessor,
            IAiControlPlaneIdResolver controlPlane, AiDurableInvocationDagBinding binding,
            AiPublicationIdentity identity, AiPublicationOptions options, AiImmutablePublicationStore publications,
            IAiDagExecutionStore? dag = null)
        {
            _executions = executions; _accessor = accessor; _controlPlane = controlPlane;
            _binding = binding; _identity = identity; _options = options; _publications = publications; _dag = dag;
        }
        public async Task<AiWorkerCodeBundle> PrepareAsync(AiDurableInvocationRecord invocation,
            CancellationToken cancellationToken = default)
        {
            AiDurableInvocationValidation.ValidateRecord(invocation);
            var definition = invocation.Definition; var scope = definition.Scope; var address = definition.Identity;
            if (address.Generation != 0) throw new NotSupportedException("Published DAG worker execution supports the initial logical generation only.");
            if (await _controlPlane.ResolveAsync(cancellationToken).ConfigureAwait(false) != scope.ControlPlaneId)
                throw new UnauthorizedAccessException("This control plane does not own the invocation.");
            var parent = await (_dag is null ? _executions.GetRecordAsync(address.ExecutionId, cancellationToken)
                : _dag.GetRecordAsync(address.ExecutionId, cancellationToken)).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The durable parent is unavailable; worker dispatch is not permitted.");
            var snapshot = parent.ExecutionContextSnapshot;
            if (parent.ExecutionId != address.ExecutionId || snapshot?.TenantId != scope.TenantId ||
                snapshot.TenantGroupId != scope.TenantGroupId || string.IsNullOrWhiteSpace(snapshot.ContextKey))
                throw new UnauthorizedAccessException("Invocation ownership does not match the persisted execution context.");
            if (parent.IsTerminal) throw new InvalidOperationException("A terminal parent cannot authorize a new worker launch.");
            var previous = _accessor.Current;
            _accessor.Set(ExecutionContextSnapshotMapper.ToExecutionContext(snapshot));
            try
            {
                var guard = await _identity.AuthorizeAsync(scope, _options.Execute, cancellationToken).ConfigureAwait(false);
                var binding = await _binding.ReadAsync(parent, scope, address.StepName, cancellationToken).ConfigureAwait(false);
                AiDurableInvocationDagBinding.RequireTarget(binding, definition.Target);
                var result = await _publications.ReadWorkerCodeAsync(invocation, guard, cancellationToken).ConfigureAwait(false);
                guard.RequireCurrent();
                // Recheck after artifact I/O. This is not a cross-store transaction: later cancellation
                // is still reconciled by the existing DAG continuation path, never by the worker.
                var current = await (_dag is null ? _executions.GetRecordAsync(address.ExecutionId, cancellationToken)
                    : _dag.GetRecordAsync(address.ExecutionId, cancellationToken)).ConfigureAwait(false);
                if (current is null || current.IsTerminal || current.ExecutionContextSnapshot?.TenantId != scope.TenantId ||
                    current.ExecutionContextSnapshot?.TenantGroupId != scope.TenantGroupId ||
                    current.ExecutionContextSnapshot?.UserId != snapshot.UserId ||
                    current.ExecutionContextSnapshot?.Project != snapshot.Project ||
                    current.ExecutionContextSnapshot?.CurrentNamespace != snapshot.CurrentNamespace)
                    throw new UnauthorizedAccessException("Parent ownership or lifecycle changed before worker dispatch.");
                guard.RequireCurrent(); cancellationToken.ThrowIfCancellationRequested(); return result;
            }
            finally { if (previous is null) _accessor.Clear(); else _accessor.Set(previous); }
        }
    }
}
