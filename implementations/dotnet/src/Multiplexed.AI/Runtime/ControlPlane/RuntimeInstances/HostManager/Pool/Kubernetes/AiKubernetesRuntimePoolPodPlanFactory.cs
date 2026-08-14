using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes
{
    /// <summary>
    /// Creates immutable Kubernetes Runtime Pool Pod plans before Kubernetes resource creation.
    /// </summary>
    public static class AiKubernetesRuntimePoolPodPlanFactory
    {
        private const int MaximumKubernetesDnsLabelLength = 63;
        private const int PodNameRequestTokenLength = 12;

        /// <summary>
        /// Creates a Pod plan with a new immutable creation request identity.
        /// </summary>
        public static AiKubernetesRuntimePoolPodPlan Create(
            AiKubernetesRuntimePoolOptions options)
        {
            return Create(
                options,
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Creates a Pod plan using an explicit immutable creation request identity.
        /// </summary>
        public static AiKubernetesRuntimePoolPodPlan Create(
            AiKubernetesRuntimePoolOptions options,
            string podRequestId)
        {
            return CreateCore(
                options,
                podRequestId,
                primaryRuntimeInstanceId: null);
        }

        /// <summary>
        /// Creates a Pod plan while preserving the exact runtime identity requested by the
        /// provider as the first child in the pool.
        /// </summary>
        /// <param name="options">The enabled and validated pool options.</param>
        /// <param name="podRequestId">The immutable Pod creation request identity.</param>
        /// <param name="primaryRuntimeInstanceId">
        /// The exact provider-selected runtime identity that must be materialized by the new Pod.
        /// </param>
        /// <returns>The generated Pod topology plan.</returns>
        public static AiKubernetesRuntimePoolPodPlan Create(
            AiKubernetesRuntimePoolOptions options,
            string podRequestId,
            string primaryRuntimeInstanceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(primaryRuntimeInstanceId);

            return CreateCore(
                options,
                podRequestId,
                primaryRuntimeInstanceId);
        }

        /// <summary>
        /// Creates the immutable Pod plan.
        /// </summary>
        private static AiKubernetesRuntimePoolPodPlan CreateCore(
            AiKubernetesRuntimePoolOptions options,
            string podRequestId,
            string? primaryRuntimeInstanceId)
        {
            ArgumentNullException.ThrowIfNull(options);
            AiKubernetesRuntimePoolOptionsValidator.Validate(options);

            if (!options.Enabled)
            {
                throw new InvalidOperationException(
                    "Kubernetes Runtime Pool hosting must be enabled before creating a Pod plan.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(podRequestId);
            ValidateRequestIdentity(podRequestId);

            var runtimeInstances =
                CreateRuntimeInstancePlans(
                    options,
                    podRequestId,
                    primaryRuntimeInstanceId);

            return new AiKubernetesRuntimePoolPodPlan
            {
                PoolId = options.PoolId,
                PodRequestId = podRequestId,
                Namespace = options.Namespace,
                PodName = CreatePodName(
                    options.PodNamePrefix,
                    options.PoolId,
                    podRequestId),
                ProviderName = options.ProviderName,
                TransportName = options.TransportName,
                StableTransportPort = options.StableTransportPort,
                ReadinessPort = options.ReadinessPort,
                RuntimeInstances = runtimeInstances
            };
        }

        /// <summary>
        /// Creates independently identifiable child runtime plans.
        /// </summary>
        private static IReadOnlyList<AiKubernetesRuntimePoolRuntimeInstancePlan>
            CreateRuntimeInstancePlans(
                AiKubernetesRuntimePoolOptions options,
                string podRequestId,
                string? primaryRuntimeInstanceId)
        {
            var plans =
                new List<AiKubernetesRuntimePoolRuntimeInstancePlan>(
                    options.InitialRuntimeInstanceCount);

            var identities =
                new HashSet<string>(StringComparer.Ordinal);

            for (var ordinal = 1;
                 ordinal <= options.InitialRuntimeInstanceCount;
                 ordinal++)
            {
                var runtimeInstanceId =
                    ordinal == 1
                    && !string.IsNullOrWhiteSpace(primaryRuntimeInstanceId)
                        ? primaryRuntimeInstanceId
                        : CreateRuntimeInstanceId(
                            options.RuntimeInstanceIdPrefix,
                            ordinal,
                            podRequestId);

                if (!identities.Add(runtimeInstanceId))
                {
                    throw new InvalidOperationException(
                        string.Concat(
                            "Duplicate Kubernetes Runtime Pool RuntimeInstanceId '",
                            runtimeInstanceId,
                            "'."));
                }

                plans.Add(
                    new AiKubernetesRuntimePoolRuntimeInstancePlan
                    {
                        PoolId = options.PoolId,
                        PodRequestId = podRequestId,
                        Ordinal = ordinal,
                        RuntimeInstanceId = runtimeInstanceId,
                        ProviderName = options.ProviderName,
                        TransportName = options.TransportName,
                        TransportPort =
                            options.FirstChildTransportPort
                            + ((ordinal - 1)
                            * options.ChildTransportPortStride)
                    });
            }

            return plans.AsReadOnly();
        }

        /// <summary>
        /// Creates a globally unique runtime instance identifier.
        /// </summary>
        private static string CreateRuntimeInstanceId(
            string prefix,
            int ordinal,
            string podRequestId)
        {
            return string.Concat(
                prefix,
                "-",
                ordinal.ToString(CultureInfo.InvariantCulture),
                "-",
                podRequestId);
        }

        /// <summary>
        /// Creates a DNS-label-safe Pod name while preserving a request-specific suffix.
        /// </summary>
        private static string CreatePodName(
            string prefix,
            string poolId,
            string podRequestId)
        {
            var baseName =
                NormalizeKubernetesDnsLabel(
                    string.Concat(prefix, "-", poolId));

            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "runtime-pool";
            }

            var requestToken =
                NormalizeKubernetesDnsLabel(podRequestId);

            if (requestToken.Length > PodNameRequestTokenLength)
            {
                requestToken =
                    requestToken.Substring(
                        0,
                        PodNameRequestTokenLength);
            }

            var maximumBaseLength =
                MaximumKubernetesDnsLabelLength
                - requestToken.Length
                - 1;

            if (baseName.Length > maximumBaseLength)
            {
                baseName =
                    baseName
                        .Substring(
                            0,
                            maximumBaseLength)
                        .TrimEnd('-');
            }

            return string.Concat(
                baseName,
                "-",
                requestToken);
        }

        /// <summary>
        /// Normalizes a value into a Kubernetes DNS label.
        /// </summary>
        private static string NormalizeKubernetesDnsLabel(
            string value)
        {
            var builder = new StringBuilder(value.Length);
            var previousWasSeparator = false;

            foreach (var character in value)
            {
                var normalized = char.ToLowerInvariant(character);
                if (char.IsLetterOrDigit(normalized))
                {
                    builder.Append(normalized);
                    previousWasSeparator = false;
                    continue;
                }

                if (previousWasSeparator || builder.Length == 0)
                {
                    continue;
                }

                builder.Append('-');
                previousWasSeparator = true;
            }

            return builder
                .ToString()
                .Trim('-');
        }

        /// <summary>
        /// Validates the caller-owned Pod request identity.
        /// </summary>
        private static void ValidateRequestIdentity(
            string podRequestId)
        {
            foreach (var character in podRequestId)
            {
                if (char.IsLetterOrDigit(character)
                    || character == '-')
                {
                    continue;
                }

                throw new ArgumentException(
                    "Pod request identities may contain only letters, digits, and hyphens.",
                    nameof(podRequestId));
            }
        }
    }
}
