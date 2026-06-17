using Multiplexed.Abstractions.Core.ExecutionContext;
using System;
using System.Collections.Generic;
using System.Text;

namespace Multiplexed.AI.Tests.Fixtures
{
    public static class AiExecutionContextSnapshotTestFactory
    {
        public const string DefaultProject =
            "distributed-deterministic-ai-runtime";

        public const string DefaultNamespace =
            "mcp-ai-runtime";

        public const string DefaultUserId =
            "unit-test";

        public const string DefaultTenantId =
            "tenant-id-xxxx";

        public const string DefaultTenantGroupId =
            "tenant-group-id-xxx";

        /// <summary>
        /// Creates a default execution context snapshot for tests.
        /// </summary>
        /// <param name="contextKey">Optional context key.</param>
        /// <param name="project">Optional project name.</param>
        /// <param name="userId">Optional user id.</param>
        /// <param name="tenantId">Optional tenant id.</param>
        /// <param name="tenantGroupId">Optional tenant group id.</param>
        /// <param name="currentNamespace">Optional current namespace.</param>
        /// <returns>The created execution context snapshot.</returns>
        public static ExecutionContextSnapshot Create(
            string? contextKey = null,
            string? project = null,
            string? userId = null,
            string? tenantId = null,
            string? tenantGroupId = null,
            string? currentNamespace = null)
        {
            var effectiveProject =
                string.IsNullOrWhiteSpace(project)
                    ? DefaultProject
                    : project;

            var effectiveNamespace =
                string.IsNullOrWhiteSpace(currentNamespace)
                    ? DefaultNamespace
                    : currentNamespace;

            return new ExecutionContextSnapshot
            {
                ContextKey = string.IsNullOrWhiteSpace(contextKey)
                    ? Guid.NewGuid().ToString("N")
                    : contextKey,

                Project = effectiveProject,
                UserId = string.IsNullOrWhiteSpace(userId)
                    ? DefaultUserId
                    : userId,

                TenantId = string.IsNullOrWhiteSpace(tenantId)
                    ? DefaultTenantId
                    : tenantId,

                TenantGroupId = string.IsNullOrWhiteSpace(tenantGroupId)
                    ? DefaultTenantGroupId
                    : tenantGroupId,

                CurrentNamespace = effectiveNamespace,

                Namespaces = new List<NamespaceEntry>
                {
                    new()
                    {
                        Name = effectiveNamespace,
                        Trns = new HashSet<string>
                        {
                            $"trn:{effectiveProject}:shared-run:execution:submit",
                            $"trn:{effectiveProject}:shared-run:registry:read",
                            $"trn:{effectiveProject}:shared-run:registry:list",
                            $"trn:{effectiveProject}:shared-queue:queue:list",
                            $"trn:{effectiveProject}:shared-queue:status:read",
                            $"trn:{effectiveProject}:shared-queue:pump:drain"
                        }
                    }
                },

                InFlightCount = 0,
                TtlSeconds = 300,
                CreatedAtUtc = DateTime.UtcNow
            };
        }
     }
}
