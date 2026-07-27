namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Defines whether one exact runtime route can accept new transport requests.
    /// </summary>
    public enum AiRuntimePoolRouteStatus
    {
        /// <summary>
        /// The route can forward requests to its exact runtime instance.
        /// </summary>
        Ready = 0,

        /// <summary>
        /// The route remains known but must reject new requests.
        /// </summary>
        Draining = 1
    }
}
