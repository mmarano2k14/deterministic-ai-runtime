using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Represents rejected authority for exact Kubernetes Pod membership enumeration.
    /// </summary>
    public sealed class AiKubernetesRuntimePoolPodMembershipAuthorityException :
        InvalidOperationException
    {
        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiKubernetesRuntimePoolPodMembershipAuthorityException"/> class.
        /// </summary>
        /// <param name="poolId">The requested logical Runtime Pool identifier.</param>
        /// <param name="podUid">The requested immutable Kubernetes Pod UID.</param>
        /// <param name="reason">The typed authority rejection reason.</param>
        /// <param name="message">The diagnostic message.</param>
        public AiKubernetesRuntimePoolPodMembershipAuthorityException(
            string poolId,
            string podUid,
            AiKubernetesRuntimePoolPodMembershipAuthorityFailure reason,
            string message)
            : base(message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(podUid);

            this.PoolId = poolId.Trim();
            this.PodUid = podUid.Trim();
            this.Reason = reason;
        }

        /// <summary>
        /// Gets the requested logical Runtime Pool identifier.
        /// </summary>
        public string PoolId { get; }

        /// <summary>
        /// Gets the requested immutable Kubernetes Pod UID.
        /// </summary>
        public string PodUid { get; }

        /// <summary>
        /// Gets the authoritative host incarnation identifier.
        /// </summary>
        public string HostId => this.PodUid;

        /// <summary>
        /// Gets the typed authority rejection reason.
        /// </summary>
        public AiKubernetesRuntimePoolPodMembershipAuthorityFailure Reason { get; }
    }
}
