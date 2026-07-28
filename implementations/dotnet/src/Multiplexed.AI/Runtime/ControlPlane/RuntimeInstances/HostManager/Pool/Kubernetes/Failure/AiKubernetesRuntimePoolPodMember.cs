using System;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Represents one independently registered runtime owned by one Kubernetes Pod incarnation.
    /// </summary>
    public sealed record AiKubernetesRuntimePoolPodMember
    {
        public required string PoolId { get; init; }

        public required string PodUid { get; init; }

        public string HostId => this.PodUid;

        public required string RuntimeInstanceId { get; init; }

        public string? RuntimeId { get; init; }

        public AiRuntimeInstanceStatus Status { get; init; }

        public bool CanAcceptRun { get; init; }

        public DateTimeOffset RegisteredAtUtc { get; init; }

        public DateTimeOffset LastHeartbeatAtUtc { get; init; }
    }
}
