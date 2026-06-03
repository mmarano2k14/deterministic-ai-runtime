using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;

namespace Multiplexed.AI.McpServer.Tools
{
    /// <summary>
    /// Exposes MCP tools related to local runtime queue control-plane operations.
    /// </summary>
    /// <remarks>
    /// This tool class controls runtime queues through the existing control-plane abstraction.
    /// When a runtime instance id is provided, operations are routed to the matching
    /// shared runtime instance endpoint. Otherwise, the root runtime queue control-plane
    /// is used as a fallback.
    /// </remarks>
    [McpServerToolType]
    public sealed class RuntimeQueueMcpTools
    {
        private readonly IAiRuntimeQueueControlPlane runtimeQueueControlPlane;
        private readonly IAiSharedRuntimeInstanceRegistry sharedRuntimeInstanceRegistry;
        private readonly ILogger<RuntimeQueueMcpTools> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RuntimeQueueMcpTools"/> class.
        /// </summary>
        /// <param name="runtimeQueueControlPlane">The root runtime queue control-plane.</param>
        /// <param name="sharedRuntimeInstanceRegistry">The shared runtime instance registry.</param>
        /// <param name="logger">The logger.</param>
        public RuntimeQueueMcpTools(
            IAiRuntimeQueueControlPlane runtimeQueueControlPlane,
            IAiSharedRuntimeInstanceRegistry sharedRuntimeInstanceRegistry,
            ILogger<RuntimeQueueMcpTools> logger)
        {
            this.runtimeQueueControlPlane = runtimeQueueControlPlane
                ?? throw new ArgumentNullException(nameof(runtimeQueueControlPlane));

            this.sharedRuntimeInstanceRegistry = sharedRuntimeInstanceRegistry
                ?? throw new ArgumentNullException(nameof(sharedRuntimeInstanceRegistry));

            this.logger = logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Gets the local runtime queue status.
        /// </summary>
        /// <param name="request">The runtime queue control-plane request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The runtime queue control-plane result.</returns>
        [McpServerTool(Name = "runtime_queue.status")]
        [Description("Gets the current visibility state of the local runtime queue.")]
        public async Task<AiRuntimeQueueControlPlaneResult> GetQueueStatusAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            logger.LogInformation(
                "MCP runtime_queue.status called. RuntimeInstanceId={RuntimeInstanceId}, CorrelationId={CorrelationId}, RequestedBy={RequestedBy}",
                request.RuntimeInstanceId,
                request.CorrelationId,
                request.RequestedBy);

            var controlPlane =
                await ResolveRuntimeQueueControlPlaneAsync(
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            return await controlPlane
                .GetQueueStatusAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the status of a local runtime run.
        /// </summary>
        /// <param name="request">The runtime queue control-plane request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The runtime queue control-plane result.</returns>
        [McpServerTool(Name = "runtime_queue.run_status")]
        [Description("Gets the current visibility state of a local runtime run.")]
        public async Task<AiRuntimeQueueControlPlaneResult> GetRunStatusAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            logger.LogInformation(
                "MCP runtime_queue.run_status called. RuntimeInstanceId={RuntimeInstanceId}, RunId={RunId}, CorrelationId={CorrelationId}, RequestedBy={RequestedBy}",
                request.RuntimeInstanceId,
                request.RunId,
                request.CorrelationId,
                request.RequestedBy);

            var controlPlane =
                await ResolveRuntimeQueueControlPlaneAsync(
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            return await controlPlane
                .GetRunStatusAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Pauses the local runtime queue.
        /// </summary>
        /// <param name="request">The runtime queue control-plane request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The runtime queue control-plane result.</returns>
        [McpServerTool(Name = "runtime_queue.pause")]
        [Description("Pauses the local runtime queue.")]
        public async Task<AiRuntimeQueueControlPlaneResult> PauseQueueAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            logger.LogInformation(
                "MCP runtime_queue.pause called. RuntimeInstanceId={RuntimeInstanceId}, Reason={Reason}, RequestedBy={RequestedBy}",
                request.RuntimeInstanceId,
                request.Reason,
                request.RequestedBy);

            var controlPlane =
                await ResolveRuntimeQueueControlPlaneAsync(
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            return await controlPlane
                .PauseQueueAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Resumes the local runtime queue.
        /// </summary>
        /// <param name="request">The runtime queue control-plane request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The runtime queue control-plane result.</returns>
        [McpServerTool(Name = "runtime_queue.resume")]
        [Description("Resumes the local runtime queue.")]
        public async Task<AiRuntimeQueueControlPlaneResult> ResumeQueueAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            logger.LogInformation(
                "MCP runtime_queue.resume called. RuntimeInstanceId={RuntimeInstanceId}, Reason={Reason}, RequestedBy={RequestedBy}",
                request.RuntimeInstanceId,
                request.Reason,
                request.RequestedBy);

            var controlPlane =
                await ResolveRuntimeQueueControlPlaneAsync(
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            return await controlPlane
                .ResumeQueueAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Cancels a local runtime run.
        /// </summary>
        /// <param name="request">The runtime queue control-plane request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The runtime queue control-plane result.</returns>
        [McpServerTool(Name = "runtime_queue.cancel_run")]
        [Description("Cancels a local runtime run by run id.")]
        public async Task<AiRuntimeQueueControlPlaneResult> CancelRunAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            logger.LogInformation(
                "MCP runtime_queue.cancel_run called. RuntimeInstanceId={RuntimeInstanceId}, RunId={RunId}, Reason={Reason}, RequestedBy={RequestedBy}",
                request.RuntimeInstanceId,
                request.RunId,
                request.Reason,
                request.RequestedBy);

            var controlPlane =
                await ResolveRuntimeQueueControlPlaneAsync(
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            return await controlPlane
                .CancelRunAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Cancels a local runtime run that is still queued.
        /// </summary>
        /// <param name="request">The runtime queue control-plane request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The runtime queue control-plane result.</returns>
        [McpServerTool(Name = "runtime_queue.cancel_queued_run")]
        [Description("Cancels a local runtime run that is still queued.")]
        public async Task<AiRuntimeQueueControlPlaneResult> CancelQueuedRunAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            logger.LogInformation(
                "MCP runtime_queue.cancel_queued_run called. RuntimeInstanceId={RuntimeInstanceId}, RunId={RunId}, Reason={Reason}, RequestedBy={RequestedBy}",
                request.RuntimeInstanceId,
                request.RunId,
                request.Reason,
                request.RequestedBy);

            var controlPlane =
                await ResolveRuntimeQueueControlPlaneAsync(
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            return await controlPlane
                .CancelQueuedRunAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Resolves the runtime queue control-plane for a runtime instance.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The resolved runtime queue control-plane.</returns>
        private async Task<IAiRuntimeQueueControlPlane> ResolveRuntimeQueueControlPlaneAsync(
            string? runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(runtimeInstanceId))
            {
                return runtimeQueueControlPlane;
            }

            var sharedRuntimeInstance =
                await sharedRuntimeInstanceRegistry
                    .GetAsync(runtimeInstanceId, cancellationToken)
                    .ConfigureAwait(false);

            if (sharedRuntimeInstance is null)
            {
                logger.LogWarning(
                    "Runtime instance not found in shared registry. Falling back to root runtime queue control-plane. RuntimeInstanceId={RuntimeInstanceId}",
                    runtimeInstanceId);

                return runtimeQueueControlPlane;
            }

            return sharedRuntimeInstance.QueueControlPlane;
        }
    }
}