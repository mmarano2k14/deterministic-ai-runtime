namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client
{
    /// <summary>
    /// Defines the Kubernetes runtime host client implementation mode.
    /// </summary>
    public enum AiKubernetesRuntimeHostClientMode
    {
        /// <summary>
        /// Uses an in-memory fake Kubernetes runtime host client.
        /// </summary>
        Fake = 0,

        /// <summary>
        /// Uses the Kubernetes .NET SDK runtime host client.
        /// </summary>
        KubernetesSdk = 1
    }
}