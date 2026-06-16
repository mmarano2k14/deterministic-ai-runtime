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
            const string project = "distributed-deterministic-ai-runtime";

            return new ExecutionContext
            {
                ContextKey = "mcp-runtime-system",
                Project = project,
                TenantId = "tenant-id-xxxx",
                TenantGroupId = "tenant-group-id-xxx",
                CurrentNamespace = "mcp-ai-runtime",
                UserId = "mcp-runtime",
                Namespaces = new List<NamespaceEntry>
                {
                    new()
                    {
                        Name = "mcp-ai-runtime",
                        Trns = new HashSet<string>
                        {
                            $"trn:{project}:replay:execution:run",
                            $"trn:{project}:replay:audit:run",
                            $"trn:{project}:replay:report:read",
                            $"trn:{project}:observability:ledger:read",
                            $"trn:{project}:observability:trace:read",

                            $"trn:{project}:execution-control:pause-execution:execute",
                            $"trn:{project}:execution-control:resume-execution:execute",
                            $"trn:{project}:execution-control:cancel-execution:execute"
                        }
                    }
                },
                TtlSeconds = 300
            };
        }
    }
}