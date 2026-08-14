using System;
using System.Collections.Generic;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Execution;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Represents deterministic transition outcomes for one exact failed Pod membership claim.
    /// </summary>
    public sealed record AiKubernetesRuntimePoolPodClaimedRecoveryExecutionResult
    {
        public required string ClaimId { get; init; }

        public required string FailureId { get; init; }

        public required string PoolId { get; init; }

        public required string PodUid { get; init; }

        public int MemberCount { get; init; }

        public int CandidateCount { get; init; }

        public int AcceptedCount { get; init; }

        public int ChangedCount { get; init; }

        public int RejectedCount { get; init; }

        public DateTimeOffset CompletedAtUtc { get; init; }

        public IReadOnlyList<AiRuntimePoolRecoveryCandidateOutcome>
            Outcomes { get; init; } =
            Array.Empty<AiRuntimePoolRecoveryCandidateOutcome>();
    }
}
