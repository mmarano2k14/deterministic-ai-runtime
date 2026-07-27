namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Grpc
{
    /// <summary>
    /// Creates an owned gRPC client for one exact child endpoint.
    /// </summary>
    public interface IAiRuntimePoolGrpcClientFactory
    {
        /// <summary>
        /// Creates one client for the exact absolute child endpoint.
        /// </summary>
        /// <param name="transportEndpoint">The exact child transport endpoint.</param>
        /// <returns>The owned gRPC client.</returns>
        IAiRuntimePoolGrpcClient Create(
            string transportEndpoint);
    }
}
