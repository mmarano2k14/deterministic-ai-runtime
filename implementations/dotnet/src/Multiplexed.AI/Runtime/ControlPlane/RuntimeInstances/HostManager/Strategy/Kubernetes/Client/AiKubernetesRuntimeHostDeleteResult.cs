using System.Collections.Generic;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client
{
    /// <summary>
    /// Represents the result of deleting Kubernetes resources associated with a runtime host.
    /// </summary>
    public sealed record AiKubernetesRuntimeHostDeleteResult
    {
        /// <summary>
        /// Gets a value indicating whether the Kubernetes runtime host resources were deleted.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Gets the Kubernetes namespace that contained the runtime host resources.
        /// </summary>
        public string Namespace { get; init; } = string.Empty;

        /// <summary>
        /// Gets the Kubernetes pod name associated with the runtime host.
        /// </summary>
        public string PodName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the optional Kubernetes service name associated with the runtime host.
        /// </summary>
        public string? ServiceName { get; init; }

        /// <summary>
        /// Gets the structured failure reason when deletion failed.
        /// </summary>
        public string? FailureReason { get; init; }

        /// <summary>
        /// Gets a value indicating whether the failure can be retried safely.
        /// </summary>
        public bool Retryable { get; init; }

        /// <summary>
        /// Gets Kubernetes deletion metadata.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();

        /// <summary>
        /// Creates a successful Kubernetes runtime host deletion result.
        /// </summary>
        /// <param name="namespaceName">The Kubernetes namespace.</param>
        /// <param name="podName">The Kubernetes pod name.</param>
        /// <param name="serviceName">The optional Kubernetes service name.</param>
        /// <param name="metadata">The optional metadata.</param>
        /// <returns>The successful deletion result.</returns>
        public static AiKubernetesRuntimeHostDeleteResult Deleted(
            string namespaceName,
            string podName,
            string? serviceName = null,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            return new AiKubernetesRuntimeHostDeleteResult
            {
                Success = true,
                Namespace = namespaceName,
                PodName = podName,
                ServiceName = serviceName,
                Retryable = false,
                Metadata = metadata ?? new Dictionary<string, string>()
            };
        }

        /// <summary>
        /// Creates a failed Kubernetes runtime host deletion result.
        /// </summary>
        /// <param name="namespaceName">The Kubernetes namespace.</param>
        /// <param name="podName">The Kubernetes pod name.</param>
        /// <param name="failureReason">The structured failure reason.</param>
        /// <param name="retryable">A value indicating whether the failure can be retried safely.</param>
        /// <param name="serviceName">The optional Kubernetes service name.</param>
        /// <param name="metadata">The optional metadata.</param>
        /// <returns>The failed deletion result.</returns>
        public static AiKubernetesRuntimeHostDeleteResult Failed(
            string namespaceName,
            string podName,
            string failureReason,
            bool retryable = false,
            string? serviceName = null,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            return new AiKubernetesRuntimeHostDeleteResult
            {
                Success = false,
                Namespace = namespaceName,
                PodName = podName,
                ServiceName = serviceName,
                FailureReason = failureReason,
                Retryable = retryable,
                Metadata = metadata ?? new Dictionary<string, string>()
            };
        }
    }
}