using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes
{
    /// <summary>
    /// Validates strongly typed Kubernetes Runtime Pool topology configuration.
    /// </summary>
    public static class AiKubernetesRuntimePoolOptionsValidator
    {
        private const int MaximumTransportPort = 65535;

        /// <summary>
        /// Validates the supplied Kubernetes Runtime Pool options.
        /// </summary>
        /// <param name="options">The options to validate.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="options"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when enabled pool topology configuration is invalid.
        /// </exception>
        public static void Validate(
            AiKubernetesRuntimePoolOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (!options.Enabled)
            {
                return;
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(options.PoolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Namespace);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.PodNamePrefix);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.RuntimeInstanceIdPrefix);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ProviderName);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.TransportName);

            ValidateCountBoundaries(options);
            ValidateStartupAndShutdown(options);
            ValidateTransportPorts(options);
        }

        /// <summary>
        /// Validates minimum, initial, and maximum runtime instance count boundaries.
        /// </summary>
        private static void ValidateCountBoundaries(
            AiKubernetesRuntimePoolOptions options)
        {
            if (options.MaximumPodCount <= 0)
            {
                throw new ArgumentException(
                    "MaximumPodCount must be greater than zero.",
                    nameof(options));
            }

            if (options.MinimumRuntimeInstanceCount <= 0)
            {
                throw new ArgumentException(
                    "MinimumRuntimeInstanceCount must be greater than zero.",
                    nameof(options));
            }

            if (options.InitialRuntimeInstanceCount < options.MinimumRuntimeInstanceCount)
            {
                throw new ArgumentException(
                    "InitialRuntimeInstanceCount cannot be lower than MinimumRuntimeInstanceCount.",
                    nameof(options));
            }

            if (options.MaximumRuntimeInstanceCount < options.InitialRuntimeInstanceCount)
            {
                throw new ArgumentException(
                    "MaximumRuntimeInstanceCount cannot be lower than InitialRuntimeInstanceCount.",
                    nameof(options));
            }
        }

        /// <summary>
        /// Validates startup parallelism and graceful shutdown configuration.
        /// </summary>
        private static void ValidateStartupAndShutdown(
            AiKubernetesRuntimePoolOptions options)
        {
            if (options.StartupParallelism <= 0)
            {
                throw new ArgumentException(
                    "StartupParallelism must be greater than zero.",
                    nameof(options));
            }

            if (options.StartupParallelism > options.MaximumRuntimeInstanceCount)
            {
                throw new ArgumentException(
                    "StartupParallelism cannot exceed MaximumRuntimeInstanceCount.",
                    nameof(options));
            }

            if (options.ShutdownTimeoutSeconds <= 0)
            {
                throw new ArgumentException(
                    "ShutdownTimeoutSeconds must be greater than zero.",
                    nameof(options));
            }
        }

        /// <summary>
        /// Validates the stable transport port and the complete child port range.
        /// </summary>
        private static void ValidateTransportPorts(
            AiKubernetesRuntimePoolOptions options)
        {
            ValidatePort(
                options.StableTransportPort,
                nameof(options.StableTransportPort));

            ValidatePort(
                options.ReadinessPort,
                nameof(options.ReadinessPort));

            ValidatePort(
                options.FirstChildTransportPort,
                nameof(options.FirstChildTransportPort));

            if (options.ReadinessPort == options.StableTransportPort)
            {
                throw new ArgumentException(
                    "ReadinessPort must be distinct from StableTransportPort.",
                    nameof(options));
            }

            if (options.ChildTransportPortStride <= 0)
            {
                throw new ArgumentException(
                    "ChildTransportPortStride must be greater than zero.",
                    nameof(options));
            }

            var lastChildPort =
                (long)options.FirstChildTransportPort
                + ((long)options.MaximumRuntimeInstanceCount - 1L)
                * options.ChildTransportPortStride;

            if (lastChildPort > MaximumTransportPort)
            {
                throw new ArgumentException(
                    "The configured child transport port range exceeds 65535.",
                    nameof(options));
            }

            ValidateDoesNotOverlapChildRange(
                options.StableTransportPort,
                nameof(options.StableTransportPort),
                options);

            ValidateDoesNotOverlapChildRange(
                options.ReadinessPort,
                nameof(options.ReadinessPort),
                options);
        }

        /// <summary>
        /// Validates that one parent endpoint does not overlap a child transport port.
        /// </summary>
        private static void ValidateDoesNotOverlapChildRange(
            int port,
            string optionName,
            AiKubernetesRuntimePoolOptions options)
        {
            var offset =
                port - options.FirstChildTransportPort;

            if (offset < 0
                || offset % options.ChildTransportPortStride != 0)
            {
                return;
            }

            var ordinal = offset / options.ChildTransportPortStride;
            if (ordinal < options.MaximumRuntimeInstanceCount)
            {
                throw new ArgumentException(
                    string.Concat(
                        optionName,
                        " cannot overlap a child transport port."),
                    nameof(options));
            }
        }

        /// <summary>
        /// Validates one TCP port.
        /// </summary>
        private static void ValidatePort(
            int port,
            string optionName)
        {
            if (port <= 0 || port > MaximumTransportPort)
            {
                throw new ArgumentException(
                    string.Concat(optionName, " must be between 1 and 65535."),
                    optionName);
            }
        }
    }
}
