using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes
{
    /// <summary>
    /// Builds runtime-owned Kubernetes Pod specifications for opt-in Runtime Pool hosting.
    /// </summary>
    /// <remarks>
    /// The builder does not call Kubernetes, does not assign <c>HostId</c>, and does not
    /// modify the existing one-runtime-per-Pod Kubernetes specification path.
    /// </remarks>
    public sealed class AiKubernetesRuntimePoolPodSpecBuilder
    {
        private const string ApplicationName = "multiplexed-ai-runtime-pool";

        private readonly AiKubernetesRuntimePoolOptions poolOptions;
        private readonly AiKubernetesRuntimePoolHostOptions hostOptions;

        /// <summary>
        /// Initializes a new Runtime Pool Pod specification builder.
        /// </summary>
        /// <param name="poolOptions">The validated Runtime Pool topology options.</param>
        /// <param name="hostOptions">The Kubernetes container host options.</param>
        public AiKubernetesRuntimePoolPodSpecBuilder(
            AiKubernetesRuntimePoolOptions poolOptions,
            AiKubernetesRuntimePoolHostOptions hostOptions)
        {
            this.poolOptions =
                poolOptions
                ?? throw new ArgumentNullException(nameof(poolOptions));

            this.hostOptions =
                hostOptions
                ?? throw new ArgumentNullException(nameof(hostOptions));
        }

        /// <summary>
        /// Builds one immutable Pod specification from a pre-provisioning Pod plan.
        /// </summary>
        /// <param name="plan">The exact Pod topology plan.</param>
        /// <returns>The runtime-owned Kubernetes Runtime Pool Pod specification.</returns>
        public AiKubernetesRuntimePoolPodSpec Build(
            AiKubernetesRuntimePoolPodPlan plan)
        {
            ArgumentNullException.ThrowIfNull(plan);
            AiKubernetesRuntimePoolOptionsValidator.Validate(this.poolOptions);
            ValidateHostOptions(this.hostOptions);
            ValidatePlanAgreement(plan, this.poolOptions);

            return new AiKubernetesRuntimePoolPodSpec
            {
                PoolId = plan.PoolId,
                PodRequestId = plan.PodRequestId,
                Namespace = plan.Namespace,
                PodName = plan.PodName,
                RuntimeImage = this.hostOptions.RuntimeImage,
                ContainerName = this.hostOptions.ContainerName,
                ServiceAccountName = this.hostOptions.ServiceAccountName,
                ImagePullPolicy = this.hostOptions.ImagePullPolicy,
                Labels = this.BuildLabels(plan),
                Annotations = this.BuildAnnotations(plan),
                Ports = BuildPorts(plan),
                Bootstrap = this.BuildBootstrap(plan)
            };
        }

        /// <summary>
        /// Builds the strongly typed in-Pod bootstrap contract.
        /// </summary>
        private AiKubernetesRuntimePoolBootstrapSpec BuildBootstrap(
            AiKubernetesRuntimePoolPodPlan plan)
        {
            return new AiKubernetesRuntimePoolBootstrapSpec
            {
                PoolId = plan.PoolId,
                PodRequestId = plan.PodRequestId,
                ProviderName = plan.ProviderName,
                TransportName = plan.TransportName,
                StableTransportPort = plan.StableTransportPort,
                ReadinessPort = plan.ReadinessPort,
                InitialRuntimeInstanceCount =
                    this.poolOptions.InitialRuntimeInstanceCount,
                MinimumRuntimeInstanceCount =
                    this.poolOptions.MinimumRuntimeInstanceCount,
                MaximumRuntimeInstanceCount =
                    this.poolOptions.MaximumRuntimeInstanceCount,
                StartupParallelism =
                    this.poolOptions.StartupParallelism,
                ShutdownTimeoutSeconds =
                    this.poolOptions.ShutdownTimeoutSeconds,
                RuntimeInstances =
                    plan.RuntimeInstances
                        .Select(CopyRuntimePlan)
                        .ToArray()
            };
        }

        /// <summary>
        /// Builds the stable pool endpoint and exact internal child ports.
        /// </summary>
        private static IReadOnlyList<AiKubernetesRuntimePoolContainerPort>
            BuildPorts(
                AiKubernetesRuntimePoolPodPlan plan)
        {
            var ports =
                new List<AiKubernetesRuntimePoolContainerPort>(
                    plan.RuntimeInstances.Count + 2)
                {
                    new()
                    {
                        Name =
                            string.Equals(
                                plan.TransportName,
                                "grpc",
                                StringComparison.OrdinalIgnoreCase)
                                ? "pool-grpc"
                                : "pool-http",
                        Port = plan.StableTransportPort,
                        RuntimeInstanceId = null
                    },
                    new()
                    {
                        Name = "pool-ready",
                        Port = plan.ReadinessPort,
                        RuntimeInstanceId = null
                    }
                };

            foreach (var runtime in plan.RuntimeInstances)
            {
                ports.Add(
                    new AiKubernetesRuntimePoolContainerPort
                    {
                        Name =
                            string.Concat(
                                "runtime-",
                                runtime.Ordinal.ToString(
                                    CultureInfo.InvariantCulture)),
                        Port = runtime.TransportPort,
                        RuntimeInstanceId = runtime.RuntimeInstanceId
                    });
            }

            return ports.AsReadOnly();
        }

        /// <summary>
        /// Builds required and caller-supplied diagnostic labels.
        /// </summary>
        private IReadOnlyDictionary<string, string> BuildLabels(
            AiKubernetesRuntimePoolPodPlan plan)
        {
            var labels =
                new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["app.kubernetes.io/name"] = ApplicationName,
                    ["app.kubernetes.io/component"] = "runtime-pool",
                    ["app.kubernetes.io/instance"] = plan.PodName,
                    ["multiplexed.ai/runtime-pool"] = "true",
                    ["multiplexed.ai/transport"] =
                        NormalizeLabelValue(plan.TransportName)
                };

            AddWithoutReplacingRequiredValues(
                labels,
                this.hostOptions.Labels,
                "label");

            return labels;
        }

        /// <summary>
        /// Builds required and caller-supplied diagnostic annotations.
        /// </summary>
        private IReadOnlyDictionary<string, string> BuildAnnotations(
            AiKubernetesRuntimePoolPodPlan plan)
        {
            var annotations =
                new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["multiplexed.ai/pool-id"] = plan.PoolId,
                    ["multiplexed.ai/pod-request-id"] = plan.PodRequestId,
                    ["multiplexed.ai/runtime-instance-count"] =
                        plan.RuntimeInstances.Count.ToString(
                            CultureInfo.InvariantCulture)
                };

            AddWithoutReplacingRequiredValues(
                annotations,
                this.hostOptions.Annotations,
                "annotation");

            return annotations;
        }

        /// <summary>
        /// Adds caller-supplied metadata without allowing required values to be replaced.
        /// </summary>
        private static void AddWithoutReplacingRequiredValues(
            IDictionary<string, string> destination,
            IEnumerable<KeyValuePair<string, string>> values,
            string metadataKind)
        {
            foreach (var value in values)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value.Key);

                if (!destination.TryAdd(
                        value.Key,
                        value.Value ?? string.Empty))
                {
                    throw new InvalidOperationException(
                        string.Concat(
                            "Kubernetes Runtime Pool ",
                            metadataKind,
                            " '",
                            value.Key,
                            "' is reserved and cannot be replaced."));
                }
            }
        }

        /// <summary>
        /// Validates the Kubernetes container host options.
        /// </summary>
        private static void ValidateHostOptions(
            AiKubernetesRuntimePoolHostOptions options)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.RuntimeImage);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ContainerName);

            if (!string.Equals(
                    options.ServiceType,
                    "ClusterIP",
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    options.ServiceType,
                    "NodePort",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "ServiceType must be ClusterIP or NodePort.",
                    nameof(options));
            }

            if (options.StartupTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentException(
                    "StartupTimeout must be greater than zero.",
                    nameof(options));
            }

            if (options.ReadinessPollInterval <= TimeSpan.Zero)
            {
                throw new ArgumentException(
                    "ReadinessPollInterval must be greater than zero.",
                    nameof(options));
            }
        }

        /// <summary>
        /// Validates that the immutable plan and configured topology agree exactly.
        /// </summary>
        private static void ValidatePlanAgreement(
            AiKubernetesRuntimePoolPodPlan plan,
            AiKubernetesRuntimePoolOptions options)
        {
            if (!StringComparer.Ordinal.Equals(
                    plan.PoolId,
                    options.PoolId))
            {
                throw new InvalidOperationException(
                    "The Kubernetes Runtime Pool Pod plan has a different PoolId.");
            }

            if (!StringComparer.Ordinal.Equals(
                    plan.Namespace,
                    options.Namespace))
            {
                throw new InvalidOperationException(
                    "The Kubernetes Runtime Pool Pod plan has a different namespace.");
            }

            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    plan.ProviderName,
                    options.ProviderName)
                || !StringComparer.OrdinalIgnoreCase.Equals(
                    plan.TransportName,
                    options.TransportName))
            {
                throw new InvalidOperationException(
                    "The Kubernetes Runtime Pool Pod plan has a different provider or transport.");
            }

            if (plan.StableTransportPort != options.StableTransportPort)
            {
                throw new InvalidOperationException(
                    "The Kubernetes Runtime Pool Pod plan has a different stable transport port.");
            }

            if (plan.ReadinessPort != options.ReadinessPort)
            {
                throw new InvalidOperationException(
                    "The Kubernetes Runtime Pool Pod plan has a different readiness port.");
            }

            if (plan.RuntimeInstances.Count
                != options.InitialRuntimeInstanceCount)
            {
                throw new InvalidOperationException(
                    "The Kubernetes Runtime Pool Pod plan has a different initial runtime count.");
            }
        }

        /// <summary>
        /// Creates an isolated copy of a child runtime plan.
        /// </summary>
        private static AiKubernetesRuntimePoolRuntimeInstancePlan CopyRuntimePlan(
            AiKubernetesRuntimePoolRuntimeInstancePlan plan)
        {
            return new AiKubernetesRuntimePoolRuntimeInstancePlan
            {
                PoolId = plan.PoolId,
                PodRequestId = plan.PodRequestId,
                Ordinal = plan.Ordinal,
                RuntimeInstanceId = plan.RuntimeInstanceId,
                ProviderName = plan.ProviderName,
                TransportName = plan.TransportName,
                TransportPort = plan.TransportPort
            };
        }

        /// <summary>
        /// Normalizes a simple Kubernetes label value.
        /// </summary>
        private static string NormalizeLabelValue(
            string value)
        {
            return value
                .Trim()
                .ToLowerInvariant()
                .Replace('_', '-');
        }
    }
}
