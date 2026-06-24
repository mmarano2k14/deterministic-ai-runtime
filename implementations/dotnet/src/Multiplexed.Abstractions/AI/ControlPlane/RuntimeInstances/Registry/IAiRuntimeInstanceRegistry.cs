namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry
{
    /// <summary>
    /// Defines a registry for runtime instance visibility and heartbeat tracking.
    ///
    /// A runtime instance corresponds to one runtime process.
    /// In Kubernetes, one runtime instance normally corresponds to one pod / replica.
    /// </summary>
    /// <remarks>
    /// This registry is intended for control-plane, dashboard, MCP, HTTP API,
    /// CLI, shared run admission, autoscaling, and diagnostics.
    ///
    /// Implementations may be in-memory for single-process development or backed
    /// by Redis / distributed storage for multi-instance Kubernetes deployments.
    /// </remarks>
    public interface IAiRuntimeInstanceRegistry
    {
        /// <summary>
        /// Registers or updates a runtime instance.
        /// </summary>
        /// <param name="registration">The runtime instance registration.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The registered runtime instance snapshot.</returns>
        Task<AiRuntimeInstanceSnapshot> RegisterAsync(
            AiRuntimeInstanceRegistration registration,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Records a heartbeat for a runtime instance and updates its visible queue/run/worker state.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="queuedRunCount">The number of locally queued runs.</param>
        /// <param name="runningRunCount">The number of locally running runs.</param>
        /// <param name="activeRunCount">The number of active runs known by the local controller.</param>
        /// <param name="availableRunSlots">The number of available local run slots.</param>
        /// <param name="activeWorkerCount">The number of local workers currently assigned to active executions.</param>
        /// <param name="availableWorkerCount">The number of local workers currently available.</param>
        /// <param name="maxLocalWorkersPerExecution">The maximum number of local workers allowed per execution.</param>
        /// <param name="isQueuePaused">Whether the local runtime queue is paused.</param>
        /// <param name="canAcceptRun">Whether the runtime instance can accept a new local run.</param>
        /// <param name="status">The current runtime instance status.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The updated runtime instance snapshot.</returns>
        Task<AiRuntimeInstanceSnapshot?> HeartbeatAsync(
            string runtimeInstanceId,
            int queuedRunCount,
            int runningRunCount,
            int activeRunCount,
            int? availableRunSlots,
            int? activeWorkerCount,
            int? availableWorkerCount,
            int? maxLocalWorkersPerExecution,
            bool isQueuePaused,
            bool canAcceptRun,
            AiRuntimeInstanceStatus status,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a registered runtime instance snapshot.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>
        /// The runtime instance snapshot, or <c>null</c> when the instance is unknown.
        /// </returns>
        Task<AiRuntimeInstanceSnapshot?> GetAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists registered runtime instance snapshots.
        /// </summary>
        /// <param name="includeStopped">
        /// Indicates whether explicitly stopped runtime instances should be included.
        /// </param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The registered runtime instance snapshots.</returns>
        Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListAsync(
            bool includeStopped = false,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Marks a runtime instance as draining.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The updated runtime instance snapshot, or <c>null</c> when unknown.</returns>
        Task<AiRuntimeInstanceSnapshot?> MarkDrainingAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Unregisters a runtime instance by marking it as stopped.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The updated runtime instance snapshot, or <c>null</c> when unknown.</returns>
        Task<AiRuntimeInstanceSnapshot?> UnregisterAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Marks a runtime instance as unhealthy.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The updated runtime instance snapshot, or null when unknown.</returns>
        Task<AiRuntimeInstanceSnapshot?> MarkUnhealthyAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default);
    }
}