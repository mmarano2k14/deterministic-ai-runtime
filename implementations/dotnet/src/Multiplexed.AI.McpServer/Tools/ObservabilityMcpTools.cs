using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Tracing;

namespace Multiplexed.AI.McpServer.Tools
{
    /// <summary>
    /// Exposes MCP tools related to runtime observability.
    /// </summary>
    /// <remarks>
    /// This tool class reads ledger and tracing observability data only.
    /// Runtime metrics query tools are intentionally not exposed yet because the
    /// current metric store is append-only and does not provide a query/snapshot API.
    /// </remarks>
    [McpServerToolType]
    public sealed class ObservabilityMcpTools
    {
        private readonly IAiDecisionLedger decisionLedger;
        private readonly IAiTraceTimeline traceTimeline;
        private readonly ILogger<ObservabilityMcpTools> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ObservabilityMcpTools"/> class.
        /// </summary>
        /// <param name="decisionLedger">The decision ledger.</param>
        /// <param name="traceTimeline">The trace timeline.</param>
        /// <param name="logger">The logger.</param>
        public ObservabilityMcpTools(
            IAiDecisionLedger decisionLedger,
            IAiTraceTimeline traceTimeline,
            ILogger<ObservabilityMcpTools> logger)
        {
            this.decisionLedger = decisionLedger
                ?? throw new ArgumentNullException(nameof(decisionLedger));

            this.traceTimeline = traceTimeline
                ?? throw new ArgumentNullException(nameof(traceTimeline));

            this.logger = logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Gets decision ledger entries for an execution.
        /// </summary>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The ordered decision ledger entries for the execution.</returns>
        [McpServerTool(Name = "observability.ledger.get_by_execution")]
        [Description("Gets decision ledger entries for a specific execution.")]
        public async Task<IReadOnlyList<AiDecisionLedgerEntry>> GetLedgerByExecutionAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            logger.LogInformation(
                "MCP observability.ledger.get_by_execution called. ExecutionId={ExecutionId}",
                executionId);

            return await decisionLedger
                .GetByExecutionAsync(executionId, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Queries decision ledger entries.
        /// </summary>
        /// <param name="query">The decision ledger query.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The ordered decision ledger entries matching the query.</returns>
        [McpServerTool(Name = "observability.ledger.query")]
        [Description("Queries decision ledger entries using ledger query filters.")]
        public async Task<IReadOnlyList<AiDecisionLedgerEntry>> QueryLedgerAsync(
            AiDecisionLedgerQuery query,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);

            logger.LogInformation(
                "MCP observability.ledger.query called.");

            return await decisionLedger
                .QueryAsync(query, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Gets trace timeline events for an execution.
        /// </summary>
        /// <param name="executionId">The execution identifier.</param>
        /// <returns>The ordered trace events for the execution.</returns>
        [McpServerTool(Name = "observability.trace.get_by_execution")]
        [Description("Gets trace timeline events for a specific execution.")]
        public IReadOnlyList<AiTraceEvent> GetTraceByExecution(
            string executionId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            logger.LogInformation(
                "MCP observability.trace.get_by_execution called. ExecutionId={ExecutionId}",
                executionId);

            return traceTimeline.Get(executionId);
        }

        /// <summary>
        /// Describes the current status of runtime metrics exposure.
        /// </summary>
        /// <returns>A message describing why runtime metrics query tools are not exposed yet.</returns>
        [McpServerTool(Name = "observability.metrics.status")]
        [Description("Describes the current runtime metrics MCP exposure status.")]
        public string GetMetricsStatus()
        {
            return "Runtime metrics are currently append-only through IAiRuntimeMetricStore. " +
                   "MCP metrics tools will be added after a query/snapshot API is introduced.";
        }
    }
}