namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions
{
    /// <summary>
    /// Defines the runtime isolation mode expected for a tenant in a production scenario.
    /// </summary>
    /// <remarks>
    /// This mode describes how the tenant is allowed to consume runtime capacity.
    /// It is scenario-level intent and must later be translated to the runtime
    /// tenant settings used by admission, scale-out, and runtime visibility logic.
    /// </remarks>
    public enum ProductionTenantRuntimeMode
    {
        /// <summary>
        /// The tenant must use only runtime instances dedicated to that tenant.
        /// </summary>
        /// <remarks>
        /// Dedicated tenants must never silently fall back to shared runtime
        /// capacity. This is the strongest isolation mode.
        /// </remarks>
        Dedicated = 0,

        /// <summary>
        /// The tenant uses shared runtime capacity.
        /// </summary>
        /// <remarks>
        /// Shared tenants may execute on shared runtime pools according to the
        /// configured tenant and provider policies.
        /// </remarks>
        Shared = 1,

        /// <summary>
        /// The tenant prefers dedicated capacity but may use shared capacity only when explicitly allowed.
        /// </summary>
        /// <remarks>
        /// Hybrid mode must be explicit and observable. It must never degrade
        /// silently without policy and scenario assertions being aware of it.
        /// </remarks>
        Hybrid = 2
    }
}