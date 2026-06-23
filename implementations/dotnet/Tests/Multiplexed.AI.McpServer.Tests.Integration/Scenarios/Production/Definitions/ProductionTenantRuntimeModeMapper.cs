using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions
{
    /// <summary>
    /// Maps production scenario tenant runtime modes to runtime isolation settings.
    /// </summary>
    /// <remarks>
    /// This mapper is the single scenario-framework location where the meaning of
    /// Dedicated, Shared, and Hybrid is translated into the runtime settings used
    /// by admission, scale-out, host creation, registration metadata, and visibility
    /// assertions.
    /// </remarks>
    public static class ProductionTenantRuntimeModeMapper
    {
        /// <summary>
        /// Resolves the runtime isolation mode for a production tenant runtime mode.
        /// </summary>
        /// <param name="runtimeMode">The production tenant runtime mode.</param>
        /// <returns>The runtime isolation mode.</returns>
        public static AiRuntimeInstanceIsolationMode ResolveIsolationMode(
            ProductionTenantRuntimeMode runtimeMode)
        {
            return runtimeMode switch
            {
                ProductionTenantRuntimeMode.Dedicated => AiRuntimeInstanceIsolationMode.Dedicated,
                ProductionTenantRuntimeMode.Shared => AiRuntimeInstanceIsolationMode.Shared,
                ProductionTenantRuntimeMode.Hybrid => AiRuntimeInstanceIsolationMode.Hybrid,
                _ => throw new ArgumentOutOfRangeException(nameof(runtimeMode), runtimeMode, "Unsupported production tenant runtime mode.")
            };
        }

        /// <summary>
        /// Resolves whether the tenant should prefer dedicated runtime capacity.
        /// </summary>
        /// <param name="runtimeMode">The production tenant runtime mode.</param>
        /// <returns><see langword="true"/> when dedicated runtime capacity should be preferred.</returns>
        public static bool ResolvePreferDedicatedCapacity(
            ProductionTenantRuntimeMode runtimeMode)
        {
            return runtimeMode switch
            {
                ProductionTenantRuntimeMode.Dedicated => true,
                ProductionTenantRuntimeMode.Shared => false,
                ProductionTenantRuntimeMode.Hybrid => true,
                _ => throw new ArgumentOutOfRangeException(nameof(runtimeMode), runtimeMode, "Unsupported production tenant runtime mode.")
            };
        }

        /// <summary>
        /// Resolves whether the tenant is allowed to fall back to shared runtime capacity.
        /// </summary>
        /// <param name="runtimeMode">The production tenant runtime mode.</param>
        /// <returns><see langword="true"/> when shared runtime fallback is allowed.</returns>
        public static bool ResolveAllowSharedFallback(
            ProductionTenantRuntimeMode runtimeMode)
        {
            return runtimeMode switch
            {
                ProductionTenantRuntimeMode.Dedicated => false,
                ProductionTenantRuntimeMode.Shared => true,
                ProductionTenantRuntimeMode.Hybrid => true,
                _ => throw new ArgumentOutOfRangeException(nameof(runtimeMode), runtimeMode, "Unsupported production tenant runtime mode.")
            };
        }
    }
}