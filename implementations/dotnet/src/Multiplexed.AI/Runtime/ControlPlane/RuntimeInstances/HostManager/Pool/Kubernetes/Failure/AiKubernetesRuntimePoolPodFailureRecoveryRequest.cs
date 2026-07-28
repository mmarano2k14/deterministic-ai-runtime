using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Requests deterministic recovery after one exact Kubernetes Runtime Pool Pod disappears.
    /// </summary>
    public sealed record AiKubernetesRuntimePoolPodFailureRecoveryRequest
    {
        public required string FailureId { get; init; }

        public required string PoolId { get; init; }

        public required string PodUid { get; init; }

        public required string ClaimedBy { get; init; }

        public string? FailureMessage { get; init; }

        public required AiRuntimeHostStartRequest HostStartTemplate { get; init; }
    }
}
