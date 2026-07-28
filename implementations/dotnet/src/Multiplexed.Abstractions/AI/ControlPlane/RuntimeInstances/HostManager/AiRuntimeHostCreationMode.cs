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
        /// Creates or scales one Kubernetes runtime host per Pod and Service.
        /// </summary>
        Kubernetes = 2,

        /// <summary>
        /// Attaches to an already existing runtime host.
        /// </summary>
        Attach = 3,

        /// <summary>
        /// Creates or scales an opt-in Kubernetes Runtime Pool Pod containing several
        /// independently registered runtime instances.
        /// </summary>
        /// <remarks>
        /// This mode is additive. It does not change the existing
        /// <see cref="Kubernetes"/> one-runtime-per-Pod behavior.
        /// </remarks>
        KubernetesPool = 4
    }
}
