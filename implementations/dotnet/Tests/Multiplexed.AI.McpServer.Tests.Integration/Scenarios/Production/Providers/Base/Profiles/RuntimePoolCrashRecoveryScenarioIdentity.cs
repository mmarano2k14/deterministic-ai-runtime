using System.Security.Cryptography;
using System.Text;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Creates deterministic, scenario-isolated Runtime Pool identities from the logical control-plane identity.
    /// </summary>
    internal static class RuntimePoolCrashRecoveryScenarioIdentity
    {
        private const int HashLength = 12;
        private const int MaximumPoolIdLength = 63;

        /// <summary>
        /// Creates a deterministic Kubernetes-safe Runtime Pool identifier.
        /// </summary>
        /// <param name="prefix">The stable scenario profile prefix.</param>
        /// <param name="controlPlaneId">The scenario-isolated logical control-plane identifier.</param>
        /// <returns>The deterministic Runtime Pool identifier.</returns>
        public static string CreatePoolId(
            string prefix,
            string controlPlaneId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);

            var normalizedPrefix = NormalizePrefix(prefix);
            var source = string.Concat(
                normalizedPrefix,
                "|",
                controlPlaneId.Trim());

            var hash =
                Convert
                    .ToHexString(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes(source)))
                    .ToLowerInvariant()[..HashLength];

            var maximumPrefixLength =
                MaximumPoolIdLength - HashLength - 1;

            if (normalizedPrefix.Length > maximumPrefixLength)
            {
                normalizedPrefix =
                    normalizedPrefix[..maximumPrefixLength]
                        .TrimEnd('-');
            }

            return string.Concat(
                normalizedPrefix,
                "-",
                hash);
        }

        private static string NormalizePrefix(
            string prefix)
        {
            var characters =
                prefix
                    .Trim()
                    .ToLowerInvariant()
                    .Select(
                        character =>
                            char.IsAsciiLetterOrDigit(character)
                                ? character
                                : '-')
                    .ToArray();

            var normalized =
                new string(characters)
                    .Trim('-');

            if (string.IsNullOrWhiteSpace(normalized))
            {
                throw new ArgumentException(
                    "The Runtime Pool identity prefix must contain at least one ASCII letter or digit.",
                    nameof(prefix));
            }

            while (normalized.Contains(
                       "--",
                       StringComparison.Ordinal))
            {
                normalized = normalized.Replace(
                    "--",
                    "-",
                    StringComparison.Ordinal);
            }

            return normalized;
        }
    }
}
