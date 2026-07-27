using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Validates real operating-system runtime pool child-process options.
    /// </summary>
    public static class AiRuntimeProcessPoolChildProcessOptionsValidator
    {
        /// <summary>
        /// Validates the supplied child-process options.
        /// </summary>
        /// <param name="options">The child-process options.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="options"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when required process configuration is missing or invalid.
        /// </exception>
        public static void Validate(
            AiRuntimeProcessPoolChildProcessOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ExecutablePath);

            if (options.Arguments is null)
            {
                throw new ArgumentException(
                    "Arguments cannot be null.",
                    nameof(options));
            }

            if (options.EnvironmentVariables is null)
            {
                throw new ArgumentException(
                    "EnvironmentVariables cannot be null.",
                    nameof(options));
            }

            foreach (var argument in options.Arguments)
            {
                if (argument is null)
                {
                    throw new ArgumentException(
                        "Arguments cannot contain null values.",
                        nameof(options));
                }
            }

            foreach (var pair in options.EnvironmentVariables)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    throw new ArgumentException(
                        "Environment variable names cannot be empty.",
                        nameof(options));
                }

                if (pair.Value is null)
                {
                    throw new ArgumentException(
                        $"Environment variable '{pair.Key}' cannot have a null value.",
                        nameof(options));
                }
            }

            if (options.StopTimeoutSeconds <= 0)
            {
                throw new ArgumentException(
                    "StopTimeoutSeconds must be greater than zero.",
                    nameof(options));
            }
        }
    }
}
