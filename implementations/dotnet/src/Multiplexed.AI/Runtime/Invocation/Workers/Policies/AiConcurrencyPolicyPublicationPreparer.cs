using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Durable;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.AI.Stores;
using Multiplexed.Rbac.Core.ExecutionContext;

namespace Multiplexed.AI.Runtime.Invocation.Workers.Policies
{
    /// <summary>
    /// Restores the persisted execution owner, applies the existing publication execute
    /// capability, and materializes only the policy implementation pinned to that run.
    /// No worker identity, permission snapshot, mutable latest lookup or alternate RBAC path is introduced.
    /// </summary>
    public sealed class AiConcurrencyPolicyPublicationPreparer : IAiConcurrencyPolicyCodePreparer
    {
        private readonly AiHostedPolicyPublicationPreparationCore _core;
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
            _core = new AiHostedPolicyPublicationPreparationCore(executions, accessor, controlPlane, identity, options, dag);
            _publications = publications ?? throw new ArgumentNullException(nameof(publications));
        }

        public Task<AiWorkerCodeBundle> PrepareAsync(
            AiConcurrencyPolicyRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateRequest(request);

            return _core.PrepareAsync(
                request,
                request.Context.ExecutionId,
                request.Context.TenantId,
                request.Context.TenantGroupId,
                "The durable parent is unavailable; custom policy evaluation is not permitted.",
                "Policy ownership does not match the persisted execution context.",
                "A terminal parent cannot authorize a new custom policy evaluation.",
                "Parent ownership or lifecycle changed before custom policy evaluation.",
                (value, guard, ct) => _publications.ReadPolicyWorkerCodeAsync(value, guard, ct),
                cancellationToken);
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
