using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Defines a provider capability for reading runtime instance run and queue status.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Status providers expose runtime queue visibility through the provider model.
    /// </para>
    ///
    /// <para>
    /// This capability must reuse the existing runtime queue control-plane request
    /// and result models. It must not introduce duplicate run-status DTOs.
    /// </para>
    ///
    /// <para>
    /// Providers must not replace local runtime queues. They only route status
    /// requests to the selected runtime instance through the appropriate transport.
    /// </para>
    /// </remarks>
    public interface IAiRuntimeInstanceStatusProvider : IAiRuntimeInstanceProvider
    {
        /// <summary>
        /// Gets the status of a runtime run from the runtime instance represented
        /// by the supplied capacity descriptor.
        /// </summary>
        /// <param name="descriptor">The target runtime instance capacity descriptor.</param>
        /// <param name="request">The runtime queue control-plane request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The runtime queue control-plane result.</returns>
        Task<AiRuntimeQueueControlPlaneResult> GetRunStatusAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the status of the runtime queue from the runtime instance represented
        /// by the supplied capacity descriptor.
        /// </summary>
        /// <param name="descriptor">The target runtime instance capacity descriptor.</param>
        /// <param name="request">The runtime queue control-plane request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The runtime queue control-plane result.</returns>
        Task<AiRuntimeQueueControlPlaneResult> GetQueueStatusAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default);
    }
}