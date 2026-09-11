using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Rbac.Core.ExecutionContext;

namespace Multiplexed.AI.Runtime.Invocation.Durable.Dag
{
    /// <summary>Checks restored identity through the existing RBAC accessor; never creates grants.</summary>
    internal static class AiDurableInvocationDagIdentity
    {
        internal static void RequireCurrent(AiStepExecutionContext context)
        {
            var stored = context.Record.ExecutionContextSnapshot
                ?? throw new InvalidOperationException("A durable invocation requires its persisted execution context.");
            var live = context.Services.GetRequiredService<IExecutionContextAccessor>().Current;
            if (live is null || string.IsNullOrWhiteSpace(stored.TenantId) || string.IsNullOrWhiteSpace(stored.TenantGroupId) ||
                string.IsNullOrWhiteSpace(stored.Project) || string.IsNullOrWhiteSpace(stored.UserId) ||
                string.IsNullOrWhiteSpace(stored.CurrentNamespace) ||
                live.TenantId != stored.TenantId || live.TenantGroupId != stored.TenantGroupId ||
                live.Project != stored.Project || live.UserId != stored.UserId || live.CurrentNamespace != stored.CurrentNamespace)
                throw new UnauthorizedAccessException("The restored RBAC identity does not own this durable invocation.");
        }

        internal static async Task<AiDurableInvocationScope> CaptureAsync(
            AiStepExecutionContext context, CancellationToken cancellationToken)
        {
            RequireCurrent(context);
            var controlPlane = await context.Services.GetRequiredService<IAiControlPlaneIdResolver>()
                .ResolveAsync(cancellationToken).ConfigureAwait(false);
            RequireCurrent(context);
            var snapshot = context.Record.ExecutionContextSnapshot!;
            var scope = new AiDurableInvocationScope(snapshot.TenantId, snapshot.TenantGroupId, controlPlane);
            AiDurableInvocationValidation.ValidateScope(scope);
            return scope;
        }
    }
}
