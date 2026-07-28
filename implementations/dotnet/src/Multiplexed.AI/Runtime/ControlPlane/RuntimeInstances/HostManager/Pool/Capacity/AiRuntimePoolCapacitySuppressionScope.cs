namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity
{
    /// <summary>
    /// Defines the infrastructure authority represented by one capacity suppression.
    /// </summary>
    public enum AiRuntimePoolCapacitySuppressionScope
    {
        /// <summary>
        /// Suppresses one exact runtime and route incarnation.
        /// </summary>
        RuntimeInstanceRoute = 0,

        /// <summary>
        /// Suppresses one exact runtime because its complete host incarnation failed.
        /// </summary>
        HostMembership = 1
    }
}
