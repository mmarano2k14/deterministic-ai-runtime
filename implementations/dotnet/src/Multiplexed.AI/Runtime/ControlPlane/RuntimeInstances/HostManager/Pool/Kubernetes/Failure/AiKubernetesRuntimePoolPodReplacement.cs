using System;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Represents one ready replacement Pod with fresh exact shared-registry runtime identities.
    /// </summary>
    public sealed record AiKubernetesRuntimePoolPodReplacement
    {
        /// <summary>
        /// Gets the immutable failed-Pod observation identifier.
        /// </summary>
        public required string FailureId { get; init; }

        /// <summary>
        /// Gets the logical Runtime Pool identifier.
        /// </summary>
        public required string PoolId { get; init; }

        /// <summary>
        /// Gets the immutable UID of the failed Kubernetes Pod.
        /// </summary>
        public required string FailedPodUid { get; init; }

        /// <summary>
        /// Gets the immutable UID of the ready replacement Kubernetes Pod.
        /// </summary>
        public required string ReplacementPodUid { get; init; }

        /// <summary>
        /// Gets the deterministic replacement host request identity.
        /// </summary>
        public required string ReplacementRequestId { get; init; }

        /// <summary>
        /// Gets the deterministic fresh primary runtime identity selected for Pod creation.
        /// </summary>
        public required string PrimaryRuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the active recovery lease incarnation that authorized this replacement generation.
        /// </summary>
        public required string RecoveryLeaseId { get; init; }

        /// <summary>
        /// Gets the successful result returned by the existing KubernetesPool host strategy.
        /// </summary>
        public required AiRuntimeHostStartResult HostStartResult { get; init; }

        /// <summary>
        /// Gets the exact ready shared-registry membership registered by the replacement Pod UID.
        /// </summary>
        public required AiKubernetesRuntimePoolPodMembership Membership { get; init; }

        /// <summary>
        /// Gets when fresh replacement membership was validated.
        /// </summary>
        public DateTimeOffset ReadyAtUtc { get; init; }
    }
}
