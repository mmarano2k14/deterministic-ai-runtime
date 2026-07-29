namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Defines the outcome of one deterministic Kubernetes Runtime Pool Pod creation
    /// request.
    /// </summary>
    public enum AiRuntimePoolPodCreationStatus
    {
        /// <summary>
        /// The request created and converged one fresh Runtime Pool Pod.
        /// </summary>
        Created = 0,

        /// <summary>
        /// The same provider-level request was already applied and no additional Pod
        /// mutation was performed.
        /// </summary>
        AlreadyApplied = 1,

        /// <summary>
        /// The Kubernetes Runtime Pool host strategy rejected the request or the created
        /// Pod did not converge to exact ready membership.
        /// </summary>
        Rejected = 2
    }
}
