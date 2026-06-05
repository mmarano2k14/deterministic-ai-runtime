using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Defines a provider capability for dispatching shared runs to runtime instances.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dispatch providers deliver a shared runtime instance dispatch request to the
    /// selected runtime instance.
    /// </para>
    /// <para>
    /// They must not execute DAG steps directly. The target runtime instance must still
    /// enqueue the run into its own local runtime queue.
    /// </para>
    /// </remarks>
    public interface IAiRuntimeInstanceDispatchProvider : IAiRuntimeInstanceProvider
    {
        /// <summary>
        /// Dispatches a shared run to the runtime instance represented by the descriptor.
        /// </summary>
        /// <param name="descriptor">The target runtime instance capacity descriptor.</param>
        /// <param name="request">The shared runtime instance dispatch request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The shared runtime instance dispatch result.</returns>
        Task<AiSharedRuntimeInstanceDispatchResult> DispatchAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            AiSharedRuntimeInstanceDispatchRequest request,
            CancellationToken cancellationToken = default);
    }
}