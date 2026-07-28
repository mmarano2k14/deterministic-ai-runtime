using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Represents rejected authority for Pod-wide assigned-work enumeration.
    /// </summary>
    public sealed class AiKubernetesRuntimePoolPodAssignedWorkException :
        InvalidOperationException
    {
        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiKubernetesRuntimePoolPodAssignedWorkException"/> class.
        /// </summary>
        public AiKubernetesRuntimePoolPodAssignedWorkException(
            string failureId,
            string poolId,
            string podUid,
            AiKubernetesRuntimePoolPodAssignedWorkFailure reason,
            string message)
            : base(message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(failureId);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(podUid);

            this.FailureId = failureId.Trim();
            this.PoolId = poolId.Trim();
            this.PodUid = podUid.Trim();
            this.Reason = reason;
        }

        /// <summary>
        /// Gets the requested immutable failure identifier.
        /// </summary>
        public string FailureId { get; }

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
        /// Gets the typed rejection reason.
        /// </summary>
        public AiKubernetesRuntimePoolPodAssignedWorkFailure Reason
        {
            get;
        }
    }
}
