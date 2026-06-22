namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation
{
    /// <summary>
    /// Defines metadata keys used to describe runtime instance tenant isolation.
    /// </summary>
    public static class AiRuntimeInstanceIsolationMetadataKeys
    {
        /// <summary>
        /// Metadata key containing the tenant identifier owning a dedicated runtime instance.
        /// </summary>
        public const string TenantId = "tenant.id";

        /// <summary>
        /// Metadata key containing the tenant group identifier owning a dedicated runtime instance.
        /// </summary>
        public const string TenantGroupId = "tenant.group.id";

        /// <summary>
        /// Metadata key containing the runtime isolation mode.
        /// </summary>
        public const string IsolationMode = "runtime.isolationMode";

        /// <summary>
        /// Metadata key indicating whether shared fallback is allowed.
        /// </summary>
        public const string AllowSharedFallback = "runtime.allowSharedFallback";

        /// <summary>
        /// Metadata key indicating whether dedicated capacity is preferred.
        /// </summary>
        public const string PreferDedicatedCapacity = "runtime.preferDedicatedCapacity";
    }
}