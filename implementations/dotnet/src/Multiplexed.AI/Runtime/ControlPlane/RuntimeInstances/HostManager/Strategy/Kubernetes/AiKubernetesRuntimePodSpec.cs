using System.Collections.Generic;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes
{
    /// <summary>
    /// Represents the runtime-owned description of a Kubernetes pod used to host a runtime instance.
    /// </summary>
    /// <remarks>
    /// This model is intentionally independent from Kubernetes client types.
    /// It allows the runtime host lifecycle layer to be tested without requiring a Kubernetes cluster or SDK model.
    /// </remarks>
    public sealed record AiKubernetesRuntimePodSpec
    {
        /// <summary>
        /// Gets the Kubernetes namespace.
        /// </summary>
        public string Namespace { get; init; } = string.Empty;

        /// <summary>
        /// Gets the Kubernetes pod name.
        /// </summary>
        public string PodName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the runtime container image.
        /// </summary>
        public string RuntimeImage { get; init; } = string.Empty;

        /// <summary>
        /// Gets the runtime container name.
        /// </summary>
        public string ContainerName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the runtime container port.
        /// </summary>
        public int ContainerPort { get; init; }

        /// <summary>
        /// Gets the optional Kubernetes service account name.
        /// </summary>
        public string? ServiceAccountName { get; init; }

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

        /// <summary>
        /// Gets environment variables injected into the runtime container.
        /// </summary>
        public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
            new Dictionary<string, string>();

        /// <summary>
        /// Gets or sets the Kubernetes container image pull policy.
        /// </summary>
        public AiKubernetesImagePullPolicy ImagePullPolicy { get; init; } =
            AiKubernetesImagePullPolicy.IfNotPresent;
    }
}