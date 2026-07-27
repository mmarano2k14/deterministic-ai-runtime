namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity
{
    /// <summary>
    /// Combines exact capacity suppression writes and reads through one authoritative registry.
    /// </summary>
    public interface IAiRuntimePoolCapacitySafetyRegistry :
        IAiRuntimePoolCapacitySafetyWriter,
        IAiRuntimePoolCapacitySafetyReader
    {
    }
}
