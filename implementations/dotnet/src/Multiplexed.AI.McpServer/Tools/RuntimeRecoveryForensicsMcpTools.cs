using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Rbac.Core.Authorization.Attributes;

namespace Multiplexed.AI.McpServer.Tools
{
    /// <summary>
    /// Exposes MCP tools for runtime recovery forensics read-model queries.
    /// </summary>
    /// <remarks>
    /// This tool class is intentionally read-only. It exposes recovery evidence,
    /// MongoDB-backed read models, and ordered timelines for operators, tests,
    /// and dashboards. It must never trigger, retry, requeue, resume, or otherwise
    /// drive runtime recovery.
    /// </remarks>
    [McpServerToolType]
    public sealed class RuntimeRecoveryForensicsMcpTools
    {
        private readonly IAiRuntimeRecoveryForensicsQueryService queryService;
        private readonly ILogger<RuntimeRecoveryForensicsMcpTools> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RuntimeRecoveryForensicsMcpTools"/> class.
        /// </summary>
        /// <param name="queryService">The runtime recovery forensics query service.</param>
        /// <param name="logger">The logger.</param>
        public RuntimeRecoveryForensicsMcpTools(
            IAiRuntimeRecoveryForensicsQueryService queryService,
            ILogger<RuntimeRecoveryForensicsMcpTools> logger)
        {
            this.queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Gets one runtime recovery forensics read model by forensics id.
        /// </summary>
        /// <param name="forensicsId">The runtime recovery forensics id.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The recovery forensics read model, or <c>null</c> when not found.</returns>
        [McpServerTool(Name = "runtime.recovery.forensics.get")]
        [Description("Gets one runtime recovery forensics read model by forensics id. Read-only; does not trigger recovery.")]
        [RequireCapability("runtime-recovery", "forensics", "read")]
        public async Task<AiRuntimeRecoveryForensicsReadModel?> GetRuntimeRecoveryForensicsAsync(
            string forensicsId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(forensicsId);

            logger.LogInformation(
                "MCP runtime.recovery.forensics.get called. ForensicsId={ForensicsId}",
                forensicsId);

            return await queryService
                .GetByForensicsIdAsync(
                    forensicsId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        [McpServerTool(Name = "runtime.recovery.forensics.search")]
        [Description("Searches runtime recovery forensics read models by execution, shared run, runtime instance, tenant, event type, or recent failures. Read-only; does not trigger recovery.")]
        [RequireCapability("runtime-recovery", "forensics", "query")]
        public async Task<AiRuntimeRecoveryForensicsQueryResult> SearchRuntimeRecoveryForensicsAsync(
            string? forensicsId = null,
            string? executionId = null,
            string? sharedRunId = null,
            string? runtimeInstanceId = null,
            string? tenantId = null,
            string? tenantGroupId = null,
            string? controlPlaneId = null,
            string? eventType = null,
            bool recentFailuresOnly = false,
            int limit = 50,
            CancellationToken cancellationToken = default)
        {
            var query = new AiRuntimeRecoveryForensicsQuery
            {
                ForensicsId = forensicsId,
                ExecutionId = executionId,
                SharedRunId = sharedRunId,
                RuntimeInstanceId = runtimeInstanceId,
                TenantId = tenantId,
                TenantGroupId = tenantGroupId,
                ControlPlaneId = controlPlaneId,
                EventType = eventType,
                RecentFailuresOnly = recentFailuresOnly,
                Limit = limit
            };

            logger.LogInformation(
                "MCP runtime.recovery.forensics.search called. ForensicsId={ForensicsId}, ExecutionId={ExecutionId}, SharedRunId={SharedRunId}, RuntimeInstanceId={RuntimeInstanceId}, TenantId={TenantId}, TenantGroupId={TenantGroupId}, ControlPlaneId={ControlPlaneId}, EventType={EventType}, RecentFailuresOnly={RecentFailuresOnly}, Limit={Limit}",
                query.ForensicsId,
                query.ExecutionId,
                query.SharedRunId,
                query.RuntimeInstanceId,
                query.TenantId,
                query.TenantGroupId,
                query.ControlPlaneId,
                query.EventType,
                query.RecentFailuresOnly,
                query.Limit);

            return await queryService
                .SearchAsync(query, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Gets only the ordered runtime recovery forensics timeline for one forensics id.
        /// </summary>
        /// <param name="forensicsId">The runtime recovery forensics id.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The ordered recovery timeline.</returns>
        [McpServerTool(Name = "runtime.recovery.forensics.timeline")]
        [Description("Gets the ordered runtime recovery forensics timeline by forensics id. Read-only; does not trigger recovery.")]
        [RequireCapability("runtime-recovery", "forensics", "read")]
        public async Task<IReadOnlyList<AiRuntimeRecoveryForensicsTimelineItem>> GetRuntimeRecoveryForensicsTimelineAsync(
            string forensicsId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(forensicsId);

            logger.LogInformation(
                "MCP runtime.recovery.forensics.timeline called. ForensicsId={ForensicsId}",
                forensicsId);

            return await queryService
                .GetTimelineAsync(
                    forensicsId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
