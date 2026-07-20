namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes
{
    /// <summary>
    /// Defines Kubernetes container image pull policies.
    /// </summary>
    public enum AiKubernetesImagePullPolicy
    {
        /// <summary>
        /// Pulls the image only when it is not already present on the node.
        /// </summary>
        IfNotPresent = 0,

        /// <summary>
        /// Always pulls the image before starting the container.
        /// </summary>
        Always = 1,

        /// <summary>
        /// Never pulls the image and expects it to already exist on the node.
        /// </summary>
        Never = 2
    }
}