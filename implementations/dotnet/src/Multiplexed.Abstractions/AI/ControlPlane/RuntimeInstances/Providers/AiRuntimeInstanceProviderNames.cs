namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Defines canonical runtime instance provider names shared across routing, dispatch, and scale-out.
    /// </summary>
    public static class AiRuntimeInstanceProviderNames
    {
        /// <summary>The local in-process runtime instance provider name.</summary>
        public const string Local = "local";

        /// <summary>
        /// Identifies the local in-process Runtime Pool provider.
        /// </summary>
        public const string LocalPool = "local-pool";

        /// <summary>The HTTP runtime instance provider name.</summary>
        public const string Http = "http";

        /// <summary>The gRPC runtime instance provider name.</summary>
        public const string Grpc = "grpc";
    }
}
