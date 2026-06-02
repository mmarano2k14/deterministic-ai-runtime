using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.AI.McpServer.Models.Responses;

namespace Multiplexed.AI.McpServer.Tools
{
    /// <summary>
    /// Exposes MCP tools related to the shared queue.
    /// </summary>
    [McpServerToolType]
    public sealed class SharedQueueMcpTools
    {
        private readonly IAiSharedQueuePump sharedQueuePump;
        private readonly IAiSharedQueue sharedQueue;
        private readonly ILogger<SharedQueueMcpTools> logger;

        public SharedQueueMcpTools(
            IAiSharedQueuePump sharedQueuePump,
            IAiSharedQueue sharedQueue,
            ILogger<SharedQueueMcpTools> logger)
        {
            this.sharedQueuePump = sharedQueuePump
                ?? throw new ArgumentNullException(nameof(sharedQueuePump));

            this.sharedQueue = sharedQueue
                ?? throw new ArgumentNullException(nameof(sharedQueue));

            this.logger = logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        [McpServerTool(Name = "queue.drain")]
        [Description("Executes one shared queue pump cycle manually.")]
        public async Task<AiSharedQueuePumpResult> DrainAsync(
            AiSharedQueuePumpRequest request,
            CancellationToken cancellationToken = default)
        {
            logger.LogInformation(
                "MCP queue.drain called. RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}",
                request.RuntimeInstanceId,
                request.WorkerId);

            return await sharedQueuePump
                .PumpOnceAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }

        [McpServerTool(Name = "shared_queue.list")]
        [Description("Lists items currently known by the shared queue.")]
        public async Task<IReadOnlyList<AiSharedQueueItem>> ListSharedQueueAsync(
            bool includeTerminal = true,
            CancellationToken cancellationToken = default)
        {
            logger.LogInformation(
                "MCP shared_queue.list called. IncludeTerminal={IncludeTerminal}",
                includeTerminal);

            return await sharedQueue
                .ListAsync(includeTerminal, cancellationToken)
                .ConfigureAwait(false);
        }

        [McpServerTool(Name = "shared_queue.status")]
        [Description("Gets aggregated shared queue status counts.")]
        public async Task<SharedQueueStatusResult> GetSharedQueueStatusAsync(
            bool includeTerminal = true,
            CancellationToken cancellationToken = default)
        {
            logger.LogInformation(
                "MCP shared_queue.status called. IncludeTerminal={IncludeTerminal}",
                includeTerminal);

            var items = await sharedQueue
                .ListAsync(includeTerminal, cancellationToken)
                .ConfigureAwait(false);

            return SharedQueueStatusResult.FromItems(
                items,
                includeTerminal);
        }
    }
}