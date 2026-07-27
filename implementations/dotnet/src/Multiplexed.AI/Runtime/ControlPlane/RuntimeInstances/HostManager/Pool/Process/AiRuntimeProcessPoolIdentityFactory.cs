using System.Globalization;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Creates immutable process pool host identities and independent runtime instance identities.
    /// </summary>
    public static class AiRuntimeProcessPoolIdentityFactory
    {
        /// <summary>
        /// Creates the identity of one process-host runtime pool incarnation.
        /// </summary>
        /// <param name="options">The validated process pool options.</param>
        /// <returns>The generated pool identity.</returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="options"/> is <see langword="null"/>.
        /// </exception>
        public static AiRuntimeProcessPoolIdentity CreatePoolIdentity(
            AiRuntimeProcessPoolOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            AiRuntimeProcessPoolOptionsValidator.Validate(options);

            return new AiRuntimeProcessPoolIdentity
            {
                PoolId = options.PoolId,
                HostId = string.Concat(
                    options.HostIdPrefix,
                    "-",
                    Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                RuntimeInstanceIdPrefix = options.RuntimeInstanceIdPrefix
            };
        }

        /// <summary>
        /// Creates an independent runtime instance identifier for a child process.
        /// </summary>
        /// <param name="identity">The process pool identity.</param>
        /// <param name="ordinal">The one-based child ordinal used only for diagnostics.</param>
        /// <returns>A globally unique runtime instance identifier.</returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="identity"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="ordinal"/> is lower than one.
        /// </exception>
        public static string CreateRuntimeInstanceId(
            AiRuntimeProcessPoolIdentity identity,
            int ordinal)
        {
            ArgumentNullException.ThrowIfNull(identity);

            if (ordinal <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ordinal),
                    ordinal,
                    "A runtime process ordinal must be greater than zero.");
            }

            return string.Concat(
                identity.RuntimeInstanceIdPrefix,
                "-",
                ordinal.ToString(CultureInfo.InvariantCulture),
                "-",
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        }
    }
}
