using System.Security.Cryptography;
using System.Text;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Creates retry-stable identities for one provider-level Runtime Pool Pod creation
    /// request.
    /// </summary>
    public static class AiRuntimePoolPodCreationIdentityFactory
    {
        private const int TokenLength = 24;

        /// <summary>
        /// Creates the deterministic host-strategy request identifier.
        /// </summary>
        /// <param name="request">The provider-level request.</param>
        /// <param name="candidate">The selected Pod-creation candidate.</param>
        /// <returns>The deterministic request identifier.</returns>
        public static string CreateHostRequestId(
            AiRuntimeScaleOutProviderRequest request,
            AiRuntimeCapacitySelectionCandidate candidate)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(candidate);

            return string.Concat(
                "kubernetes-runtime-pool-pod-scale-out-",
                CreateToken(request, candidate));
        }

        /// <summary>
        /// Creates the deterministic primary runtime identity materialized by the new
        /// Runtime Pool Pod.
        /// </summary>
        /// <param name="runtimeInstanceIdPrefix">
        /// The authoritative runtime identity prefix.
        /// </param>
        /// <param name="request">The provider-level request.</param>
        /// <param name="candidate">The selected Pod-creation candidate.</param>
        /// <returns>The deterministic primary runtime identity.</returns>
        public static string CreatePrimaryRuntimeInstanceId(
            string runtimeInstanceIdPrefix,
            AiRuntimeScaleOutProviderRequest request,
            AiRuntimeCapacitySelectionCandidate candidate)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                runtimeInstanceIdPrefix);
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(candidate);

            return string.Concat(
                runtimeInstanceIdPrefix.Trim(),
                "-pod-scale-out-",
                CreateToken(request, candidate));
        }

        /// <summary>
        /// Creates deterministic identity material from first-class request authority.
        /// </summary>
        private static string CreateToken(
            AiRuntimeScaleOutProviderRequest request,
            AiRuntimeCapacitySelectionCandidate candidate)
        {
            var snapshot =
                request.ExecutionContextSnapshot ??
                throw new InvalidOperationException(
                    "Runtime Pool Pod creation requires an execution context snapshot.");

            var authority =
                string.Join(
                    "\n",
                    request.RequestId,
                    request.ControlPlaneId,
                    request.SharedRunId,
                    candidate.PoolId,
                    candidate.ProviderName,
                    snapshot.TenantId,
                    snapshot.TenantGroupId,
                    request.IsolationMode);

            return Convert
                .ToHexString(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes(authority)))
                .ToLowerInvariant()[..TokenLength];
        }
    }
}
