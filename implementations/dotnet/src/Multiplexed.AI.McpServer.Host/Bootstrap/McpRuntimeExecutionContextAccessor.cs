using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.Rbac.Core.ExecutionContext;
using RbacExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Provides the current RBAC execution context for MCP runtime/background execution.
    /// </summary>
    /// <remarks>
    /// The current context is stored in an async-local scope so concurrent MCP requests
    /// do not overwrite each other.
    ///
    /// This accessor does not create a default context. A context must be provided by
    /// the RBAC middleware, test setup, or an explicit system/background execution flow.
    /// </remarks>
    public sealed class McpRuntimeExecutionContextAccessor :
        IExecutionContextAccessor,
        IExecutionContextSnapshotProvider
    {
        private static readonly AsyncLocal<RbacExecutionContext?> CurrentContext =
            new();

        /// <inheritdoc />
        public RbacExecutionContext? Current => CurrentContext.Value;

        /// <inheritdoc />
        public void Set(
            RbacExecutionContext context)
        {
            CurrentContext.Value = context
                ?? throw new ArgumentNullException(nameof(context));
        }

        /// <inheritdoc />
        public void Clear()
        {
            CurrentContext.Value = null;
        }

        /// <inheritdoc />
        public ExecutionContextSnapshot MapToSnapshot()
        {
            var current = Current;

            if (current is null)
            {
                throw new InvalidOperationException(
                    "Cannot map execution context to snapshot because no current execution context is available. " +
                    "The operation must run inside an RBAC execution context provided by the MCP middleware, " +
                    "a test RBAC context, or an explicit system/background context.");
            }

            return new ExecutionContextSnapshot
            {
                ContextKey = current.ContextKey,
                Project = current.Project,
                UserId = current.UserId,
                TenantId = current.TenantId,
                TenantGroupId = current.TenantGroupId,
                CurrentNamespace = current.CurrentNamespace,
                Namespaces = current.Namespaces
                    .Select(namespaceEntry => new NamespaceEntry
                    {
                        Name = namespaceEntry.Name,
                        Trns = new HashSet<string>(
                            namespaceEntry.Trns,
                            StringComparer.Ordinal)
                    })
                    .ToList(),
                InFlightCount = current.InFlightCount,
                TtlSeconds = current.TtlSeconds,
                CreatedAtUtc = DateTime.UtcNow
            };
        }
    }
}