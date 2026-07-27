using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances
{
    /// <summary>
    /// Validates the first-class identity contract of a runtime instance registration.
    /// </summary>
    /// <remarks>
    /// This validator intentionally inspects only typed identity properties. Optional metadata is
    /// non-authoritative and must never be used to infer pool membership, host membership, routing,
    /// lifecycle, draining, capacity selection, or recovery behavior.
    /// </remarks>
    internal static class AiRuntimePoolIdentityValidator
    {
        /// <summary>
        /// Validates a runtime instance registration before it is written to a registry.
        /// </summary>
        /// <param name="registration">The runtime instance registration to validate.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="registration"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when a required identity is empty, when an optional identity contains only
        /// whitespace, or when a pooled runtime does not define an exact host incarnation.
        /// </exception>
        public static void ValidateRegistration(
            AiRuntimeInstanceRegistration registration)
        {
            ArgumentNullException.ThrowIfNull(registration);
            ArgumentException.ThrowIfNullOrWhiteSpace(registration.RuntimeInstanceId);

            ValidateOptionalIdentity(
                registration.PoolId,
                nameof(registration.PoolId));

            ValidateOptionalIdentity(
                registration.HostId,
                nameof(registration.HostId));

            if (registration.PoolId is not null && registration.HostId is null)
            {
                throw new ArgumentException(
                    "A runtime instance that defines PoolId must also define the immutable " +
                    "HostId of its exact host incarnation.",
                    nameof(registration));
            }
        }

        /// <summary>
        /// Validates an optional first-class identity value.
        /// </summary>
        /// <param name="value">The optional identity value.</param>
        /// <param name="parameterName">The identity property name used by validation errors.</param>
        /// <exception cref="ArgumentException">
        /// Thrown when a non-null identity contains only whitespace.
        /// </exception>
        private static void ValidateOptionalIdentity(
            string? value,
            string parameterName)
        {
            if (value is not null && string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "A first-class identity cannot contain only whitespace.",
                    parameterName);
            }
        }
    }
}
