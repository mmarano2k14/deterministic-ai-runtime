using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes
{
    /// <summary>
    /// Maps a created Kubernetes Runtime Pool Pod to its authoritative host identity.
    /// </summary>
    public static class AiKubernetesRuntimePoolHostIdentityFactory
    {
        /// <summary>
        /// Creates the host identity after Kubernetes returns the created Pod UID.
        /// </summary>
        /// <param name="plan">The immutable pre-provisioning Pod plan.</param>
        /// <param name="podUid">The Kubernetes Pod UID.</param>
        /// <returns>The authoritative Runtime Pool host identity.</returns>
        public static AiKubernetesRuntimePoolHostIdentity Create(
            AiKubernetesRuntimePoolPodPlan plan,
            string podUid)
        {
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentException.ThrowIfNullOrWhiteSpace(podUid);
            ArgumentException.ThrowIfNullOrWhiteSpace(plan.PoolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(plan.PodRequestId);
            ArgumentException.ThrowIfNullOrWhiteSpace(plan.Namespace);
            ArgumentException.ThrowIfNullOrWhiteSpace(plan.PodName);

            return new AiKubernetesRuntimePoolHostIdentity
            {
                PoolId = plan.PoolId,
                HostId = podUid,
                PodRequestId = plan.PodRequestId,
                Namespace = plan.Namespace,
                PodName = plan.PodName
            };
        }
    }
}
