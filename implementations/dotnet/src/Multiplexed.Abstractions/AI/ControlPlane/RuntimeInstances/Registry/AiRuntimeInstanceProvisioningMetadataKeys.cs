namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry
{
    /// <summary>
    /// Defines canonical metadata keys used to describe runtime instance provisioning settings.
    /// </summary>
    /// <remarks>
    /// These keys preserve the existing physical metadata names propagated across admission,
    /// scale-out, host creation, local pool scaling, and runtime registration.
    /// </remarks>
    public static class AiRuntimeInstanceProvisioningMetadataKeys
    {
        /// <summary>
        /// Gets the metadata key carrying the maximum runtime instance count.
        /// </summary>
        public const string MaxRuntimeInstances = "runtime.maxRuntimeInstances";

        /// <summary>
        /// Gets the metadata key carrying the runtime instance identifier prefix.
        /// </summary>
        public const string RuntimeInstanceIdPrefix = "runtime.instanceIdPrefix";

        /// <summary>
        /// Gets the metadata key carrying the worker count per runtime instance.
        /// </summary>
        public const string WorkerCountPerInstance = "runtime.workerCountPerInstance";

        /// <summary>
        /// Gets the metadata key carrying the maximum concurrent run count per runtime instance.
        /// </summary>
        public const string MaxConcurrentRunsPerInstance = "runtime.maxConcurrentRunsPerInstance";

        /// <summary>
        /// Gets the metadata key carrying the local queue capacity per runtime instance.
        /// </summary>
        public const string LocalQueueCapacity = "runtime.localQueueCapacity";
    }
}
