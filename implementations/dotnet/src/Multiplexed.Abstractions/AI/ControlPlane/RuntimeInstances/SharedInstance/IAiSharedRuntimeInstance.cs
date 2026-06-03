using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance
{
    /// <summary>
    /// Represents a runtime instance that can be addressed by the shared control-plane.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Provides a transport-neutral abstraction for dispatching shared runs
    ///   into a specific runtime instance.
    ///
    /// EXAMPLES:
    /// - In-memory test instance.
    /// - Local runtime instance.
    /// - HTTP/gRPC Kubernetes pod adapter.
    /// - Redis command queue backed runtime instance.
    ///
    /// IMPORTANT:
    /// - This is not only metadata.
    /// - This object represents a runtime instance capable of receiving work.
    /// </remarks>
    public interface IAiSharedRuntimeInstance
    {
        /// <summary>
        /// Gets the runtime instance identifier.
        /// </summary>
        string RuntimeInstanceId { get; }

        /// <summary>
        /// Gets the runtime queue control-plane associated with this runtime instance.
        /// </summary>
        IAiRuntimeQueueControlPlane QueueControlPlane { get; }

        /// <summary>
        /// Dispatches a shared run to this runtime instance.
        /// </summary>
        Task<AiSharedRuntimeInstanceDispatchResult> DispatchAsync(
            AiSharedRuntimeInstanceDispatchRequest request,
            CancellationToken cancellationToken = default);
    }
}