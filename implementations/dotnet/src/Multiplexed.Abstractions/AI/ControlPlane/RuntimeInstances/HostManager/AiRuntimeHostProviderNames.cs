namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager
{
    /// <summary>
    /// Provides well-known runtime host lifecycle provider names.
    /// </summary>
    public static class AiRuntimeHostProviderNames
    {
        /// <summary>
        /// Identifies a fixture-backed runtime host provider.
        /// </summary>
        public const string Fixture = "fixture";

        /// <summary>
        /// Identifies a local process-backed runtime host provider.
        /// </summary>
        public const string Process = "process";

        /// <summary>
        /// Identifies a Kubernetes-backed runtime host provider.
        /// </summary>
        public const string Kubernetes = "kubernetes";

        /// <summary>
        /// Identifies an externally attached runtime host provider.
        /// </summary>
        public const string Attach = "attach";
    }
}