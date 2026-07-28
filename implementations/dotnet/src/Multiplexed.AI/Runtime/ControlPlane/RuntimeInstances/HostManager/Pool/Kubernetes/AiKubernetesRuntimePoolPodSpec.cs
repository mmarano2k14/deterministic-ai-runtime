using System;
using System.Collections.Generic;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes
{
    /// <summary>
    /// Represents the runtime-owned Kubernetes Pod specification for one Runtime Pool host.
    /// </summary>
    /// <remarks>
    /// This model is independent from Kubernetes SDK types and does not create resources.
    /// The existing one-runtime-per-Pod <c>AiKubernetesRuntimePodSpec</c> remains unchanged.
    /// </remarks>
    public sealed record AiKubernetesRuntimePoolPodSpec
    {
        /// <summary>
        /// Gets the logical Runtime Pool identifier.
        /// </summary>
        public required string PoolId { get; init; }

        /// <summary>
        /// Gets the immutable Pod creation request identity.
        /// </summary>
        public required string PodRequestId { get; init; }

        /// <summary>
        /// Gets the Kubernetes namespace.
        /// </summary>
        public required string Namespace { get; init; }

        /// <summary>
        /// Gets the Kubernetes Pod name.
        /// </summary>
        public required string PodName { get; init; }

        /// <summary>
        /// Gets the runtime pool container image.
        /// </summary>
        public required string RuntimeImage { get; init; }

        /// <summary>
        /// Gets the runtime pool container name.
        /// </summary>
        public required string ContainerName { get; init; }

        /// <summary>
        /// Gets the optional Kubernetes service account name.
        /// </summary>
        public string? ServiceAccountName { get; init; }

        /// <summary>
        /// Gets the image pull policy.
        /// </summary>
        public AiKubernetesImagePullPolicy ImagePullPolicy { get; init; } =
            AiKubernetesImagePullPolicy.IfNotPresent;

        /// <summary>
        /// Gets the required and caller-supplied diagnostic labels.
        /// </summary>
        public required IReadOnlyDictionary<string, string> Labels { get; init; }

        /// <summary>
        /// Gets the required and caller-supplied diagnostic annotations.
        /// </summary>
        public required IReadOnlyDictionary<string, string> Annotations { get; init; }

        /// <summary>
        /// Gets the stable pool and internal child container ports.
        /// </summary>
        public required IReadOnlyList<AiKubernetesRuntimePoolContainerPort> Ports
        {
            get;
            init;
        }

        /// <summary>
        /// Gets the strongly typed in-Pod Runtime Pool bootstrap contract.
        /// </summary>
        public required AiKubernetesRuntimePoolBootstrapSpec Bootstrap { get; init; }

        /// <summary>
        /// Gets the strongly typed ASP.NET Core command-line settings passed to the parent
        /// Runtime Pool container.
        /// </summary>
        public IReadOnlyList<string> ContainerArguments { get; init; } =
            Array.Empty<string>();
    }
}
