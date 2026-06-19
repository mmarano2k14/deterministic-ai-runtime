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

        public const string DefaultTenantId =
            "tenant-id-xxxx";

        public const string DefaultTenantGroupId =
            "tenant-group-id-xxx";

        public static ExecutionContext CreateDefaultContext(
            string? userId = null,
            string? tenantId = null,
            string? tenantGroupId = null)
        {
            var effectiveUserId =
                string.IsNullOrWhiteSpace(userId)
                    ? DefaultUserId
                    : userId;

            var effectiveTenantId =
                string.IsNullOrWhiteSpace(tenantId)
                    ? DefaultTenantId
                    : tenantId;

            var effectiveTenantGroupId =
                string.IsNullOrWhiteSpace(tenantGroupId)
                    ? DefaultTenantGroupId
                    : tenantGroupId;

            return new ExecutionContext
            {
                ContextKey = Guid.NewGuid().ToString("D"),
                Project = Project,
                TenantId = effectiveTenantId,
                TenantGroupId = effectiveTenantGroupId,
                CurrentNamespace = Namespace,
                UserId = effectiveUserId,
                Namespaces = new List<NamespaceEntry>
                {
                    new()
                    {
                        Name = Namespace,
                        Trns = new HashSet<string>
                        {
                            $"trn:{Project}:runtime-instance:registry:list",
                            $"trn:{Project}:runtime-instance:registry:read",

                            $"trn:{Project}:runtime-queue:status:read",
                            $"trn:{Project}:runtime-queue:run:read",
                            $"trn:{Project}:runtime-queue:queue:pause",
                            $"trn:{Project}:runtime-queue:queue:resume",
                            $"trn:{Project}:runtime-queue:run:cancel",

                            $"trn:{Project}:shared-queue:activity:read",
                            $"trn:{Project}:shared-queue:pump:drain",
                            $"trn:{Project}:shared-queue:queue:list",
                            $"trn:{Project}:shared-queue:status:read",

                            $"trn:{Project}:shared-run:execution:submit",
                            $"trn:{Project}:shared-run:registry:list",
                            $"trn:{Project}:shared-run:registry:read",
                            $"trn:{Project}:shared-run:execution:cancel",

                            $"trn:{Project}:replay:execution:run",
                            $"trn:{Project}:replay:audit:run",
                            $"trn:{Project}:replay:report:read",

                            $"trn:{Project}:execution:control:pause",
                            $"trn:{Project}:execution:control:resume",
                            $"trn:{Project}:execution:control:cancel",
                            $"trn:{Project}:execution:control:read",

                            $"trn:{Project}:observability:ledger:read",
                            $"trn:{Project}:observability:ledger:query",
                            $"trn:{Project}:observability:trace:read",
                            $"trn:{Project}:observability:metrics:read"
                        }
                    }
                },
                TtlSeconds = 300
            };
        }
    }
}