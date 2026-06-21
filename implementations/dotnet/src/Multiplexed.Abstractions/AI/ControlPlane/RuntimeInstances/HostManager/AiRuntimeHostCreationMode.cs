namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager
{
    /// <summary>
    /// Defines how a runtime host is physically materialized for a scale-out request.
    /// </summary>
    public enum AiRuntimeHostCreationMode
    {
        /// <summary>
        /// Uses an integration-test fixture or test host. Fast path for tests only.
        /// </summary>
        Fixture = 0,

        /// <summary>
        /// Starts a real external runtime host process.
        /// </summary>
        Process = 1,

        /// <summary>
        /// Creates or scales a Kubernetes runtime host.
        /// </summary>
        Kubernetes = 2,

        /// <summary>
        /// Attaches to an already existing runtime host.
        /// </summary>
        Attach = 3
    }
}