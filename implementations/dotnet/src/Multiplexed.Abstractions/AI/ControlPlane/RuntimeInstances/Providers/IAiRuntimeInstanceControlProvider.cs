using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Defines a provider capability for controlling runtime instance queues and runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Control providers expose runtime queue control operations through the provider model.
    /// </para>
    ///
    /// <para>
    /// This capability must reuse the existing runtime queue control-plane request
    /// and result models. It must not introduce duplicate pause, resume, cancel,
    /// or queue-control DTOs.
    /// </para>
    ///
    /// <para>
    /// Providers must not replace local runtime queues. They only route control
    /// requests to the selected runtime instance through the appropriate transport.
    /// </para>
    /// </remarks>
    public interface IAiRuntimeInstanceControlProvider : IAiRuntimeInstanceProvider
    {
        /// <summary>
        /// Pauses the runtime queue owned by the runtime instance represented by the descriptor.
        /// </summary>
        /// <param name="descriptor">The target runtime instance capacity descriptor.</param>
        /// <param name="request">The runtime queue control-plane request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The runtime queue control-plane result.</returns>
        Task<AiRuntimeQueueControlPlaneResult> PauseQueueAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Resumes the runtime queue owned by the runtime instance represented by the descriptor.
        /// </summary>
        /// <param name="descriptor">The target runtime instance capacity descriptor.</param>
        /// <param name="request">The runtime queue control-plane request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The runtime queue control-plane result.</returns>
        Task<AiRuntimeQueueControlPlaneResult> ResumeQueueAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Cancels a runtime run owned by the runtime instance represented by the descriptor.
        /// </summary>
        /// <param name="descriptor">The target runtime instance capacity descriptor.</param>
        /// <param name="request">The runtime queue control-plane request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The runtime queue control-plane result.</returns>
        Task<AiRuntimeQueueControlPlaneResult> CancelRunAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Cancels a queued runtime run owned by the runtime instance represented by the descriptor.
        /// </summary>
        /// <param name="descriptor">The target runtime instance capacity descriptor.</param>
        /// <param name="request">The runtime queue control-plane request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The runtime queue control-plane result.</returns>
        Task<AiRuntimeQueueControlPlaneResult> CancelQueuedRunAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default);
    }
}