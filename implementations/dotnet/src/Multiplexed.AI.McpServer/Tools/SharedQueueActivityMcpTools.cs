using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Activity;

namespace Multiplexed.AI.McpServer.Tools
{
    /// <summary>
    /// Exposes recent shared queue activity through MCP.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Provides visibility into recently submitted shared runs.
    /// - Complements shared_queue.list by exposing activity that may already
    ///   have been dispatched, completed, failed, or cancelled.
    /// - Intended for dashboards, MCP diagnostics, Kubernetes visibility,
    ///   and operational troubleshooting.
    /// </remarks>
    [McpServerToolType]
    public sealed class SharedQueueActivityMcpTools
    {
        private readonly IAiSharedRunStore sharedRunStore;
        private readonly ILogger<SharedQueueActivityMcpTools> logger;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="SharedQueueActivityMcpTools"/> class.
        /// </summary>
        /// <param name="sharedRunStore">The shared run store.</param>
        /// <param name="logger">The logger.</param>
        public SharedQueueActivityMcpTools(
            IAiSharedRunStore sharedRunStore,
            ILogger<SharedQueueActivityMcpTools> logger)
        {
            this.sharedRunStore =
                sharedRunStore
                ?? throw new ArgumentNullException(nameof(sharedRunStore));

            this.logger =
                logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Lists recent shared queue activity.
        /// </summary>
        /// <param name="request">The activity request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The recent shared queue activity.</returns>
        [McpServerTool(Name = "shared_queue.activity")]
        [Description("Lists recent shared queue activity.")]
        public async Task<AiSharedQueueActivityResult> GetActivityAsync(
            AiSharedQueueActivityRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (request.MaxResults <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    request.MaxResults,
                    "MaxResults must be greater than zero.");
            }

            logger.LogInformation(
                "MCP shared_queue.activity called. PipelineKey={PipelineKey}, TenantId={TenantId}, MaxResults={MaxResults}",
                request.PipelineKey,
                request.TenantId,
                request.MaxResults);

            var runs =
                await sharedRunStore
                    .ListAsync(
                        includeCancelled: request.IncludeCancelled,
                        includeCompleted: request.IncludeCompleted,
                        includeFailed: request.IncludeFailed,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

            var filteredRuns =
                runs.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(request.PipelineKey))
            {
                filteredRuns =
                    filteredRuns.Where(run =>
                        string.Equals(
                            run.PipelineKey,
                            request.PipelineKey,
                            StringComparison.Ordinal));
            }

            if (!string.IsNullOrWhiteSpace(request.TenantId))
            {
                filteredRuns =
                    filteredRuns.Where(run =>
                        string.Equals(
                            run.TenantId,
                            request.TenantId,
                            StringComparison.Ordinal));
            }

            var resultRuns =
                filteredRuns
                    .OrderByDescending(run => run.UpdatedAtUtc)
                    .Take(request.MaxResults)
                    .ToArray();

            logger.LogInformation(
                "MCP shared_queue.activity completed. Count={Count}",
                resultRuns.Length);

            return new AiSharedQueueActivityResult
            {
                Runs = resultRuns,
                Count = resultRuns.Length,
                SnapshotAtUtc = DateTimeOffset.UtcNow
            };
        }
    }
}