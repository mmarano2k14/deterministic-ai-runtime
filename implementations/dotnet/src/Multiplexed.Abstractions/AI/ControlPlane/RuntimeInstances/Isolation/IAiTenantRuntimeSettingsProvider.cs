namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation
{
    /// <summary>
    /// Provides tenant-specific runtime capacity and isolation settings.
    /// </summary>
    public interface IAiTenantRuntimeSettingsProvider
    {
        /// <summary>
        /// Gets runtime settings for the specified tenant.
        /// </summary>
        /// <param name="tenantId">The durable tenant identifier.</param>
        /// <param name="tenantGroupId">The optional tenant group identifier.</param>
        /// <returns>The runtime settings for the tenant.</returns>
        AiTenantRuntimeSettings GetSettings(
            string? tenantId,
            string? tenantGroupId);
    }
}