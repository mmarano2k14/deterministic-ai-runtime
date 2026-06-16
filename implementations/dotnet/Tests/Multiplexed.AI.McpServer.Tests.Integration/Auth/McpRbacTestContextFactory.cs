using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.Rbac.Core.ExecutionContext;
using ExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.AI.McpServer.Tests.Integration.Auth
{
    public static class McpRbacTestContextFactory
    {
        public const string Project =
            "distributed-deterministic-ai-runtime";

        public const string Namespace =
            "mcp-ai-runtime";

        public const string DemoUserIdHeaderName =
            "X-Demo-UserId";

        public const string DefaultUserId =
            "mcp-integration-test";

        public static ExecutionContext CreateDefaultContext(
            string? userId = null)
        {
            var effectiveUserId =
                string.IsNullOrWhiteSpace(userId)
                    ? DefaultUserId
                    : userId;

            return new ExecutionContext
            {
                ContextKey = Guid.NewGuid().ToString("D"),
                Project = Project,
                TenantId = "tenant-id-xxxx",
                TenantGroupId = "tenant-group-id-xxx",
                CurrentNamespace = Namespace,
                UserId = effectiveUserId,
                Namespaces = new List<NamespaceEntry>
                {
                    new()
                    {
                        Name = Namespace,
                        Trns = new HashSet<string>
                        {
                            $"trn:{Project}:replay:execution:run",
                            $"trn:{Project}:replay:audit:run",
                            $"trn:{Project}:replay:report:read",

                            $"trn:{Project}:observability:ledger:read",
                            $"trn:{Project}:observability:trace:read"
                        }
                    }
                },
                TtlSeconds = 300
            };
        }
    }
}