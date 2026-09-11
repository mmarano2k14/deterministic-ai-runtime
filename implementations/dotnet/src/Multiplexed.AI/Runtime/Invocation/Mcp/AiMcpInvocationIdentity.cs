using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.Rbac.Core.Authorization.Engine;
using Multiplexed.Rbac.Core.ExecutionContext;

namespace Multiplexed.AI.Runtime.Invocation.Mcp
{
    /// <summary>
    /// Checks the existing restored RBAC identity and delegates the decision to the
    /// registered IAuthorizationEngine. It neither creates permissions nor restores an
    /// ambient context from tenant input. Each check gets a fresh DI authorization scope.
    /// </summary>
    internal sealed record AiMcpInvocationIdentity(
        string TenantId, string TenantGroupId, string Project, string UserId, string Namespace)
    {
        public static AiMcpInvocationIdentity Capture(AiStepExecutionContext context)
        {
            var snapshot = context.Record.ExecutionContextSnapshot
                ?? throw new InvalidOperationException("MCP invocation requires the durable execution identity.");
            if (string.IsNullOrWhiteSpace(snapshot.TenantId) || string.IsNullOrWhiteSpace(snapshot.TenantGroupId) ||
                string.IsNullOrWhiteSpace(snapshot.Project) || string.IsNullOrWhiteSpace(snapshot.UserId) ||
                string.IsNullOrWhiteSpace(snapshot.CurrentNamespace))
            {
                throw new InvalidOperationException("MCP invocation requires a complete trusted execution identity.");
            }
            var identity = new AiMcpInvocationIdentity(snapshot.TenantId, snapshot.TenantGroupId,
                snapshot.Project, snapshot.UserId, snapshot.CurrentNamespace);
            identity.EnsureCurrent(context.Services);
            return identity;
        }

        public void EnsureCurrent(IServiceProvider services)
        {
            var current = services.GetRequiredService<IExecutionContextAccessor>().Current;
            if (current is null || current.TenantId != TenantId || current.TenantGroupId != TenantGroupId ||
                current.Project != Project || current.UserId != UserId || current.CurrentNamespace != Namespace)
            {
                throw new InvalidOperationException("The restored RBAC context does not match the MCP execution identity.");
            }
        }

        public void Authorize(IServiceProvider services, AiMcpToolBinding target)
        {
            // AuthorizationScope contains mutable decision/index caches. A fresh scope
            // prevents parallel runs or namespaces from borrowing a previous decision.
            // Use the host's existing registration; no fallback evaluator is installed.
            using var scope = services.CreateScope();
            EnsureCurrent(scope.ServiceProvider);
            var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationEngine>();
            if (!authorization.IsAllowed(target.Resource, target.Feature, target.Action))
            {
                throw new UnauthorizedAccessException("The current RBAC context does not authorize this MCP tool.");
            }
        }

        public void ValidateTarget(AiMcpToolBinding target, AiInvocationBinding binding)
        {
            if (target.TenantId != TenantId || target.TenantGroupId != TenantGroupId ||
                target.ConnectionRef != binding.ConnectionRef || target.Tool != binding.Tool ||
                !IsReference(target.ConnectionRevision) || !IsSegment(target.Resource) ||
                !IsSegment(target.Feature) || !IsSegment(target.Action))
            {
                throw new InvalidOperationException("The resolved MCP target does not match the requested tenant, connection, tool or concrete capability.");
            }
        }

        // These are local reference/capability restrictions, not a new TRN parser.
        // No wildcard can be requested; wildcard grants remain the RBAC engine's job.
        public static bool IsSegment(string? value) =>
            value is { Length: > 0 and <= 128 } && value.All(IsSegmentCharacter);

        public static bool IsReference(string? value) =>
            value is { Length: > 0 and <= 256 } && char.IsAsciiLetterOrDigit(value[0]) &&
            value.All(c => IsSegmentCharacter(c) || c == '/') && !value.Contains("..", StringComparison.Ordinal);

        private static bool IsSegmentCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.';
    }
}
