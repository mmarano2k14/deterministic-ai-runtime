using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Durable;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.AI.Stores;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime;

namespace Multiplexed.AI.Runtime.Invocation.Workers.Policies
{
    /// <summary>
    /// Restores the persisted execution owner, applies the existing publication execute
    /// capability, and materializes only the policy implementation pinned to that run.
    /// No worker identity, permission snapshot, mutable latest lookup or alternate RBAC path is introduced.
    /// </summary>
    public sealed class AiConcurrencyPolicyPublicationPreparer : IAiConcurrencyPolicyCodePreparer
    {
        private readonly IAiExecutionStore _executions;
        private readonly IAiDagExecutionStore? _dag;
        private readonly IExecutionContextAccessor _accessor;
        private readonly IAiControlPlaneIdResolver _controlPlane;
        private readonly AiPublicationIdentity _identity;
        private readonly AiPublicationOptions _options;
        private readonly AiImmutablePublicationStore _publications;

        public AiConcurrencyPolicyPublicationPreparer(
            IAiExecutionStore executions,
            IExecutionContextAccessor accessor,
            IAiControlPlaneIdResolver controlPlane,
            AiPublicationIdentity identity,
            AiPublicationOptions options,
            AiImmutablePublicationStore publications,
            IAiDagExecutionStore? dag = null)
        {
            _executions = executions ?? throw new ArgumentNullException(nameof(executions));
            _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
            _controlPlane = controlPlane ?? throw new ArgumentNullException(nameof(controlPlane));
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _publications = publications ?? throw new ArgumentNullException(nameof(publications));
            _dag = dag;
        }

        public async Task<AiWorkerCodeBundle> PrepareAsync(
            AiConcurrencyPolicyRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateRequest(request);

            var parent = await (_dag is null
                ? _executions.GetRecordAsync(request.Context.ExecutionId, cancellationToken)
                : _dag.GetRecordAsync(request.Context.ExecutionId, cancellationToken)).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The durable parent is unavailable; custom policy evaluation is not permitted.");
            var snapshot = AiHostedPolicyOwnershipRevalidator.ValidateInitial(
                parent,
                request.Context.ExecutionId,
                request.Context.TenantId,
                request.Context.TenantGroupId,
                "Policy ownership does not match the persisted execution context.",
                "A terminal parent cannot authorize a new custom policy evaluation.");

            var controlPlaneId = await _controlPlane.ResolveAsync(cancellationToken).ConfigureAwait(false);
            var scope = new AiDurableInvocationScope(snapshot.TenantId!, snapshot.TenantGroupId!, controlPlaneId);
            var previous = _accessor.Current;
            _accessor.Set(ExecutionContextSnapshotMapper.ToExecutionContext(snapshot));
            try
            {
                var guard = await _identity.AuthorizeAsync(scope, _options.Execute, cancellationToken).ConfigureAwait(false);
                var result = await _publications.ReadPolicyWorkerCodeAsync(request, guard, cancellationToken).ConfigureAwait(false);
                guard.RequireCurrent();

                // Recheck lifecycle and owner after immutable artifact I/O. The policy remains
                // a short admission evaluation and never obtains authority over the DAG.
                var current = await (_dag is null
                    ? _executions.GetRecordAsync(request.Context.ExecutionId, cancellationToken)
                    : _dag.GetRecordAsync(request.Context.ExecutionId, cancellationToken)).ConfigureAwait(false);
                AiHostedPolicyOwnershipRevalidator.RequireCurrent(
                    current,
                    request.Context.ExecutionId,
                    snapshot,
                    "Parent ownership or lifecycle changed before custom policy evaluation.");

                guard.RequireCurrent();
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            finally
            {
                if (previous is null) _accessor.Clear();
                else _accessor.Set(previous);
            }
        }

        private static void ValidateRequest(AiConcurrencyPolicyRequest request)
        {
            AiDurableInvocationValidation.ValidateLanguage(request.ExecutionLanguage);
            AiDurableInvocationValidation.Text(request.RequestId, nameof(request.RequestId));
            AiDurableInvocationValidation.Text(request.PolicyName, nameof(request.PolicyName));
            AiDurableInvocationValidation.Text(request.ImplementationRef, nameof(request.ImplementationRef));
            AiDurableInvocationValidation.Text(request.Context.TenantId, nameof(request.Context.TenantId));
            AiDurableInvocationValidation.Text(request.Context.ExecutionId, nameof(request.Context.ExecutionId));
            AiDurableInvocationValidation.Text(request.Context.StepName, nameof(request.Context.StepName));
            if (request.Scope is not ("Pipeline" or "Step") ||
                request.Scope == "Pipeline" && request.OwnerStepName is not null ||
                request.Scope == "Step" && string.IsNullOrWhiteSpace(request.OwnerStepName) ||
                request.Scope == "Step" && request.OwnerStepName != request.Context.StepName)
            {
                throw new InvalidOperationException("Custom policy scope does not match its admission context.");
            }
            if (request.DeadlineUtc.Offset != TimeSpan.Zero)
            {
                throw new InvalidOperationException("Custom policy deadline must be UTC.");
            }
        }
    }
}
