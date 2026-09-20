using System;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.KubernetesPool
{
    /// <summary>
    /// Applies optional matrix-only immutable image selection to the existing Kubernetes Runtime Pool host options.
    /// </summary>
    internal static class KubernetesRuntimePoolMatrixImageProfile
    {
        public const string RepositoryEnvironmentVariable =
            "MULTIPLEXED_AI_MATRIX_KUBERNETES_RUNTIME_IMAGE_REPOSITORY";

        public const string DigestEnvironmentVariable =
            "MULTIPLEXED_AI_MATRIX_KUBERNETES_RUNTIME_IMAGE_DIGEST";

        /// <summary>
        /// Applies the exact repository/digest pair when the matrix runner provides one.
        /// Historical production scenarios keep their existing tagged image when no override is present.
        /// </summary>
        /// <param name="options">The existing Kubernetes Runtime Pool host options.</param>
        public static void Apply(
            AiKubernetesRuntimePoolHostOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var repository =
                Environment.GetEnvironmentVariable(
                    RepositoryEnvironmentVariable)?.Trim()
                ?? string.Empty;

            var digest =
                Environment.GetEnvironmentVariable(
                    DigestEnvironmentVariable)?.Trim()
                ?? string.Empty;

            if (repository.Length == 0 && digest.Length == 0)
            {
                return;
            }

            if (repository.Length == 0 || digest.Length == 0)
            {
                throw new InvalidOperationException(
                    "KubernetesPool matrix immutable runtime image override requires both repository and digest environment variables.");
            }

            options.RuntimeImage = string.Empty;
            options.RuntimeImageRepository = repository;
            options.RuntimeImageDigest = digest;
            options.RequireImmutableRuntimeImage = true;
        }
    }
}
