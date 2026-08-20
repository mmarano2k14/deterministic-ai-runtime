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

        /// <summary>
        /// Metadata key identifying the source that resolved runtime tenant settings.
        /// </summary>
        public const string SettingsSource = "runtime.settings.source";

        /// <summary>
        /// Metadata key carrying the tenant identifier used by runtime settings diagnostics.
        /// </summary>
        public const string RuntimeTenant = "runtime.tenant";
        /// <summary>
        /// Gets the legacy dotted-camel tenant-group identifier metadata key.
        /// </summary>
        public const string LegacyTenantGroupId = "tenant.groupId";


        /// <summary>
        /// Gets the camel-case tenant identifier metadata key used by compatibility and diagnostic payloads.
        /// </summary>
        public const string CamelCaseTenantId = "tenantId";

        /// <summary>
        /// Gets the camel-case tenant-group identifier metadata key used by compatibility and diagnostic payloads.
        /// </summary>
        public const string CamelCaseTenantGroupId = "tenantGroupId";
    }
}