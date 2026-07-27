using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Validates RuntimeInstanceOnly launch and readiness settings for process-pool children.
    /// </summary>
    public static class AiRuntimeProcessPoolRuntimeInstanceOptionsValidator
    {
        /// <summary>
        /// Validates the supplied runtime instance options.
        /// </summary>
        /// <param name="options">The runtime instance options.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="options"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when required launch, capacity, or readiness configuration is invalid.
        /// </exception>
        public static void Validate(
            AiRuntimeProcessPoolRuntimeInstanceOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.DotnetExecutablePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.RuntimeHostAssemblyPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.EndpointHost);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ControlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ProviderName);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.TransportName);
            ArgumentNullException.ThrowIfNull(options.ExecutionContextSnapshot);

            if (options.BasePort <= 0 || options.BasePort > 65535)
            {
                throw new ArgumentException(
                    "BasePort must be between 1 and 65535.",
                    nameof(options));
            }

            if (options.MaxPort < options.BasePort || options.MaxPort > 65535)
            {
                throw new ArgumentException(
                    "MaxPort must be between BasePort and 65535.",
                    nameof(options));
            }

            if (options.WorkerCountPerInstance <= 0 ||
                options.MaxConcurrentRunsPerInstance <= 0 ||
                options.LocalQueueCapacity <= 0)
            {
                throw new ArgumentException(
                    "Worker count, concurrent run count, and queue capacity must be greater than zero.",
                    nameof(options));
            }

            if (options.StartupTimeout <= TimeSpan.Zero ||
                options.ReadinessPollInterval <= TimeSpan.Zero ||
                options.ReadinessPollInterval > options.StartupTimeout)
            {
                throw new ArgumentException(
                    "Readiness timing values must be positive and the poll interval cannot exceed the startup timeout.",
                    nameof(options));
            }

            if (options.DiscoveryResolutionTimeout <= TimeSpan.Zero ||
                options.DiscoveryResolutionPollInterval <= TimeSpan.Zero ||
                options.DiscoveryResolutionPollInterval >
                    options.DiscoveryResolutionTimeout)
            {
                throw new ArgumentException(
                    "Discovery timing values must be positive and the poll interval cannot exceed the discovery timeout.",
                    nameof(options));
            }

            if (options.RequireControlPlaneDiscovery &&
                !options.EnableControlPlaneDiscovery)
            {
                throw new ArgumentException(
                    "RequireControlPlaneDiscovery cannot be true when EnableControlPlaneDiscovery is false.",
                    nameof(options));
            }

            if (options.HeartbeatInterval <= TimeSpan.Zero ||
                options.StopTimeoutSeconds <= 0)
            {
                throw new ArgumentException(
                    "HeartbeatInterval and StopTimeoutSeconds must be greater than zero.",
                    nameof(options));
            }

            if (options.EnvironmentVariables is null)
            {
                throw new ArgumentException(
                    "EnvironmentVariables cannot be null.",
                    nameof(options));
            }
        }
    }
}
