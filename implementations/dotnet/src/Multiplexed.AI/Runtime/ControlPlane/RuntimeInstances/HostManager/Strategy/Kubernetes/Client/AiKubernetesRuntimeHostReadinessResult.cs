using System.Collections.Generic;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client
{
    /// <summary>
    /// Represents the result of waiting for Kubernetes-level runtime host readiness.
    /// </summary>
    /// <remarks>
    /// Kubernetes host readiness only means that Kubernetes considers the host resources ready.
    /// Runtime dispatch readiness must still be validated through runtime registry, capacity, and tenant visibility.
    /// </remarks>
    public sealed record AiKubernetesRuntimeHostReadinessResult
    {
        /// <summary>
        /// Gets a value indicating whether the Kubernetes runtime host became ready.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Gets the Kubernetes namespace that contains the runtime host.
        /// </summary>
        public string Namespace { get; init; } = string.Empty;

        /// <summary>
        /// Gets the Kubernetes pod name checked for readiness.
        /// </summary>
        public string PodName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the optional Kubernetes service name checked for readiness.
        /// </summary>
        public string? ServiceName { get; init; }

        /// <summary>
        /// Gets the structured failure reason when readiness failed.
        /// </summary>
        public string? FailureReason { get; init; }

        /// <summary>
        /// Gets a value indicating whether the readiness operation timed out.
        /// </summary>
        public bool TimedOut { get; init; }

        /// <summary>
        /// Gets a value indicating whether the failure can be retried safely.
        /// </summary>
        public bool Retryable { get; init; }

        /// <summary>
        /// Gets Kubernetes readiness metadata.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();

        /// <summary>
        /// Creates a successful Kubernetes runtime host readiness result.
        /// </summary>
        /// <param name="namespaceName">The Kubernetes namespace.</param>
        /// <param name="podName">The Kubernetes pod name.</param>
        /// <param name="serviceName">The optional Kubernetes service name.</param>
        /// <param name="metadata">The optional metadata.</param>
        /// <returns>The successful readiness result.</returns>
        public static AiKubernetesRuntimeHostReadinessResult Ready(
            string namespaceName,
            string podName,
            string? serviceName = null,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            return new AiKubernetesRuntimeHostReadinessResult
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
        /// Creates a failed Kubernetes runtime host readiness result.
        /// </summary>
        /// <param name="namespaceName">The Kubernetes namespace.</param>
        /// <param name="podName">The Kubernetes pod name.</param>
        /// <param name="failureReason">The structured failure reason.</param>
        /// <param name="timedOut">A value indicating whether the readiness operation timed out.</param>
        /// <param name="retryable">A value indicating whether the failure can be retried safely.</param>
        /// <param name="serviceName">The optional Kubernetes service name.</param>
        /// <param name="metadata">The optional metadata.</param>
        /// <returns>The failed readiness result.</returns>
        public static AiKubernetesRuntimeHostReadinessResult Failed(
            string namespaceName,
            string podName,
            string failureReason,
            bool timedOut = false,
            bool retryable = false,
            string? serviceName = null,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            return new AiKubernetesRuntimeHostReadinessResult
            {
                Success = false,
                Namespace = namespaceName,
                PodName = podName,
                ServiceName = serviceName,
                FailureReason = failureReason,
                TimedOut = timedOut,
                Retryable = retryable,
                Metadata = metadata ?? new Dictionary<string, string>()
            };
        }
    }
}