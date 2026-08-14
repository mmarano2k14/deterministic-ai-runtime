using System;
using System.Collections.Generic;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Represents one read-only inventory of recoverable work assigned across a failed Pod membership.
    /// </summary>
    public sealed record AiKubernetesRuntimePoolPodAssignedWorkInventory
    {
        /// <summary>
        /// Gets the immutable Pod failure identifier.
        /// </summary>
        public required string FailureId { get; init; }

        /// <summary>
        /// Gets the logical Runtime Pool identifier.
        /// </summary>
        public required string PoolId { get; init; }

        /// <summary>
        /// Gets the immutable Kubernetes Pod UID.
        /// </summary>
        public required string PodUid { get; init; }

        /// <summary>
        /// Gets the authoritative host incarnation identifier.
        /// </summary>
        public string HostId => this.PodUid;

        /// <summary>
        /// Gets when the complete Pod inventory was enumerated.
        /// </summary>
        public DateTimeOffset EnumeratedAtUtc { get; init; }

        /// <summary>
        /// Gets the exact per-runtime inventories in deterministic runtime identity order.
        /// </summary>
        public IReadOnlyList<AiRuntimePoolAssignedWorkInventory>
            RuntimeInventories { get; init; } =
            Array.Empty<AiRuntimePoolAssignedWorkInventory>();

        /// <summary>
        /// Gets all candidates in deterministic global recovery priority order.
        /// </summary>
        public IReadOnlyList<AiRuntimePoolAssignedWorkCandidate>
            Candidates { get; init; } =
            Array.Empty<AiRuntimePoolAssignedWorkCandidate>();
    }
}
