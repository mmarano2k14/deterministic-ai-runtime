using System.Collections.Generic;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes
{
    /// <summary>
    /// Represents Kubernetes metadata generated for a runtime host pod.
    /// </summary>
    public sealed record AiKubernetesRuntimePodMetadata
    {
        /// <summary>
        /// Gets the Kubernetes pod name.
        /// </summary>
        public string PodName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the Kubernetes namespace.
        /// </summary>
        public string Namespace { get; init; } = string.Empty;

        /// <summary>
        /// Gets the Kubernetes labels.
        /// </summary>
        public IReadOnlyDictionary<string, string> Labels { get; init; } =
            new Dictionary<string, string>();

        /// <summary>
        /// Gets the Kubernetes annotations.
        /// </summary>
        public IReadOnlyDictionary<string, string> Annotations { get; init; } =
            new Dictionary<string, string>();
    }
}