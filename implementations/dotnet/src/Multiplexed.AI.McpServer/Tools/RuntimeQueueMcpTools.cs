using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;

namespace Multiplexed.AI.McpServer.Tools
{
    /// <summary>
    /// Exposes MCP tools related to runtime queue control-plane operations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This tool class controls runtime queues through the existing control-plane abstraction.
    /// </para>
    ///
    /// <para>
    /// When a runtime instance id is provided, provider-aware operations are routed through:
    /// </para>
    ///
    /// <code>
    /// IAiRuntimeInstanceProviderCapabilityResolver
    ///     -> IAiRuntimeInstanceStatusProvider / IAiRuntimeInstanceControlProvider
    /// </code>
    ///
    /// <para>
    /// When no runtime instance id is provided, the root runtime queue control-plane
    /// is used as a local fallback.
    /// </para>
    ///
    /// <para>
    /// The legacy shared runtime instance registry path is preserved as a fallback
    /// when provider capability resolution fails.
    /// </para>
    /// </remarks>
    [McpServerToolType]
    public sealed class RuntimeQueueMcpTools
    {
        /// <summary>
        /// The root runtime queue control-plane used when no runtime instance id is provided.
        /// </summary>
        private readonly IAiRuntimeQueueControlPlane runtimeQueueControlPlane;

        /// <summary>
        /// The shared runtime instance registry used as a legacy fallback path.
        /// </summary>
        private readonly IAiSharedRuntimeInstanceRegistry sharedRuntimeInstanceRegistry;

        /// <summary>
        /// The provider capability resolver used to resolve status and control providers.
        /// </summary>
        private readonly IAiRuntimeInstanceProviderCapabilityResolver providerCapabilityResolver;

        /// <summary>
        /// The logger.
        /// </summary>
        private readonly ILogger<RuntimeQueueMcpTools> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RuntimeQueueMcpTools"/> class.
        /// </summary>
        /// <param name="runtimeQueueControlPlane">The root runtime queue control-plane.</param>
        /// <param name="sharedRuntimeInstanceRegistry">
        /// The shared runtime instance registry used as a legacy fallback path.
        /// </param>
        /// <param name="providerCapabilityResolver">
        /// The provider capability resolver used to resolve runtime instance providers.
        /// </param>
        /// <param name="logger">The logger.</param>
        public RuntimeQueueMcpTools(
            IAiRuntimeQueueControlPlane runtimeQueueControlPlane,
            IAiSharedRuntimeInstanceRegistry sharedRuntimeInstanceRegistry,
            IAiRuntimeInstanceProviderCapabilityResolver providerCapabilityResolver,
            ILogger<RuntimeQueueMcpTools> logger)
        {
            this.runtimeQueueControlPlane =
                runtimeQueueControlPlane
                ?? throw new ArgumentNullException(nameof(runtimeQueueControlPlane));

            this.sharedRuntimeInstanceRegistry =
                sharedRuntimeInstanceRegistry
                ?? throw new ArgumentNullException(nameof(sharedRuntimeInstanceRegistry));

            this.providerCapabilityResolver =
                providerCapabilityResolver
                ?? throw new ArgumentNullException(nameof(providerCapabilityResolver));

            this.logger =
                logger
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

            if (string.IsNullOrWhiteSpace(request.RuntimeInstanceId))
            {
                return await runtimeQueueControlPlane
                    .GetQueueStatusAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var resolution =
                await providerCapabilityResolver
                    .ResolveAsync<IAiRuntimeInstanceStatusProvider>(
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!resolution.Success ||
                resolution.Provider is null ||
                resolution.Descriptor is null)
            {
                logger.LogWarning(
                    "Provider-aware runtime queue status resolution failed. Falling back to legacy queue control-plane resolution. RuntimeInstanceId={RuntimeInstanceId}, Reason={Reason}",
                    request.RuntimeInstanceId,
                    resolution.FailureReason);

                var controlPlane =
                    await ResolveRuntimeQueueControlPlaneAsync(
                            request.RuntimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                return await controlPlane
                    .GetQueueStatusAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            logger.LogInformation(
                "MCP runtime_queue.status routed through provider. RuntimeInstanceId={RuntimeInstanceId}, ProviderType={ProviderType}",
                request.RuntimeInstanceId,
                resolution.Provider.GetType().FullName ?? resolution.Provider.GetType().Name);

            return await resolution.Provider
                .GetQueueStatusAsync(
                    resolution.Descriptor,
                    request,
                    cancellationToken)
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

            if (string.IsNullOrWhiteSpace(request.RuntimeInstanceId))
            {
                return await runtimeQueueControlPlane
                    .GetRunStatusAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var resolution =
                await providerCapabilityResolver
                    .ResolveAsync<IAiRuntimeInstanceStatusProvider>(
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!resolution.Success ||
                resolution.Provider is null ||
                resolution.Descriptor is null)
            {
                logger.LogWarning(
                    "Provider-aware runtime run status resolution failed. Falling back to legacy queue control-plane resolution. RuntimeInstanceId={RuntimeInstanceId}, RunId={RunId}, Reason={Reason}",
                    request.RuntimeInstanceId,
                    request.RunId,
                    resolution.FailureReason);

                var controlPlane =
                    await ResolveRuntimeQueueControlPlaneAsync(
                            request.RuntimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                return await controlPlane
                    .GetRunStatusAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            logger.LogInformation(
                "MCP runtime_queue.run_status routed through provider. RuntimeInstanceId={RuntimeInstanceId}, RunId={RunId}, ProviderType={ProviderType}",
                request.RuntimeInstanceId,
                request.RunId,
                resolution.Provider.GetType().FullName ?? resolution.Provider.GetType().Name);

            return await resolution.Provider
                .GetRunStatusAsync(
                    resolution.Descriptor,
                    request,
                    cancellationToken)
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

            if (string.IsNullOrWhiteSpace(request.RuntimeInstanceId))
            {
                return await runtimeQueueControlPlane
                    .PauseQueueAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var resolution =
                await providerCapabilityResolver
                    .ResolveAsync<IAiRuntimeInstanceControlProvider>(
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!resolution.Success ||
                resolution.Provider is null ||
                resolution.Descriptor is null)
            {
                logger.LogWarning(
                    "Provider-aware runtime queue pause resolution failed. Falling back to legacy queue control-plane resolution. RuntimeInstanceId={RuntimeInstanceId}, Reason={Reason}",
                    request.RuntimeInstanceId,
                    resolution.FailureReason);

                var controlPlane =
                    await ResolveRuntimeQueueControlPlaneAsync(
                            request.RuntimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                return await controlPlane
                    .PauseQueueAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            logger.LogInformation(
                "MCP runtime_queue.pause routed through provider. RuntimeInstanceId={RuntimeInstanceId}, ProviderType={ProviderType}",
                request.RuntimeInstanceId,
                resolution.Provider.GetType().FullName ?? resolution.Provider.GetType().Name);

            return await resolution.Provider
                .PauseQueueAsync(
                    resolution.Descriptor,
                    request,
                    cancellationToken)
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

            if (string.IsNullOrWhiteSpace(request.RuntimeInstanceId))
            {
                return await runtimeQueueControlPlane
                    .ResumeQueueAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var resolution =
                await providerCapabilityResolver
                    .ResolveAsync<IAiRuntimeInstanceControlProvider>(
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!resolution.Success ||
                resolution.Provider is null ||
                resolution.Descriptor is null)
            {
                logger.LogWarning(
                    "Provider-aware runtime queue resume resolution failed. Falling back to legacy queue control-plane resolution. RuntimeInstanceId={RuntimeInstanceId}, Reason={Reason}",
                    request.RuntimeInstanceId,
                    resolution.FailureReason);

                var controlPlane =
                    await ResolveRuntimeQueueControlPlaneAsync(
                            request.RuntimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                return await controlPlane
                    .ResumeQueueAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            logger.LogInformation(
                "MCP runtime_queue.resume routed through provider. RuntimeInstanceId={RuntimeInstanceId}, ProviderType={ProviderType}",
                request.RuntimeInstanceId,
                resolution.Provider.GetType().FullName ?? resolution.Provider.GetType().Name);

            return await resolution.Provider
                .ResumeQueueAsync(
                    resolution.Descriptor,
                    request,
                    cancellationToken)
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

            if (string.IsNullOrWhiteSpace(request.RuntimeInstanceId))
            {
                return await runtimeQueueControlPlane
                    .CancelRunAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var resolution =
                await providerCapabilityResolver
                    .ResolveAsync<IAiRuntimeInstanceControlProvider>(
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!resolution.Success ||
                resolution.Provider is null ||
                resolution.Descriptor is null)
            {
                logger.LogWarning(
                    "Provider-aware runtime run cancel resolution failed. Falling back to legacy queue control-plane resolution. RuntimeInstanceId={RuntimeInstanceId}, RunId={RunId}, Reason={Reason}",
                    request.RuntimeInstanceId,
                    request.RunId,
                    resolution.FailureReason);

                var controlPlane =
                    await ResolveRuntimeQueueControlPlaneAsync(
                            request.RuntimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                return await controlPlane
                    .CancelRunAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            logger.LogInformation(
                "MCP runtime_queue.cancel_run routed through provider. RuntimeInstanceId={RuntimeInstanceId}, RunId={RunId}, ProviderType={ProviderType}",
                request.RuntimeInstanceId,
                request.RunId,
                resolution.Provider.GetType().FullName ?? resolution.Provider.GetType().Name);

            return await resolution.Provider
                .CancelRunAsync(
                    resolution.Descriptor,
                    request,
                    cancellationToken)
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

            if (string.IsNullOrWhiteSpace(request.RuntimeInstanceId))
            {
                return await runtimeQueueControlPlane
                    .CancelQueuedRunAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var resolution =
                await providerCapabilityResolver
                    .ResolveAsync<IAiRuntimeInstanceControlProvider>(
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!resolution.Success ||
                resolution.Provider is null ||
                resolution.Descriptor is null)
            {
                logger.LogWarning(
                    "Provider-aware queued runtime run cancel resolution failed. Falling back to legacy queue control-plane resolution. RuntimeInstanceId={RuntimeInstanceId}, RunId={RunId}, Reason={Reason}",
                    request.RuntimeInstanceId,
                    request.RunId,
                    resolution.FailureReason);

                var controlPlane =
                    await ResolveRuntimeQueueControlPlaneAsync(
                            request.RuntimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                return await controlPlane
                    .CancelQueuedRunAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            logger.LogInformation(
                "MCP runtime_queue.cancel_queued_run routed through provider. RuntimeInstanceId={RuntimeInstanceId}, RunId={RunId}, ProviderType={ProviderType}",
                request.RuntimeInstanceId,
                request.RunId,
                resolution.Provider.GetType().FullName ?? resolution.Provider.GetType().Name);

            return await resolution.Provider
                .CancelQueuedRunAsync(
                    resolution.Descriptor,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Resolves the runtime queue control-plane for a runtime instance using the legacy local registry path.
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
                    .GetAsync(
                        runtimeInstanceId,
                        cancellationToken)
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