using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.Rbac.Core.ExecutionContext;
using ExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Provides a system RBAC execution context for MCP runtime/background execution.
    /// </summary>
    public sealed class McpRuntimeExecutionContextAccessor : IExecutionContextAccessor
    {
        private ExecutionContext? context;

        public McpRuntimeExecutionContextAccessor()
        {
            context = CreateDefaultContext();
        }

        public ExecutionContext? Current => context;

        public void Set(
            ExecutionContext context)
        {
            this.context = context
                ?? throw new ArgumentNullException(nameof(context));
        }

        public void Clear()
        {
            context = null;
        }

        private static ExecutionContext CreateDefaultContext()
        {
            return new ExecutionContext
            {
                ContextKey = string.Empty,
                Project = "Project",
                TenantId = "tenant-id-xxxx",
                TenantGroupId = "tenant-group-id-xxx",
                CurrentNamespace = "Namespace",
                UserId = "mcp-runtime",
                Namespaces = new List<NamespaceEntry>
                {
                    new()
                    {
                        Name = "Namespace",
                        Trns = new HashSet<string>
                        {
                            "trn:Project:crm:billing:invoice:read",
                            "trn:Project:crm:billing:invoice:refund"
                        }
                    }
                },
                TtlSeconds = 300
            };
        }
    }
}