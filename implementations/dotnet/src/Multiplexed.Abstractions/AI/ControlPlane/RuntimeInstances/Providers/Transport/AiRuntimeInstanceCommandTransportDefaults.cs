namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport
{
    /// <summary>
    /// Defines canonical default values used by runtime instance command transports.
    /// </summary>
    public static class AiRuntimeInstanceCommandTransportDefaults
    {
        /// <summary>
        /// Gets the default routing header used to select an exact runtime instance behind a shared gateway.
        /// </summary>
        public const string DefaultGatewayRoutingHeaderName =
            "x-ai-runtime-instance-id";

        /// <summary>
        /// Gets the default HTTP endpoint path used to dispatch commands to a runtime instance.
        /// </summary>
        public const string DefaultHttpCommandEndpointPath =
            "/runtime-instance/commands";

        /// <summary>
        /// Gets the default loopback endpoint base used by process-host scale-out fallbacks.
        /// </summary>
        public const string DefaultLoopbackEndpointBase =
            "http://localhost";
    }
}
