using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;

namespace Multiplexed.AI.McpServer.Tools
{
    /// <summary>
    /// Exposes MCP tools related to the shared queue.
    /// </summary>
    [McpServerToolType]
    public sealed class SharedQueueMcpTools
    {
        private readonly IAiSharedQueuePump sharedQueuePump;
        private readonly ILogger<SharedQueueMcpTools> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="SharedQueueMcpTools"/> class.
        /// </summary>
        /// <param name="sharedQueuePump">The shared queue pump.</param>
        /// <param name="logger">The logger.</param>
        public SharedQueueMcpTools(
            IAiSharedQueuePump sharedQueuePump,
            ILogger<SharedQueueMcpTools> logger)
        {
            this.sharedQueuePump = sharedQueuePump
                ?? throw new ArgumentNullException(nameof(sharedQueuePump));

            this.logger = logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Executes one shared queue pump cycle manually.
        /// </summary>
        /// <param name="request">The shared queue pump request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The pump result.</returns>
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
    }
}