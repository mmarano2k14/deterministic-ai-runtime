using System.Collections.Generic;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes
{
    /// <summary>
    /// Represents the immutable pre-provisioning topology of one Kubernetes Runtime Pool Pod.
    /// </summary>
    /// <remarks>
    /// A Pod plan deliberately has no <c>HostId</c>. The authoritative host incarnation is the
    /// Kubernetes Pod UID and becomes available only after the Kubernetes API creates the Pod.
    /// </remarks>
    public sealed record AiKubernetesRuntimePoolPodPlan
    {
        /// <summary>
        /// Gets the logical Runtime Pool identifier.
        /// </summary>
        public required string PoolId { get; init; }

        /// <summary>
        /// Gets the immutable identity of this Pod creation request.
        /// </summary>
        public required string PodRequestId { get; init; }

        /// <summary>
        /// Gets the Kubernetes namespace.
        /// </summary>
        public required string Namespace { get; init; }

        /// <summary>
        /// Gets the DNS-label-safe Kubernetes Pod name.
        /// </summary>
        public required string PodName { get; init; }

        /// <summary>
        /// Gets the runtime provider name shared by the planned child runtimes.
        /// </summary>
        public required string ProviderName { get; init; }

        /// <summary>
        /// Gets the command transport name shared by the planned child runtimes.
        /// </summary>
        public required string TransportName { get; init; }

        /// <summary>
        /// Gets the stable pool transport port.
        /// </summary>
        public int StableTransportPort { get; init; }

        /// <summary>
        /// Gets the dedicated HTTP/1 Kubernetes readiness port.
        /// </summary>
        public int ReadinessPort { get; init; }

        /// <summary>
        /// Gets the independently identifiable child runtime plans.
        /// </summary>
        public required IReadOnlyList<AiKubernetesRuntimePoolRuntimeInstancePlan> RuntimeInstances
        {
            get;
            init;
        }
    }
}
