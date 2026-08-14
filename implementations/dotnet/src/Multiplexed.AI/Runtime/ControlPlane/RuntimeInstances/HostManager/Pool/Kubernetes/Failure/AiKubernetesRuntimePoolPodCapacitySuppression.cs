using System;
using System.Collections.Generic;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Represents one immutable atomic Pod-wide capacity suppression result.
    /// </summary>
    public sealed record AiKubernetesRuntimePoolPodCapacitySuppression
    {
        /// <summary>
        /// Gets the immutable failure observation identifier.
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
        /// Gets when exact Pod membership was enumerated.
        /// </summary>
        public DateTimeOffset MembershipEnumeratedAtUtc { get; init; }

        /// <summary>
        /// Gets the shared atomic suppression timestamp.
        /// </summary>
        public DateTimeOffset SuppressedAtUtc { get; init; }

        /// <summary>
        /// Gets every exact child capacity suppression in deterministic identity order.
        /// </summary>
        public IReadOnlyList<AiRuntimePoolCapacitySuppression> Suppressions
        {
            get;
            init;
        } = Array.Empty<AiRuntimePoolCapacitySuppression>();
    }
}
