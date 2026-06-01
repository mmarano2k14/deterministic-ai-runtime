using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.AI.McpServer.Models.Responses;

namespace Multiplexed.AI.McpServer.Adapters
{
    /// <summary>
    /// Adapts MCP server requests to the shared runtime controller control-plane API.
    /// </summary>
    public sealed class SharedRuntimeControllerMcpAdapter
    {
        private readonly IAiSharedRuntimeController sharedRuntimeController;

        /// <summary>
        /// Initializes a new instance of the <see cref="SharedRuntimeControllerMcpAdapter"/> class.
        /// </summary>
        /// <param name="sharedRuntimeController">The shared runtime controller.</param>
        public SharedRuntimeControllerMcpAdapter(
            IAiSharedRuntimeController sharedRuntimeController)
        {
            this.sharedRuntimeController = sharedRuntimeController
                ?? throw new ArgumentNullException(nameof(sharedRuntimeController));
        }

        /// <summary>
        /// Submits a shared run through the shared runtime controller.
        /// </summary>
        /// <param name="request">The shared runtime controller request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The MCP-wrapped shared runtime controller result.</returns>
        public async Task<AiMcpToolResponse<AiSharedRuntimeControllerResult>> SubmitRunAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            request.Operation = AiSharedRuntimeControllerOperation.SubmitRun;

            var result = await sharedRuntimeController
                .SubmitRunAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                return AiMcpToolResponse<AiSharedRuntimeControllerResult>.Fail(
                    result.FailureReason ?? "Shared runtime controller submit operation failed.",
                    result.Message);
            }

            return AiMcpToolResponse<AiSharedRuntimeControllerResult>.Ok(
                result,
                result.Message);
        }
    }
}