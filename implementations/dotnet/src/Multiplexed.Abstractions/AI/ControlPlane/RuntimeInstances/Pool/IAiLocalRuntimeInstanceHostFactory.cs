namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool
{
    /// <summary>
    /// Creates local runtime instance hosts.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Centralizes creation of independent local runtime instances.
    /// - Allows a runtime instance pool to create multiple isolated runtime instances.
    /// - Provides a bridge between local multi-instance execution and future
    ///   Kubernetes or external host providers.
    ///
    /// IMPORTANT:
    /// - Each created host must own its own runtime controller.
    /// - Each created host must own its own local runtime queue.
    /// - Each created host must expose a shared runtime instance.
    /// - Implementations should isolate runtime instances from each other.
    /// </remarks>
    public interface IAiLocalRuntimeInstanceHostFactory
    {
        /// <summary>
        /// Creates a local runtime instance host.
        /// </summary>
        /// <param name="runtimeInstanceId">
        /// The runtime instance identifier.
        /// </param>
        /// <param name="workerCount">
        /// The worker count assigned to the runtime instance.
        /// </param>
        /// <param name="maxConcurrentRuns">
        /// The maximum number of concurrent runs allowed by the runtime instance.
        /// </param>
        /// <param name="localQueueCapacity">
        /// The local queue capacity.
        /// Null means unlimited.
        /// </param>
        /// <param name="metadata">
        /// Optional metadata copied to the runtime instance registration,
        /// capacity descriptor, and provider metadata.
        /// </param>
        /// <param name="cancellationToken">
        /// A token used to cancel the operation.
        /// </param>
        /// <returns>
        /// The created runtime instance host.
        /// </returns>
        Task<IAiLocalRuntimeInstanceHost> CreateAsync(
            string runtimeInstanceId,
            int workerCount,
            int maxConcurrentRuns,
            int? localQueueCapacity,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default);
    }
}