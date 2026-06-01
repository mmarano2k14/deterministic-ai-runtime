using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Multiplexed.Abstractions.AI.ControlPlane.Replay;

namespace Multiplexed.AI.McpServer.Tools
{
    /// <summary>
    /// Exposes MCP tools related to replay, audit, ledger, and timeline control-plane operations.
    /// </summary>
    /// <remarks>
    /// This tool class consumes the replay control-plane only.
    /// It does not re-run DAG steps, LLM calls, provider calls, or runtime workers.
    /// </remarks>
    [McpServerToolType]
    public sealed class ReplayMcpTools
    {
        private readonly IAiReplayControlPlane replayControlPlane;
        private readonly ILogger<ReplayMcpTools> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ReplayMcpTools"/> class.
        /// </summary>
        /// <param name="replayControlPlane">The replay control-plane.</param>
        /// <param name="logger">The logger.</param>
        public ReplayMcpTools(
            IAiReplayControlPlane replayControlPlane,
            ILogger<ReplayMcpTools> logger)
        {
            this.replayControlPlane = replayControlPlane
                ?? throw new ArgumentNullException(nameof(replayControlPlane));

            this.logger = logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Runs deterministic replay validation for an execution.
        /// </summary>
        /// <param name="request">The replay control-plane request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The replay control-plane result.</returns>
        [McpServerTool(Name = "replay.execution")]
        [Description("Runs deterministic replay validation for an existing execution.")]
        public async Task<AiReplayControlResult> ReplayExecutionAsync(
            AiReplayControlRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            logger.LogInformation(
                "MCP replay.execution called. ExecutionId={ExecutionId}, CorrelationId={CorrelationId}, RequestedBy={RequestedBy}, Source={Source}",
                request.ExecutionId,
                request.CorrelationId,
                request.RequestedBy,
                request.Source);

            return await replayControlPlane
                .ReplayAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Runs audit-only replay for an execution.
        /// </summary>
        /// <param name="request">The replay control-plane request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The replay control-plane result.</returns>
        [McpServerTool(Name = "replay.audit")]
        [Description("Runs audit-only replay for an existing execution.")]
        public async Task<AiReplayControlResult> AuditExecutionAsync(
            AiReplayControlRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            logger.LogInformation(
                "MCP replay.audit called. ExecutionId={ExecutionId}, CorrelationId={CorrelationId}, RequestedBy={RequestedBy}, Source={Source}",
                request.ExecutionId,
                request.CorrelationId,
                request.RequestedBy,
                request.Source);

            return await replayControlPlane
                .AuditAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Retrieves or builds the replay report for an execution.
        /// </summary>
        /// <param name="request">The replay control-plane request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The replay control-plane result.</returns>
        [McpServerTool(Name = "replay.report")]
        [Description("Retrieves or builds the replay report for an existing execution.")]
        public async Task<AiReplayControlResult> GetReplayReportAsync(
            AiReplayControlRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            logger.LogInformation(
                "MCP replay.report called. ExecutionId={ExecutionId}, CorrelationId={CorrelationId}, RequestedBy={RequestedBy}, Source={Source}",
                request.ExecutionId,
                request.CorrelationId,
                request.RequestedBy,
                request.Source);

            return await replayControlPlane
                .GetReportAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Retrieves decision ledger entries for an execution.
        /// </summary>
        /// <param name="request">The replay control-plane request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The replay control-plane result.</returns>
        [McpServerTool(Name = "observability.ledger")]
        [Description("Retrieves decision ledger entries associated with an execution.")]
        public async Task<AiReplayControlResult> GetExecutionLedgerAsync(
            AiReplayControlRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            logger.LogInformation(
                "MCP observability.ledger called. ExecutionId={ExecutionId}, CorrelationId={CorrelationId}, RequestedBy={RequestedBy}, Source={Source}",
                request.ExecutionId,
                request.CorrelationId,
                request.RequestedBy,
                request.Source);

            return await replayControlPlane
                .GetLedgerAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Retrieves trace timeline entries for an execution.
        /// </summary>
        /// <param name="request">The replay control-plane request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The replay control-plane result.</returns>
        [McpServerTool(Name = "observability.trace")]
        [Description("Retrieves trace timeline entries associated with an execution.")]
        public async Task<AiReplayControlResult> GetExecutionTimelineAsync(
            AiReplayControlRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            logger.LogInformation(
                "MCP observability.trace called. ExecutionId={ExecutionId}, CorrelationId={CorrelationId}, RequestedBy={RequestedBy}, Source={Source}",
                request.ExecutionId,
                request.CorrelationId,
                request.RequestedBy,
                request.Source);

            return await replayControlPlane
                .GetTimelineAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}