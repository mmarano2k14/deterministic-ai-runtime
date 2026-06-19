namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation
{
    /// <summary>
    /// Describes the tenant isolation metadata of a runtime resource.
    /// This can represent either a runtime instance snapshot or a capacity descriptor.
    /// </summary>
    public sealed class AiRuntimeInstanceVisibilityDescriptor
    {
        /// <summary>
        /// Gets the runtime instance identifier, when available.
        /// </summary>
        public string? RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the tenant identifier declared by the runtime resource.
        /// </summary>
        public string? TenantId { get; init; }

        /// <summary>
        /// Gets the tenant group identifier declared by the runtime resource.
        /// </summary>
        public string? TenantGroupId { get; init; }

        /// <summary>
        /// Gets the isolation mode declared by the runtime resource.
        /// Missing metadata is interpreted as Shared for backward compatibility.
        /// </summary>
        public AiRuntimeInstanceIsolationMode IsolationMode { get; init; } =
            AiRuntimeInstanceIsolationMode.Shared;

        /// <summary>
        /// Gets whether the runtime resource allows shared fallback.
        /// </summary>
        public bool AllowSharedFallback { get; init; } = true;

        /// <summary>
        /// Gets whether dedicated capacity should be preferred.
        /// </summary>
        public bool PreferDedicatedCapacity { get; init; }

        /// <summary>
        /// Gets the original metadata used to build this descriptor.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();
    }
}