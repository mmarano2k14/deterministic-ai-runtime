using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool
{
    /// <summary>
    /// Represents a locally hosted runtime instance managed by a runtime instance pool.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Encapsulates an independent runtime instance.
    /// - Owns its own runtime controller.
    /// - Owns its own local runtime queue.
    /// - Owns its own workers.
    /// - Can be registered into a shared runtime registry.
    ///
    /// IMPORTANT:
    /// - One host instance represents one logical runtime instance.
    /// - Multiple local runtime instances may coexist inside a single process.
    /// - This abstraction is intended for local benchmarking,
    ///   MCP control-plane hosting, and future Kubernetes providers.
    /// </remarks>
    public interface IAiLocalRuntimeInstanceHost :
        IAsyncDisposable
    {
        /// <summary>
        /// Gets the runtime instance identifier.
        /// </summary>
        string RuntimeInstanceId { get; }

        /// <summary>
        /// Gets the worker count assigned to this runtime instance.
        /// </summary>
        int WorkerCount { get; }

        /// <summary>
        /// Gets the runtime controller.
        /// </summary>
        IAiRuntimePipelineBackgroundController Controller { get; }

        /// <summary>
        /// Gets the local runtime queue control plane.
        /// </summary>
        IAiRuntimeQueueControlPlane QueueControlPlane { get; }

        /// <summary>
        /// Gets the shared runtime instance representation.
        /// </summary>
        IAiSharedRuntimeInstance SharedRuntimeInstance { get; }

        /// <summary>
        /// Starts the runtime instance.
        /// </summary>
        /// <param name="cancellationToken">
        /// A token used to cancel the operation.
        /// </param>
        /// <returns>A task representing the operation.</returns>
        Task StartAsync(
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Stops the runtime instance.
        /// </summary>
        /// <param name="cancellationToken">
        /// A token used to cancel the operation.
        /// </param>
        /// <returns>A task representing the operation.</returns>
        Task StopAsync(
            CancellationToken cancellationToken = default);
    }
}