using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.Invocation;
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
        private readonly AiHostedPolicyPublicationPreparationCore _core;
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
            _core = new AiHostedPolicyPublicationPreparationCore(executions, accessor, controlPlane, identity, options, dag);
            _publications = publications ?? throw new ArgumentNullException(nameof(publications));
        }

        public Task<AiWorkerCodeBundle> PrepareAsync(
            AiDelegationPolicyRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            Validate(request);

            return _core.PrepareAsync(
                request,
                request.Context.ParentExecutionId,
                request.Context.TenantId,
                request.Context.TenantGroupId,
                "The durable parent is unavailable; custom Delegation evaluation is not permitted.",
                "Delegation policy ownership does not match the persisted parent execution context.",
                "A terminal parent cannot authorize a custom Delegation evaluation.",
                "Parent ownership or lifecycle changed before custom Delegation evaluation.",
                (value, guard, ct) => _publications.ReadDelegationPolicyWorkerCodeAsync(value, guard, ct),
                cancellationToken);
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
                throw new InvalidOperationException("Custom Delegation scope does not match its parent call site.");
            }

            if (request.DeadlineUtc.Offset != TimeSpan.Zero)
            {
                throw new InvalidOperationException("Custom Delegation deadline must be UTC.");
            }
        }
    }
}
