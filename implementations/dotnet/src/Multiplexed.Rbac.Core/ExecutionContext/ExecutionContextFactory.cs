using Multiplexed.Abstractions.Core.ExecutionContext;
using System;

namespace Multiplexed.Rbac.Core.ExecutionContext
{
    /// <summary>
    /// Default implementation used to create durable execution context snapshots
    /// from live runtime contexts.
    /// </summary>
    public sealed class ExecutionContextFactory : IExecutionContextFactory
    {
        /// <summary>
        /// Creates a shollow from the provided runtime execution context.
        /// </summary>
        /// <param name="context">Current runtime execution context.</param>
        /// <returns>Durable execution context .</returns>
        public ExecutionContext CreateCopy(ExecutionContext context, string contextKey = "")
        {
            ArgumentNullException.ThrowIfNull(context);

            return new ExecutionContext
            {
                ContextKey = contextKey,
                TenantId = context.TenantId,
                TenantGroupId = context.TenantGroupId,
                UserId = context.UserId,
                Project = context.Project,
                CurrentNamespace = context.CurrentNamespace,
                Namespaces = context.Namespaces?.ToList() ?? new List<NamespaceEntry>(),
                InFlightCount = context.InFlightCount,
                TtlSeconds = context.TtlSeconds,
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Creates a snapshot from the provided runtime execution context.
        /// </summary>
        public ExecutionContextSnapshot CreateSnapshot(ExecutionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return new ExecutionContextSnapshot
            {
                ContextKey = context.ContextKey,
                TenantId = context.TenantId,
                TenantGroupId = context.TenantGroupId,
                UserId = context.UserId,
                Project = context.Project,
                CurrentNamespace = context.CurrentNamespace,
                Namespaces = context.Namespaces?.ToList() ?? new List<NamespaceEntry>(),
                InFlightCount = context.InFlightCount,
                TtlSeconds = context.TtlSeconds,
                CreatedAtUtc = DateTime.UtcNow
            };
        }
    }
}