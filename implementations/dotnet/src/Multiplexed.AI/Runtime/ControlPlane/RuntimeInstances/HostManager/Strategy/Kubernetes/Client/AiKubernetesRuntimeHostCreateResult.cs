using System.Collections.Generic;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client
{
    /// <summary>
    /// Represents the result of creating Kubernetes resources for a runtime host.
    /// </summary>
    public sealed record AiKubernetesRuntimeHostCreateResult
    {
        /// <summary>
        /// Gets a value indicating whether the Kubernetes runtime host resources were created.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Gets the Kubernetes namespace where the runtime host resources were created.
        /// </summary>
        public string Namespace { get; init; } = string.Empty;

        /// <summary>
        /// Gets the Kubernetes pod name created for the runtime host.
        /// </summary>
        public string PodName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the optional Kubernetes service name created for the runtime host.
        /// </summary>
        public string? ServiceName { get; init; }

        /// <summary>
        /// Gets the structured failure reason when creation failed.
        /// </summary>
        public string? FailureReason { get; init; }

        /// <summary>
        /// Gets a value indicating whether the failure can be retried safely.
        /// </summary>
        public bool Retryable { get; init; }

        /// <summary>
        /// Gets Kubernetes creation metadata.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();

        /// <summary>
        /// Creates a successful Kubernetes runtime host creation result.
        /// </summary>
        /// <param name="namespaceName">The Kubernetes namespace.</param>
        /// <param name="podName">The Kubernetes pod name.</param>
        /// <param name="serviceName">The optional Kubernetes service name.</param>
        /// <param name="metadata">The optional metadata.</param>
        /// <returns>The successful creation result.</returns>
        public static AiKubernetesRuntimeHostCreateResult Created(
            string namespaceName,
            string podName,
            string? serviceName = null,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            return new AiKubernetesRuntimeHostCreateResult
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
        /// Creates a rejected Kubernetes runtime host creation result.
        /// </summary>
        /// <param name="namespaceName">The Kubernetes namespace.</param>
        /// <param name="podName">The Kubernetes pod name.</param>
        /// <param name="failureReason">The structured failure reason.</param>
        /// <param name="retryable">A value indicating whether the failure can be retried safely.</param>
        /// <param name="serviceName">The optional Kubernetes service name.</param>
        /// <param name="metadata">The optional metadata.</param>
        /// <returns>The rejected creation result.</returns>
        public static AiKubernetesRuntimeHostCreateResult Rejected(
            string namespaceName,
            string podName,
            string failureReason,
            bool retryable = false,
            string? serviceName = null,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            return new AiKubernetesRuntimeHostCreateResult
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