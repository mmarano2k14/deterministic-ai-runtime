using System;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry
{
    /// <summary>
    /// Resolves authoritative runtime pool and host identities without consulting metadata.
    /// </summary>
    public static class AiRuntimeInstanceRegistrationIdentityResolver
    {
        /// <summary>
        /// Resolves the first-class registration identity.
        /// </summary>
        /// <param name="configuredPoolId">The explicitly configured logical pool identifier.</param>
        /// <param name="configuredHostId">The explicitly configured host-incarnation identifier.</param>
        /// <param name="environmentHostId">The provider-neutral host identity from the environment.</param>
        /// <returns>The resolved first-class registration identity.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a pool identifier is configured without any resolvable host identity.
        /// </exception>
        public static AiRuntimeInstanceRegistrationIdentity Resolve(
            string? configuredPoolId,
            string? configuredHostId,
            string? environmentHostId)
        {
            var poolId = Normalize(configuredPoolId);
            var hostId = Normalize(configuredHostId) ?? Normalize(environmentHostId);

            if (poolId is not null && hostId is null)
            {
                throw new InvalidOperationException(
                    "A runtime registration with PoolId requires a first-class HostId.");
            }

            return new AiRuntimeInstanceRegistrationIdentity
            {
                PoolId = poolId,
                HostId = hostId
            };
        }

        /// <summary>
        /// Converts empty identity configuration to an absent optional identity.
        /// </summary>
        /// <param name="value">The configured identity value.</param>
        /// <returns>The original value when meaningful; otherwise, <see langword="null"/>.</returns>
        private static string? Normalize(
            string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : value;
        }
    }
}
