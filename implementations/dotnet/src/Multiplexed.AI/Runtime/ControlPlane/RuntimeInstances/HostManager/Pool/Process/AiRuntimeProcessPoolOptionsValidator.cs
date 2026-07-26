namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Validates process-host Runtime Pool Manager configuration.
    /// </summary>
    public static class AiRuntimeProcessPoolOptionsValidator
    {
        /// <summary>
        /// Validates the supplied process pool options.
        /// </summary>
        /// <param name="options">The process pool options to validate.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="options"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when an enabled pool has an invalid identity prefix or process count boundary.
        /// </exception>
        public static void Validate(
            AiRuntimeProcessPoolOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (!options.Enabled)
            {
                return;
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(options.PoolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.HostIdPrefix);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.RuntimeInstanceIdPrefix);

            if (options.MinimumProcessCount <= 0)
            {
                throw new ArgumentException(
                    "MinimumProcessCount must be greater than zero for an enabled runtime pool.",
                    nameof(options));
            }

            if (options.InitialProcessCount < options.MinimumProcessCount)
            {
                throw new ArgumentException(
                    "InitialProcessCount cannot be lower than MinimumProcessCount.",
                    nameof(options));
            }

            if (options.MaximumProcessCount < options.InitialProcessCount)
            {
                throw new ArgumentException(
                    "MaximumProcessCount cannot be lower than InitialProcessCount.",
                    nameof(options));
            }

            if (options.StartupParallelism <= 0)
            {
                throw new ArgumentException(
                    "StartupParallelism must be greater than zero.",
                    nameof(options));
            }

            if (options.StartupParallelism > options.MaximumProcessCount)
            {
                throw new ArgumentException(
                    "StartupParallelism cannot exceed MaximumProcessCount.",
                    nameof(options));
            }

            if (options.ShutdownTimeoutSeconds <= 0)
            {
                throw new ArgumentException(
                    "ShutdownTimeoutSeconds must be greater than zero.",
                    nameof(options));
            }
        }
    }
}
