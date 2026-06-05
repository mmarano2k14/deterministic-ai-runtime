using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;

namespace Multiplexed.AI.McpServer.Tools
{
    /// <summary>
    /// Exposes MCP tools related to local runtime queue control-plane operations.
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
    /// IAiRuntimeInstanceCapacityStore
    ///     -> IAiRuntimeInstanceProviderRouter
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
    /// when provider resolution fails.
    /// </para>
    /// </remarks>
    [McpServerToolType]
    public sealed class RuntimeQueueMcpTools
    {
        private readonly IAiRuntimeQueueControlPlane runtimeQueueControlPlane;
        private readonly IAiSharedRuntimeInstanceRegistry sharedRuntimeInstanceRegistry;
        private readonly IAiRuntimeInstanceCapacityStore capacityStore;
        private readonly IAiRuntimeInstanceProviderRouter providerRouter;
        private readonly ILogger<RuntimeQueueMcpTools> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RuntimeQueueMcpTools"/> class.
        /// </summary>
        /// <param name="runtimeQueueControlPlane">The root runtime queue control-plane.</param>
        /// <param name="sharedRuntimeInstanceRegistry">
        /// The shared runtime instance registry used as a legacy fallback path.
        /// </param>
        /// <param name="capacityStore">
        /// The runtime instance capacity store used to resolve runtime instance descriptors.
        /// </param>
        /// <param name="providerRouter">
        /// The runtime instance provider router used to resolve provider capabilities.
        /// </param>
        /// <param name="logger">The logger.</param>
        public RuntimeQueueMcpTools(
            IAiRuntimeQueueControlPlane runtimeQueueControlPlane,
            IAiSharedRuntimeInstanceRegistry sharedRuntimeInstanceRegistry,
            IAiRuntimeInstanceCapacityStore capacityStore,
            IAiRuntimeInstanceProviderRouter providerRouter,
            ILogger<RuntimeQueueMcpTools> logger)
        {
            this.runtimeQueueControlPlane =
                runtimeQueueControlPlane
                ?? throw new ArgumentNullException(nameof(runtimeQueueControlPlane));

            this.sharedRuntimeInstanceRegistry =
                sharedRuntimeInstanceRegistry
                ?? throw new ArgumentNullException(nameof(sharedRuntimeInstanceRegistry));

            this.capacityStore =
                capacityStore
                ?? throw new ArgumentNullException(nameof(capacityStore));

            this.providerRouter =
                providerRouter
                ?? throw new ArgumentNullException(nameof(providerRouter));

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

            var providerResolution =
                await ResolveStatusProviderAsync(
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (providerResolution.Provider is null ||
                providerResolution.Descriptor is null)
            {
                logger.LogWarning(
                    "Provider-aware runtime queue status resolution failed. Falling back to legacy queue control-plane resolution. RuntimeInstanceId={RuntimeInstanceId}, Reason={Reason}",
                    request.RuntimeInstanceId,
                    providerResolution.FailureReason);

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
                providerResolution.Provider.GetType().FullName ?? providerResolution.Provider.GetType().Name);

            return await providerResolution.Provider
                .GetQueueStatusAsync(
                    providerResolution.Descriptor,
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

            var providerResolution =
                await ResolveStatusProviderAsync(
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (providerResolution.Provider is null ||
                providerResolution.Descriptor is null)
            {
                logger.LogWarning(
                    "Provider-aware runtime run status resolution failed. Falling back to legacy queue control-plane resolution. RuntimeInstanceId={RuntimeInstanceId}, RunId={RunId}, Reason={Reason}",
                    request.RuntimeInstanceId,
                    request.RunId,
                    providerResolution.FailureReason);

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
                providerResolution.Provider.GetType().FullName ?? providerResolution.Provider.GetType().Name);

            return await providerResolution.Provider
                .GetRunStatusAsync(
                    providerResolution.Descriptor,
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

            var providerResolution =
                await ResolveControlProviderAsync(
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (providerResolution.Provider is null ||
                providerResolution.Descriptor is null)
            {
                logger.LogWarning(
                    "Provider-aware runtime queue pause resolution failed. Falling back to legacy queue control-plane resolution. RuntimeInstanceId={RuntimeInstanceId}, Reason={Reason}",
                    request.RuntimeInstanceId,
                    providerResolution.FailureReason);

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
                providerResolution.Provider.GetType().FullName ?? providerResolution.Provider.GetType().Name);

            return await providerResolution.Provider
                .PauseQueueAsync(
                    providerResolution.Descriptor,
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

            var providerResolution =
                await ResolveControlProviderAsync(
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (providerResolution.Provider is null ||
                providerResolution.Descriptor is null)
            {
                logger.LogWarning(
                    "Provider-aware runtime queue resume resolution failed. Falling back to legacy queue control-plane resolution. RuntimeInstanceId={RuntimeInstanceId}, Reason={Reason}",
                    request.RuntimeInstanceId,
                    providerResolution.FailureReason);

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
                providerResolution.Provider.GetType().FullName ?? providerResolution.Provider.GetType().Name);

            return await providerResolution.Provider
                .ResumeQueueAsync(
                    providerResolution.Descriptor,
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

            var providerResolution =
                await ResolveControlProviderAsync(
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (providerResolution.Provider is null ||
                providerResolution.Descriptor is null)
            {
                logger.LogWarning(
                    "Provider-aware runtime run cancel resolution failed. Falling back to legacy queue control-plane resolution. RuntimeInstanceId={RuntimeInstanceId}, RunId={RunId}, Reason={Reason}",
                    request.RuntimeInstanceId,
                    request.RunId,
                    providerResolution.FailureReason);

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
                providerResolution.Provider.GetType().FullName ?? providerResolution.Provider.GetType().Name);

            return await providerResolution.Provider
                .CancelRunAsync(
                    providerResolution.Descriptor,
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

            var providerResolution =
                await ResolveControlProviderAsync(
                        request.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (providerResolution.Provider is null ||
                providerResolution.Descriptor is null)
            {
                logger.LogWarning(
                    "Provider-aware queued runtime run cancel resolution failed. Falling back to legacy queue control-plane resolution. RuntimeInstanceId={RuntimeInstanceId}, RunId={RunId}, Reason={Reason}",
                    request.RuntimeInstanceId,
                    request.RunId,
                    providerResolution.FailureReason);

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
                providerResolution.Provider.GetType().FullName ?? providerResolution.Provider.GetType().Name);

            return await providerResolution.Provider
                .CancelQueuedRunAsync(
                    providerResolution.Descriptor,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Resolves the provider status capability for a runtime instance.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The status provider resolution.</returns>
        private async Task<RuntimeInstanceStatusProviderResolution> ResolveStatusProviderAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            var descriptor =
                await capacityStore
                    .GetAsync(
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (descriptor is null)
            {
                return RuntimeInstanceStatusProviderResolution.Failed(
                    $"Runtime instance capacity descriptor '{runtimeInstanceId}' was not found.");
            }

            if (!providerRouter.TryGetProvider<IAiRuntimeInstanceStatusProvider>(
                    descriptor,
                    out var provider))
            {
                return RuntimeInstanceStatusProviderResolution.Failed(
                    $"No runtime instance status provider was found for runtime instance '{runtimeInstanceId}'.");
            }

            return RuntimeInstanceStatusProviderResolution.Succeeded(
                descriptor,
                provider);
        }

        /// <summary>
        /// Resolves the provider control capability for a runtime instance.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The control provider resolution.</returns>
        private async Task<RuntimeInstanceControlProviderResolution> ResolveControlProviderAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            var descriptor =
                await capacityStore
                    .GetAsync(
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (descriptor is null)
            {
                return RuntimeInstanceControlProviderResolution.Failed(
                    $"Runtime instance capacity descriptor '{runtimeInstanceId}' was not found.");
            }

            if (!providerRouter.TryGetProvider<IAiRuntimeInstanceControlProvider>(
                    descriptor,
                    out var provider))
            {
                return RuntimeInstanceControlProviderResolution.Failed(
                    $"No runtime instance control provider was found for runtime instance '{runtimeInstanceId}'.");
            }

            return RuntimeInstanceControlProviderResolution.Succeeded(
                descriptor,
                provider);
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

        /// <summary>
        /// Represents the result of resolving a runtime instance status provider.
        /// </summary>
        private sealed class RuntimeInstanceStatusProviderResolution
        {
            /// <summary>
            /// Gets the runtime instance capacity descriptor.
            /// </summary>
            public AiRuntimeInstanceCapacityDescriptor? Descriptor { get; private init; }

            /// <summary>
            /// Gets the resolved runtime instance status provider.
            /// </summary>
            public IAiRuntimeInstanceStatusProvider? Provider { get; private init; }

            /// <summary>
            /// Gets the failure reason when provider resolution failed.
            /// </summary>
            public string? FailureReason { get; private init; }

            /// <summary>
            /// Creates a successful provider resolution.
            /// </summary>
            /// <param name="descriptor">The runtime instance capacity descriptor.</param>
            /// <param name="provider">The runtime instance status provider.</param>
            /// <returns>The provider resolution.</returns>
            public static RuntimeInstanceStatusProviderResolution Succeeded(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                IAiRuntimeInstanceStatusProvider provider)
            {
                ArgumentNullException.ThrowIfNull(descriptor);
                ArgumentNullException.ThrowIfNull(provider);

                return new RuntimeInstanceStatusProviderResolution
                {
                    Descriptor = descriptor,
                    Provider = provider
                };
            }

            /// <summary>
            /// Creates a failed provider resolution.
            /// </summary>
            /// <param name="failureReason">The provider resolution failure reason.</param>
            /// <returns>The provider resolution.</returns>
            public static RuntimeInstanceStatusProviderResolution Failed(
                string failureReason)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

                return new RuntimeInstanceStatusProviderResolution
                {
                    FailureReason = failureReason
                };
            }
        }

        /// <summary>
        /// Represents the result of resolving a runtime instance control provider.
        /// </summary>
        private sealed class RuntimeInstanceControlProviderResolution
        {
            /// <summary>
            /// Gets the runtime instance capacity descriptor.
            /// </summary>
            public AiRuntimeInstanceCapacityDescriptor? Descriptor { get; private init; }

            /// <summary>
            /// Gets the resolved runtime instance control provider.
            /// </summary>
            public IAiRuntimeInstanceControlProvider? Provider { get; private init; }

            /// <summary>
            /// Gets the failure reason when provider resolution failed.
            /// </summary>
            public string? FailureReason { get; private init; }

            /// <summary>
            /// Creates a successful provider resolution.
            /// </summary>
            /// <param name="descriptor">The runtime instance capacity descriptor.</param>
            /// <param name="provider">The runtime instance control provider.</param>
            /// <returns>The provider resolution.</returns>
            public static RuntimeInstanceControlProviderResolution Succeeded(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                IAiRuntimeInstanceControlProvider provider)
            {
                ArgumentNullException.ThrowIfNull(descriptor);
                ArgumentNullException.ThrowIfNull(provider);

                return new RuntimeInstanceControlProviderResolution
                {
                    Descriptor = descriptor,
                    Provider = provider
                };
            }

            /// <summary>
            /// Creates a failed provider resolution.
            /// </summary>
            /// <param name="failureReason">The provider resolution failure reason.</param>
            /// <returns>The provider resolution.</returns>
            public static RuntimeInstanceControlProviderResolution Failed(
                string failureReason)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

                return new RuntimeInstanceControlProviderResolution
                {
                    FailureReason = failureReason
                };
            }
        }
    }
}