using System;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Represents one deterministic Pod failure recovery coordination result.
    /// </summary>
    public sealed record AiKubernetesRuntimePoolPodFailureRecoveryResult
    {
        public required string FailureId { get; init; }

        public required string PoolId { get; init; }

        public required string FailedPodUid { get; init; }

        public AiRuntimePoolRecoveryClaimAcquisitionStatus Status { get; init; }

        public required AiRuntimePoolFailureObservation Failure { get; init; }

        public required AiKubernetesRuntimePoolPodCapacitySuppression
            Suppression { get; init; }

        public required AiKubernetesRuntimePoolPodClaimedAssignedWork
            ClaimedAssignedWork { get; init; }

        public AiKubernetesRuntimePoolPodReplacement? Replacement { get; init; }

        public AiKubernetesRuntimePoolPodClaimedRecoveryExecutionResult?
            Recovery { get; init; }

        public DateTimeOffset CompletedAtUtc { get; init; }
    }
}
