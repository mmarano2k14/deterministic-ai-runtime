using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Durable;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.AI.Stores;
using Multiplexed.Rbac.Core.ExecutionContext;

namespace Multiplexed.AI.Runtime.Invocation.Workers.Policies
{
    /// <summary>Restores the durable parent owner and materializes the exact pinned Delegation implementation.</summary>
    public sealed class AiDelegationPolicyPublicationPreparer : IAiDelegationPolicyCodePreparer
    {
        private readonly IAiExecutionStore _executions;
        private readonly IAiDagExecutionStore? _dag;
        private readonly IExecutionContextAccessor _accessor;
        private readonly IAiControlPlaneIdResolver _controlPlane;
        private readonly AiPublicationIdentity _identity;
        private readonly AiPublicationOptions _options;
        private readonly AiImmutablePublicationStore _publications;

        public AiDelegationPolicyPublicationPreparer(
            IAiExecutionStore executions,
            IExecutionContextAccessor accessor,
            IAiControlPlaneIdResolver controlPlane,
            AiPublicationIdentity identity,
            AiPublicationOptions options,
            AiImmutablePublicationStore publications,
            IAiDagExecutionStore? dag = null)
        {
            _executions = executions;
            _accessor = accessor;
            _controlPlane = controlPlane;
            _identity = identity;
            _options = options;
            _publications = publications;
            _dag = dag;
        }

        public async Task<AiWorkerCodeBundle> PrepareAsync(
            AiDelegationPolicyRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            Validate(request);

            var parent = await (_dag is null
                    ? _executions.GetRecordAsync(request.Context.ParentExecutionId, cancellationToken)
                    : _dag.GetRecordAsync(request.Context.ParentExecutionId, cancellationToken))
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "The durable parent is unavailable; custom Delegation evaluation is not permitted.");

            var snapshot = parent.ExecutionContextSnapshot;
            if (snapshot?.TenantId != request.Context.TenantId ||
                snapshot.TenantGroupId != request.Context.TenantGroupId ||
                string.IsNullOrWhiteSpace(snapshot.ContextKey) ||
                string.IsNullOrWhiteSpace(snapshot.TenantGroupId))
            {
                throw new UnauthorizedAccessException(
                    "Delegation policy ownership does not match the persisted parent execution context.");
            }

            if (parent.IsTerminal)
            {
                throw new InvalidOperationException(
                    "A terminal parent cannot authorize a custom Delegation evaluation.");
            }

            var scope = new AiDurableInvocationScope(
                snapshot.TenantId!,
                snapshot.TenantGroupId!,
                await _controlPlane.ResolveAsync(cancellationToken).ConfigureAwait(false));

            var previous = _accessor.Current;
            _accessor.Set(ExecutionContextSnapshotMapper.ToExecutionContext(snapshot));
            try
            {
                var guard = await _identity
                    .AuthorizeAsync(scope, _options.Execute, cancellationToken)
                    .ConfigureAwait(false);

                var result = await _publications
                    .ReadDelegationPolicyWorkerCodeAsync(request, guard, cancellationToken)
                    .ConfigureAwait(false);
                guard.RequireCurrent();

                var current = await (_dag is null
                        ? _executions.GetRecordAsync(request.Context.ParentExecutionId, cancellationToken)
                        : _dag.GetRecordAsync(request.Context.ParentExecutionId, cancellationToken))
                    .ConfigureAwait(false);

                if (current is null ||
                    current.IsTerminal ||
                    current.ExecutionContextSnapshot?.TenantId != scope.TenantId ||
                    current.ExecutionContextSnapshot.TenantGroupId != scope.TenantGroupId ||
                    current.ExecutionContextSnapshot.UserId != snapshot.UserId)
                {
                    throw new UnauthorizedAccessException(
                        "Parent ownership or lifecycle changed before custom Delegation evaluation.");
                }

                guard.RequireCurrent();
                return result;
            }
            finally
            {
                if (previous is null)
                {
                    _accessor.Clear();
                }
                else
                {
                    _accessor.Set(previous);
                }
            }
        }

        private static void Validate(AiDelegationPolicyRequest request)
        {
            AiDurableInvocationValidation.ValidateLanguage(request.ExecutionLanguage);
            AiDurableInvocationValidation.Text(request.RequestId, nameof(request.RequestId));
            AiDurableInvocationValidation.Text(request.PolicyName, nameof(request.PolicyName));
            AiDurableInvocationValidation.Text(request.ImplementationRef, nameof(request.ImplementationRef));
            AiDurableInvocationValidation.Text(request.Context.TenantId, nameof(request.Context.TenantId));
            AiDurableInvocationValidation.Text(request.Context.ParentExecutionId, nameof(request.Context.ParentExecutionId));
            AiDurableInvocationValidation.Text(request.Context.ParentCallSiteId, nameof(request.Context.ParentCallSiteId));
            AiDurableInvocationValidation.Text(request.Context.ChildDagId, nameof(request.Context.ChildDagId));
            AiDurableInvocationValidation.Text(request.Context.ChildDagDefinitionVersion, nameof(request.Context.ChildDagDefinitionVersion));
            AiDurableInvocationValidation.Text(request.Context.ChildInvocationKey, nameof(request.Context.ChildInvocationKey));

            if (request.Context.InvocationGeneration < 0)
            {
                throw new InvalidOperationException("Custom Delegation invocation generation cannot be negative.");
            }

            if (request.Scope is not ("Pipeline" or "Step") ||
                request.Scope == "Pipeline" && request.OwnerStepName is not null ||
                request.Scope == "Step" && request.OwnerStepName != request.Context.ParentCallSiteId)
            {
                throw new InvalidOperationException(
                    "Custom Delegation scope does not match its parent call site.");
            }

            if (request.DeadlineUtc.Offset != TimeSpan.Zero)
            {
                throw new InvalidOperationException("Custom Delegation deadline must be UTC.");
            }
        }
    }
}
