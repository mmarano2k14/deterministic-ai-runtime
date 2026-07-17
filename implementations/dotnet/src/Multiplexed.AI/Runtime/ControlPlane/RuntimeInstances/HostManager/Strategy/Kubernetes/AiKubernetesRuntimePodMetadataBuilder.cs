using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Kubernetes;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes
{
    /// <summary>
    /// Builds Kubernetes labels and annotations for runtime host pods.
    /// </summary>
    /// <remarks>
    /// Kubernetes metadata is diagnostic and lifecycle-oriented.
    /// Runtime tenant isolation must still be enforced by execution context, registry, capacity, and admission.
    /// </remarks>
    public sealed class AiKubernetesRuntimePodMetadataBuilder
    {
        private const int MaxLabelValueLength = 63;
        private const int HashLength = 12;

        private static readonly Regex InvalidDnsLabelCharacters =
            new("[^a-z0-9.-]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly AiKubernetesRuntimeHostOptions options;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiKubernetesRuntimePodMetadataBuilder"/> class.
        /// </summary>
        /// <param name="options">The Kubernetes runtime host options.</param>
        public AiKubernetesRuntimePodMetadataBuilder(
            AiKubernetesRuntimeHostOptions options)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        /// Builds Kubernetes metadata for a runtime host start request.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <returns>The generated Kubernetes pod metadata.</returns>
        public AiKubernetesRuntimePodMetadata Build(
            AiRuntimeHostStartRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            var podName =
                BuildPodName(
                    this.options.PodNamePrefix,
                    request.RuntimeInstanceId);

            var labels =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["app.kubernetes.io/name"] = "multiplexed-ai-runtime",
                    ["app.kubernetes.io/component"] = "runtime-instance",
                    ["multiplexed.ai/control-plane-id"] = SanitizeLabelValue(request.ControlPlaneId),
                    ["multiplexed.ai/runtime-instance-id"] = SanitizeLabelValue(request.RuntimeInstanceId),
                    ["multiplexed.ai/provider"] = SanitizeLabelValue(request.ProviderName),
                    ["multiplexed.ai/transport"] = SanitizeLabelValue(request.TransportName ?? request.ProviderName),
                    ["multiplexed.ai/host-provider"] = AiRuntimeHostProviderNames.Kubernetes
                };

            var annotations =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimeHostMetadataKeys.HostProvider] = AiRuntimeHostProviderNames.Kubernetes,
                    [AiRuntimeHostMetadataKeys.HostCreationMode] = AiRuntimeHostCreationMode.Kubernetes.ToString(),
                    [AiRuntimeHostMetadataKeys.HostCreationStrategy] = nameof(KubernetesAiRuntimeHostCreationStrategy),
                    [AiKubernetesRuntimeHostMetadataKeys.Namespace] = this.options.Namespace,
                    [AiKubernetesRuntimeHostMetadataKeys.PodName] = podName,
                    [AiKubernetesRuntimeHostMetadataKeys.ContainerName] = this.options.ContainerName,
                    [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = request.ProviderName
                };

            if (!string.IsNullOrWhiteSpace(request.TransportName))
            {
                annotations["transport.name"] = request.TransportName;
            }

            if (!string.IsNullOrWhiteSpace(request.TransportEndpoint))
            {
                annotations["transport.endpoint"] = request.TransportEndpoint;
            }

            if (!string.IsNullOrWhiteSpace(request.ExecutionContextSnapshot.TenantId))
            {
                labels["multiplexed.ai/tenant-id"] = SanitizeLabelValue(request.ExecutionContextSnapshot.TenantId);
                annotations["tenant.id"] = request.ExecutionContextSnapshot.TenantId;
            }

            if (!string.IsNullOrWhiteSpace(request.ExecutionContextSnapshot.TenantGroupId))
            {
                labels["multiplexed.ai/tenant-group-id"] = SanitizeLabelValue(request.ExecutionContextSnapshot.TenantGroupId);
                annotations["tenant.groupId"] = request.ExecutionContextSnapshot.TenantGroupId;
            }

            foreach (var label in this.options.Labels)
            {
                labels[label.Key] = SanitizeLabelValue(label.Value);
            }

            foreach (var annotation in this.options.Annotations)
            {
                annotations[annotation.Key] = annotation.Value;
            }

            return new AiKubernetesRuntimePodMetadata
            {
                PodName = podName,
                Namespace = this.options.Namespace,
                Labels = labels,
                Annotations = annotations
            };
        }

        /// <summary>
        /// Builds a Kubernetes-safe pod name.
        /// </summary>
        /// <param name="prefix">The pod name prefix.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <returns>The generated pod name.</returns>
        private static string BuildPodName(
            string prefix,
            string runtimeInstanceId)
        {
            var normalizedPrefix =
                SanitizeDnsLabel(
                    string.IsNullOrWhiteSpace(prefix) ? "ai-runtime" : prefix);

            var runtimeSuffix =
                ResolveRuntimeInstanceShortSuffix(runtimeInstanceId);

            var hash =
                ComputeStableHash(runtimeInstanceId);

            var value =
                SanitizeDnsLabel(
                    $"{normalizedPrefix}-{runtimeSuffix}-{hash}");

            if (value.Length <= MaxLabelValueLength)
            {
                return value;
            }

            var hashSuffix =
                $"-{hash}";

            return value[..(MaxLabelValueLength - hashSuffix.Length)]
                .TrimEnd('-', '.') + hashSuffix;
        }

        /// <summary>
        /// Resolves a short human-readable suffix from a runtime instance id.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <returns>The short suffix.</returns>
        private static string ResolveRuntimeInstanceShortSuffix(
            string runtimeInstanceId)
        {
            var sanitized =
                SanitizeDnsLabel(runtimeInstanceId);

            var lastColonSegment =
                sanitized
                    .Split(':', StringSplitOptions.RemoveEmptyEntries)
                    .LastOrDefault() ?? sanitized;

            var parts =
                lastColonSegment.Split(
                    '-',
                    StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2)
            {
                return SanitizeDnsLabel(
                    string.Join(
                        "-",
                        parts.TakeLast(2)));
            }

            return SanitizeDnsLabel(lastColonSegment);
        }

        /// <summary>
        /// Computes a stable lowercase hexadecimal hash from a value.
        /// </summary>
        /// <param name="value">The source value.</param>
        /// <returns>The stable hash.</returns>
        private static string ComputeStableHash(
            string value)
        {
            var bytes =
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(value ?? string.Empty));

            return Convert
                .ToHexString(bytes)
                .ToLower(CultureInfo.InvariantCulture)[..HashLength];
        }

        /// <summary>
        /// Sanitizes a Kubernetes label value.
        /// </summary>
        /// <param name="value">The label value.</param>
        /// <returns>The sanitized label value.</returns>
        private static string SanitizeLabelValue(
            string? value)
        {
            var sanitized =
                SanitizeDnsLabel(value);

            if (sanitized.Length <= MaxLabelValueLength)
            {
                return sanitized;
            }

            /*
             * Kubernetes label values are limited to 63 characters. Truncating a
             * runtime identity at the boundary is unsafe because runtime ids created
             * by one control plane share a long prefix and normally differ only near
             * the end. A Service selector built from the truncated value can therefore
             * select several runtime pods.
             *
             * Preserve a readable prefix and append a stable hash of the complete
             * source value so the pod label and its Service selector remain both
             * Kubernetes-safe and runtime-specific.
             */
            var hash =
                ComputeStableHash(
                    value ?? string.Empty);

            var hashSuffix =
                $"-{hash}";

            var readablePrefix =
                sanitized[..(MaxLabelValueLength - hashSuffix.Length)]
                    .TrimEnd('-', '.');

            return $"{readablePrefix}{hashSuffix}";
        }

        /// <summary>
        /// Sanitizes a value using DNS-label-compatible characters.
        /// </summary>
        /// <param name="value">The value to sanitize.</param>
        /// <returns>The sanitized value.</returns>
        private static string SanitizeDnsLabel(
            string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unknown";
            }

            var lower =
                value.Trim().ToLower(CultureInfo.InvariantCulture);

            var sanitized =
                InvalidDnsLabelCharacters.Replace(lower, "-").Trim('-', '.');

            return string.IsNullOrWhiteSpace(sanitized)
                ? "unknown"
                : sanitized;
        }
    }
}