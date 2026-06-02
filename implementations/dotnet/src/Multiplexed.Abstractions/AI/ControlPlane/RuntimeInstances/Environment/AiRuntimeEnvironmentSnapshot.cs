namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Environment
{
    /// <summary>
    /// Represents provider-neutral runtime environment information.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Describes where the current runtime instance is running.
    /// - Avoids hardcoding Kubernetes, Docker, or host-specific concepts into
    ///   runtime instance registration options.
    /// - Can be produced by local, Docker, Kubernetes, or future environment providers.
    /// </remarks>
    public sealed class AiRuntimeEnvironmentSnapshot
    {
        /// <summary>
        /// Gets the runtime environment provider name.
        /// </summary>
        /// <remarks>
        /// Example values:
        /// - local
        /// - docker
        /// - kubernetes
        /// </remarks>
        public required string ProviderName { get; init; }

        /// <summary>
        /// Gets the provider-resolved runtime instance identifier, when available.
        /// </summary>
        public string? RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the host name where the runtime process is running, when available.
        /// </summary>
        public string? HostName { get; init; }

        /// <summary>
        /// Gets the local process identifier, when available.
        /// </summary>
        public int? ProcessId { get; init; }

        /// <summary>
        /// Gets provider-specific metadata.
        /// </summary>
        /// <remarks>
        /// Examples:
        /// - Kubernetes: namespace, pod, node, deployment.
        /// - Docker: container id, image, network.
        /// - Local: machine name, user, process path.
        /// </remarks>
        public IReadOnlyDictionary<string, string> ProviderMetadata { get; init; } =
            new Dictionary<string, string>();
    }
}