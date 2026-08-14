using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Reports one fail-closed Kubernetes Runtime Pool Pod replacement violation.
    /// </summary>
    public sealed class AiKubernetesRuntimePoolPodReplacementException :
        InvalidOperationException
    {
        /// <summary>
        /// Initializes a new replacement exception.
        /// </summary>
        public AiKubernetesRuntimePoolPodReplacementException(
            string failureId,
            string poolId,
            string failedPodUid,
            AiKubernetesRuntimePoolPodReplacementFailure reason,
            string message,
            bool retryable = false,
            Exception? innerException = null)
            : base(message, innerException)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(failureId);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedPodUid);

            this.FailureId = failureId.Trim();
            this.PoolId = poolId.Trim();
            this.FailedPodUid = failedPodUid.Trim();
            this.Reason = reason;
            this.Retryable = retryable;
        }

        /// <summary>
        /// Gets the immutable failed-Pod observation identifier.
        /// </summary>
        public string FailureId { get; }

        /// <summary>
        /// Gets the logical Runtime Pool identifier.
        /// </summary>
        public string PoolId { get; }

        /// <summary>
        /// Gets the immutable failed Kubernetes Pod UID.
        /// </summary>
        public string FailedPodUid { get; }

        /// <summary>
        /// Gets the typed replacement failure reason.
        /// </summary>
        public AiKubernetesRuntimePoolPodReplacementFailure Reason { get; }

        /// <summary>
        /// Gets whether the failed operation may be retried safely.
        /// </summary>
        public bool Retryable { get; }
    }
}
