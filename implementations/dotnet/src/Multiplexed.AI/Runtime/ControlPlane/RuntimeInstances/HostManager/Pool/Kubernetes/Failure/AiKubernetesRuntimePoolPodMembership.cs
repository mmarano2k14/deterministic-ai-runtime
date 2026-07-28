using System;
using System.Collections.Generic;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Represents authoritative registry-backed membership for one Kubernetes Runtime Pool Pod.
    /// </summary>
    public sealed record AiKubernetesRuntimePoolPodMembership
    {
        public required string PoolId { get; init; }

        public required string PodUid { get; init; }

        public string HostId => this.PodUid;

        public DateTimeOffset EnumeratedAtUtc { get; init; }

        public IReadOnlyList<AiKubernetesRuntimePoolPodMember> Members
        {
            get;
            init;
        } = Array.Empty<AiKubernetesRuntimePoolPodMember>();
    }
}
