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
    /// <summary>Restores the durable owner and materializes the exact pinned Retry implementation.</summary>
    public sealed class AiRetryPolicyPublicationPreparer : IAiRetryPolicyCodePreparer
    {
        private readonly IAiExecutionStore _executions; private readonly IAiDagExecutionStore? _dag; private readonly IExecutionContextAccessor _accessor;
        private readonly IAiControlPlaneIdResolver _controlPlane; private readonly AiPublicationIdentity _identity; private readonly AiPublicationOptions _options; private readonly AiImmutablePublicationStore _publications;
        public AiRetryPolicyPublicationPreparer(IAiExecutionStore executions,IExecutionContextAccessor accessor,IAiControlPlaneIdResolver controlPlane,AiPublicationIdentity identity,AiPublicationOptions options,AiImmutablePublicationStore publications,IAiDagExecutionStore? dag=null)
        { _executions=executions; _accessor=accessor; _controlPlane=controlPlane; _identity=identity; _options=options; _publications=publications; _dag=dag; }
        public async Task<AiWorkerCodeBundle> PrepareAsync(AiRetryPolicyRequest request,CancellationToken cancellationToken=default)
        {
            ArgumentNullException.ThrowIfNull(request); Validate(request);
            var parent=await (_dag is null?_executions.GetRecordAsync(request.Context.ExecutionId,cancellationToken):_dag.GetRecordAsync(request.Context.ExecutionId,cancellationToken)).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The durable parent is unavailable; custom Retry evaluation is not permitted.");
            var snapshot=parent.ExecutionContextSnapshot;
            if(snapshot?.TenantId!=request.Context.TenantId || snapshot.TenantGroupId!=request.Context.TenantGroupId || string.IsNullOrWhiteSpace(snapshot.ContextKey) || string.IsNullOrWhiteSpace(snapshot.TenantGroupId))
                throw new UnauthorizedAccessException("Retry policy ownership does not match the persisted execution context.");
            if(parent.IsTerminal) throw new InvalidOperationException("A terminal parent cannot authorize a custom Retry evaluation.");
            var scope=new AiDurableInvocationScope(snapshot.TenantId!,snapshot.TenantGroupId!,await _controlPlane.ResolveAsync(cancellationToken).ConfigureAwait(false));
            var previous=_accessor.Current; _accessor.Set(ExecutionContextSnapshotMapper.ToExecutionContext(snapshot));
            try
            {
                var guard=await _identity.AuthorizeAsync(scope,_options.Execute,cancellationToken).ConfigureAwait(false);
                var result=await _publications.ReadRetryPolicyWorkerCodeAsync(request,guard,cancellationToken).ConfigureAwait(false); guard.RequireCurrent();
                var current=await (_dag is null?_executions.GetRecordAsync(request.Context.ExecutionId,cancellationToken):_dag.GetRecordAsync(request.Context.ExecutionId,cancellationToken)).ConfigureAwait(false);
                if(current is null || current.IsTerminal || current.ExecutionContextSnapshot?.TenantId!=scope.TenantId || current.ExecutionContextSnapshot.TenantGroupId!=scope.TenantGroupId || current.ExecutionContextSnapshot.UserId!=snapshot.UserId)
                    throw new UnauthorizedAccessException("Parent ownership or lifecycle changed before custom Retry evaluation.");
                guard.RequireCurrent(); return result;
            }
            finally { if(previous is null)_accessor.Clear(); else _accessor.Set(previous); }
        }
        private static void Validate(AiRetryPolicyRequest r)
        {
            AiDurableInvocationValidation.ValidateLanguage(r.ExecutionLanguage); AiDurableInvocationValidation.Text(r.RequestId,nameof(r.RequestId)); AiDurableInvocationValidation.Text(r.PolicyName,nameof(r.PolicyName)); AiDurableInvocationValidation.Text(r.ImplementationRef,nameof(r.ImplementationRef));
            AiDurableInvocationValidation.Text(r.Context.TenantId,nameof(r.Context.TenantId)); AiDurableInvocationValidation.Text(r.Context.ExecutionId,nameof(r.Context.ExecutionId)); AiDurableInvocationValidation.Text(r.Context.StepName,nameof(r.Context.StepName));
            if(r.Scope is not ("Pipeline" or "Step") || r.Scope=="Pipeline"&&r.OwnerStepName is not null || r.Scope=="Step"&&r.OwnerStepName!=r.Context.StepName) throw new InvalidOperationException("Custom Retry scope does not match its execution context.");
            if(r.DeadlineUtc.Offset!=TimeSpan.Zero) throw new InvalidOperationException("Custom Retry deadline must be UTC.");
        }
    }
}
