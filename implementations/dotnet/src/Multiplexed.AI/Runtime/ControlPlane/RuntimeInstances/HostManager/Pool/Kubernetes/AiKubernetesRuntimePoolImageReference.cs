using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes
{
    /// <summary>
    /// Resolves the Kubernetes Runtime Pool host image without allowing an immutable
    /// repository/digest pair to be mixed with a second image authority.
    /// </summary>
    internal static class AiKubernetesRuntimePoolImageReference
    {
        private const string Sha256Prefix = "sha256:";

        /// <summary>
        /// Resolves the configured Runtime Pool image. Repository/digest configuration is
        /// preferred for new deterministic profiles; the historical RuntimeImage value remains
        /// available unless immutable-image enforcement is explicitly enabled.
        /// </summary>
        public static string Resolve(AiKubernetesRuntimePoolHostOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var image = options.RuntimeImage?.Trim() ?? string.Empty;
            var repository = options.RuntimeImageRepository?.Trim() ?? string.Empty;
            var digest = options.RuntimeImageDigest?.Trim() ?? string.Empty;
            var hasRepository = repository.Length > 0;
            var hasDigest = digest.Length > 0;

            if (hasRepository != hasDigest)
            {
                throw new InvalidOperationException(
                    "Kubernetes Runtime Pool immutable image configuration requires both RuntimeImageRepository and RuntimeImageDigest.");
            }

            if (hasRepository)
            {
                if (image.Length > 0)
                {
                    throw new InvalidOperationException(
                        "Kubernetes Runtime Pool image configuration cannot combine RuntimeImage with RuntimeImageRepository/RuntimeImageDigest.");
                }

                if (repository.Contains('@'))
                {
                    throw new InvalidOperationException(
                        "RuntimeImageRepository must not contain a digest; configure RuntimeImageDigest separately.");
                }

                ValidateDigest(digest);
                return string.Concat(repository, "@", digest);
            }

            if (image.Length == 0)
            {
                throw new InvalidOperationException(
                    "Kubernetes Runtime Pool requires a runtime image.");
            }

            if (options.RequireImmutableRuntimeImage)
            {
                ValidateExactReference(image);
            }

            return image;
        }

        /// <summary>
        /// Validates one exact OCI image reference expressed as repository@sha256:digest.
        /// </summary>
        private static void ValidateExactReference(string image)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(image);
            var marker = image.LastIndexOf("@sha256:", StringComparison.Ordinal);
            if (marker <= 0)
            {
                throw new InvalidOperationException(
                    "Kubernetes Runtime Pool immutable image enforcement requires repository@sha256:<64-hex>.");
            }

            var repository = image[..marker];
            var digest = image[(marker + 1)..];
            if (repository.Length == 0 || repository.Contains('@'))
            {
                throw new InvalidOperationException(
                    "Kubernetes Runtime Pool immutable image reference has an invalid repository component.");
            }

            ValidateDigest(digest);
        }

        private static void ValidateDigest(string digest)
        {
            if (!digest.StartsWith(Sha256Prefix, StringComparison.Ordinal) ||
                digest.Length != Sha256Prefix.Length + 64)
            {
                throw new InvalidOperationException(
                    "Kubernetes Runtime Pool image digest must be an algorithm-qualified SHA-256 digest.");
            }

            foreach (var character in digest.AsSpan(Sha256Prefix.Length))
            {
                if (!char.IsAsciiHexDigit(character))
                {
                    throw new InvalidOperationException(
                        "Kubernetes Runtime Pool image digest must contain exactly 64 hexadecimal characters.");
                }
            }
        }
    }
}
