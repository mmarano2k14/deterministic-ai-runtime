namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation
{
    /// <summary>
    /// Evaluates whether runtime resources are visible and usable for a tenant.
    /// </summary>
    public interface IAiRuntimeInstanceVisibilityEvaluator
    {
        /// <summary>
        /// Determines whether the specified runtime resource is visible for the tenant.
        /// </summary>
        /// <param name="tenantId">The current tenant identifier.</param>
        /// <param name="tenantGroupId">The current tenant group identifier.</param>
        /// <param name="descriptor">The runtime resource visibility descriptor.</param>
        /// <returns>True when the resource is visible; otherwise false.</returns>
        bool IsVisible(
            string? tenantId,
            string? tenantGroupId,
            AiRuntimeInstanceVisibilityDescriptor descriptor);

        /// <summary>
        /// Creates a visibility descriptor from metadata.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier, when available.</param>
        /// <param name="metadata">The runtime metadata.</param>
        /// <returns>A visibility descriptor.</returns>
        AiRuntimeInstanceVisibilityDescriptor CreateDescriptor(
            string? runtimeInstanceId,
            IReadOnlyDictionary<string, string>? metadata);
    }
}