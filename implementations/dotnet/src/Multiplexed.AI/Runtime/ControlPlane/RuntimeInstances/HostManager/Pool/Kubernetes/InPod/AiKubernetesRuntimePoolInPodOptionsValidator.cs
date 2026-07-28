using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod
{
    /// <summary>
    /// Validates the exact in-Pod Runtime Pool bootstrap contract.
    /// </summary>
    public static class AiKubernetesRuntimePoolInPodOptionsValidator
    {
        /// <summary>
        /// Validates enabled in-Pod Runtime Pool options.
        /// </summary>
        public static void Validate(
            AiKubernetesRuntimePoolInPodOptions options,
            bool requirePodUidFile = true)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (!options.Enabled)
            {
                return;
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(options.PoolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.PodUidFilePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                options.RuntimeInstanceIdPrefix);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ControlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ProviderName);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.TransportName);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                options.DotnetExecutablePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                options.RuntimeHostAssemblyPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.EndpointHost);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ContextKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Project);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.UserId);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.TenantId);

            if (requirePodUidFile
                && !File.Exists(options.PodUidFilePath))
            {
                throw new FileNotFoundException(
                    "The Kubernetes Pod UID Downward API file was not found.",
                    options.PodUidFilePath);
            }

            if (options.MinimumProcessCount <= 0
                || options.InitialProcessCount
                    < options.MinimumProcessCount
                || options.MaximumProcessCount
                    < options.InitialProcessCount)
            {
                throw new ArgumentException(
                    "The minimum, initial, and maximum process counts are invalid.",
                    nameof(options));
            }

            if (options.StartupParallelism != 1)
            {
                throw new ArgumentException(
                    "Step 5D requires StartupParallelism=1 so exact planned child ports remain deterministic.",
                    nameof(options));
            }

            if (options.RuntimeInstances.Count
                != options.InitialProcessCount)
            {
                throw new ArgumentException(
                    "RuntimeInstances must contain exactly InitialProcessCount entries.",
                    nameof(options));
            }

            ValidateRuntimeInstances(options.RuntimeInstances);

            if (options.ChildStartupTimeout <= TimeSpan.Zero
                || options.ChildReadinessPollInterval <= TimeSpan.Zero
                || options.ChildReadinessPollInterval
                    > options.ChildStartupTimeout)
            {
                throw new ArgumentException(
                    "Child readiness timing values are invalid.",
                    nameof(options));
            }

            if (options.WorkerCountPerInstance <= 0
                || options.MaxConcurrentRunsPerInstance <= 0
                || options.LocalQueueCapacity <= 0
                || options.ShutdownTimeoutSeconds <= 0
                || options.SnapshotTtlSeconds <= 0)
            {
                throw new ArgumentException(
                    "Capacity, shutdown, and snapshot values must be greater than zero.",
                    nameof(options));
            }
        }

        /// <summary>
        /// Reads and validates the immutable Kubernetes Pod UID.
        /// </summary>
        public static string ReadHostId(
            AiKubernetesRuntimePoolInPodOptions options)
        {
            Validate(options);

            var hostId =
                File.ReadAllText(options.PodUidFilePath)
                    .Trim();

            if (string.IsNullOrWhiteSpace(hostId))
            {
                throw new InvalidOperationException(
                    "The Kubernetes Pod UID file is empty.");
            }

            return hostId;
        }

        /// <summary>
        /// Validates exact child identities, ordinals, and ports.
        /// </summary>
        private static void ValidateRuntimeInstances(
            IEnumerable<AiKubernetesRuntimePoolInPodRuntimeInstanceOptions>
                runtimeInstances)
        {
            var ordered =
                runtimeInstances
                    .OrderBy(item => item.Ordinal)
                    .ToArray();

            for (var index = 0; index < ordered.Length; index++)
            {
                var runtime = ordered[index];
                var expectedOrdinal = index + 1;

                if (runtime.Ordinal != expectedOrdinal)
                {
                    throw new ArgumentException(
                        "Runtime instance ordinals must be contiguous and one-based.",
                        nameof(runtimeInstances));
                }

                ArgumentException.ThrowIfNullOrWhiteSpace(
                    runtime.RuntimeInstanceId);

                if (runtime.TransportPort <= 0
                    || runtime.TransportPort > 65535)
                {
                    throw new ArgumentException(
                        "Every child transport port must be between 1 and 65535.",
                        nameof(runtimeInstances));
                }
            }

            if (ordered
                    .Select(item => item.RuntimeInstanceId)
                    .Distinct(StringComparer.Ordinal)
                    .Count()
                != ordered.Length)
            {
                throw new ArgumentException(
                    "RuntimeInstanceId values must be unique.",
                    nameof(runtimeInstances));
            }

            if (ordered
                    .Select(item => item.TransportPort)
                    .Distinct()
                    .Count()
                != ordered.Length)
            {
                throw new ArgumentException(
                    "Child transport ports must be unique.",
                    nameof(runtimeInstances));
            }
        }
    }
}
