using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Invocation.Durable;
using Multiplexed.Rbac.Core.Authorization.Engine;
using Multiplexed.Rbac.Core.ExecutionContext;

namespace Multiplexed.AI.Runtime.Publication
{
    /// <summary>Delegates authorization to the existing RBAC engine, in a fresh authorization scope per operation.</summary>
    public sealed class AiPublicationIdentity
    {
        private readonly IServiceProvider _services;
        private readonly IExecutionContextAccessor _accessor;
        private readonly IAiControlPlaneIdResolver _controlPlane;
        public AiPublicationIdentity(IServiceProvider services, IExecutionContextAccessor accessor, IAiControlPlaneIdResolver controlPlane)
        { _services = services; _accessor = accessor; _controlPlane = controlPlane; }

        internal async Task<Guard> CaptureGuardAsync(AiDurableInvocationScope scope, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AiDurableInvocationValidation.ValidateScope(scope);
            var live = _accessor.Current ?? throw new UnauthorizedAccessException("No trusted publication context is active.");
            AiPublicationJson.Text(live.Project, "Project"); AiPublicationJson.Text(live.UserId, "UserId");
            AiPublicationJson.Text(live.CurrentNamespace, "Namespace");
            var guard = new Guard(_accessor, new AiPublicationPartition(scope, live.Project, live.CurrentNamespace), live.UserId);
            guard.RequireCurrent();
            if (await _controlPlane.ResolveAsync(cancellationToken).ConfigureAwait(false) != scope.ControlPlaneId)
                throw new UnauthorizedAccessException("Publication scope does not match this control plane.");
            guard.RequireCurrent(); cancellationToken.ThrowIfCancellationRequested();
            return guard;
        }

        internal async Task<Guard> AuthorizeAsync(AiDurableInvocationScope scope,
            AiPublicationCapability capability, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AiDurableInvocationValidation.ValidateScope(scope);
            var live = _accessor.Current ?? throw new UnauthorizedAccessException("No trusted publication context is active.");
            AiPublicationJson.Text(live.Project, "Project"); AiPublicationJson.Text(live.UserId, "UserId");
            AiPublicationJson.Text(live.CurrentNamespace, "Namespace");
            var guard = new Guard(_accessor, new AiPublicationPartition(scope, live.Project, live.CurrentNamespace), live.UserId);
            guard.RequireCurrent();
            using (var authorizationScope = _services.CreateScope())
            {
                var scopedAccessor = authorizationScope.ServiceProvider.GetRequiredService<IExecutionContextAccessor>();
                if (scopedAccessor.Current?.TenantId != scope.TenantId || scopedAccessor.Current?.TenantGroupId != scope.TenantGroupId ||
                    scopedAccessor.Current?.Project != live.Project || scopedAccessor.Current?.CurrentNamespace != live.CurrentNamespace ||
                    scopedAccessor.Current?.UserId != live.UserId)
                    throw new UnauthorizedAccessException("Authorization scope does not preserve the trusted context.");
                if (!authorizationScope.ServiceProvider.GetRequiredService<IAuthorizationEngine>()
                    .IsAllowed(capability.Resource, capability.Feature, capability.Action))
                    throw new UnauthorizedAccessException("The active RBAC context does not authorize this publication operation.");
            }
            if (await _controlPlane.ResolveAsync(cancellationToken).ConfigureAwait(false) != scope.ControlPlaneId)
                throw new UnauthorizedAccessException("Publication scope does not match this control plane.");
            guard.RequireCurrent(); cancellationToken.ThrowIfCancellationRequested();
            return guard;
        }

        internal sealed record Guard(IExecutionContextAccessor Accessor, AiPublicationPartition Partition, string UserId)
        {
            internal void RequireCurrent()
            {
                var current = Accessor.Current;
                if (current is null || current.TenantId != Partition.Scope.TenantId || current.TenantGroupId != Partition.Scope.TenantGroupId ||
                    current.Project != Partition.Project || current.CurrentNamespace != Partition.Namespace || current.UserId != UserId)
                    throw new UnauthorizedAccessException("Publication ownership context changed during the operation.");
            }
        }
    }
}
