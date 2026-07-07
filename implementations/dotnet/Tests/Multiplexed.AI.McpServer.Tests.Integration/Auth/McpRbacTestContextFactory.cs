using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.Rbac.Core.ExecutionContext;
using System;
using System.Collections.Generic;
using System.Linq;
using ExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;
using SnapshotNamespaceEntry = Multiplexed.Abstractions.Core.ExecutionContext.NamespaceEntry;

namespace Multiplexed.AI.McpServer.Tests.Integration.Auth
{
    /// <summary>
    /// Creates RBAC execution contexts used by MCP integration tests.
    /// </summary>
    public static class McpRbacTestContextFactory
    {
        /// <summary>
        /// Default integration-test project.
        /// </summary>
        public const string Project =
            "distributed-deterministic-ai-runtime";

        /// <summary>
        /// Default integration-test namespace.
        /// </summary>
        public const string Namespace =
            "mcp-ai-runtime";

        /// <summary>
        /// Demo user id header name.
        /// </summary>
        public const string DemoUserIdHeaderName =
            "X-Demo-UserId";

        /// <summary>
        /// Default integration-test user id.
        /// </summary>
        public const string DefaultUserId =
            "mcp-integration-test";

        /// <summary>
        /// Default integration-test tenant id.
        /// </summary>
        public const string DefaultTenantId =
            "tenant-id-xxxx";

        /// <summary>
        /// Default integration-test tenant group id.
        /// </summary>
        public const string DefaultTenantGroupId =
            "tenant-group-id-xxx";

        /// <summary>
        /// Creates a default RBAC execution context.
        /// </summary>
        /// <param name="userId">The optional user id.</param>
        /// <param name="tenantId">The optional tenant id.</param>
        /// <param name="tenantGroupId">The optional tenant group id.</param>
        /// <returns>The RBAC execution context.</returns>
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
                            $"trn:{Project}:observability:metrics:read",

                            $"trn:{Project}:runtime-recovery:forensics:read",
                            $"trn:{Project}:runtime-recovery:forensics:query"
                        }
                    }
                },
                TtlSeconds = 300
            };
        }

        /// <summary>
        /// Creates a default AI execution context snapshot for integration tests.
        /// </summary>
        /// <param name="userId">The optional user id.</param>
        /// <param name="tenantId">The optional tenant id.</param>
        /// <param name="tenantGroupId">The optional tenant group id.</param>
        /// <returns>The AI execution context snapshot.</returns>
        public static ExecutionContextSnapshot CreateDefaultSnapshot(
            string? userId = null,
            string? tenantId = null,
            string? tenantGroupId = null)
        {
            return MapToSnapshot(
                CreateDefaultContext(
                    userId,
                    tenantId,
                    tenantGroupId));
        }

        /// <summary>
        /// Maps an RBAC execution context to an AI execution context snapshot.
        /// </summary>
        /// <param name="context">The RBAC execution context.</param>
        /// <returns>The AI execution context snapshot.</returns>
        public static ExecutionContextSnapshot MapToSnapshot(
            ExecutionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return new ExecutionContextSnapshot
            {
                ContextKey = context.ContextKey,
                Project = context.Project,
                UserId = context.UserId,
                TenantId = context.TenantId,
                TenantGroupId = context.TenantGroupId,
                CurrentNamespace = context.CurrentNamespace,
                Namespaces = context.Namespaces
                    .Select(MapNamespace)
                    .ToList(),
                InFlightCount = 0,
                TtlSeconds = context.TtlSeconds,
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Maps an RBAC namespace entry to an AI execution context namespace entry.
        /// </summary>
        /// <param name="namespaceEntry">The RBAC namespace entry.</param>
        /// <returns>The AI execution context namespace entry.</returns>
        private static SnapshotNamespaceEntry MapNamespace(
            NamespaceEntry namespaceEntry)
        {
            ArgumentNullException.ThrowIfNull(namespaceEntry);

            return new SnapshotNamespaceEntry
            {
                Name = namespaceEntry.Name,
                Trns = new HashSet<string>(namespaceEntry.Trns)
            };
        }
    }
}