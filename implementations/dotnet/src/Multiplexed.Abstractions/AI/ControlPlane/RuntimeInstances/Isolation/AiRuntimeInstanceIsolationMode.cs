namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation
{
    /// <summary>
    /// Defines how runtime instances are isolated across tenants.
    /// </summary>
    public enum AiRuntimeInstanceIsolationMode
    {
        /// <summary>
        /// Runtime instances are shared across tenants according to shared capacity policy.
        /// This is the backward-compatible default.
        /// </summary>
        Shared = 0,

        /// <summary>
        /// Runtime instances are dedicated to a tenant or tenant group.
        /// </summary>
        Dedicated = 1,

        /// <summary>
        /// Runtime instances prefer dedicated capacity but may fallback to shared capacity
        /// when policy allows it.
        /// </summary>
        Hybrid = 2
    }
}