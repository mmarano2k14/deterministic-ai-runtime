using System;
using System.Security.Cryptography;
using System.Text;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Creates retry-stable fresh identities for one exact failed-Pod recovery claim.
    /// </summary>
    /// <remarks>
    /// The active lease authorizes one mutation attempt, but it is not part of the logical
    /// replacement identity. Reacquiring the same exact claim therefore converges on the same
    /// provider request and runtime identities instead of creating duplicate replacement Pods.
    /// A later failure of the replacement Pod is represented by a new failure and a new claim.
    /// </remarks>
    public static class AiKubernetesRuntimePoolPodReplacementIdentityFactory
    {
        private const int ReplacementTokenLength = 24;

        /// <summary>
        /// Creates the deterministic provider request identity used to deduplicate Pod creation
        /// for one exact failed-Pod recovery claim.
        /// </summary>
        public static string CreateRequestId(
            AiRuntimePoolRecoveryMembershipClaim claim)
        {
            ArgumentNullException.ThrowIfNull(claim);

            return string.Concat(
                "kubernetes-runtime-pool-pod-replacement-",
                CreateToken(claim));
        }

        /// <summary>
        /// Creates the deterministic fresh primary runtime identity materialized by the
        /// replacement Pod for one exact failed-Pod recovery claim.
        /// </summary>
        public static string CreatePrimaryRuntimeInstanceId(
            string runtimeInstanceIdPrefix,
            AiRuntimePoolRecoveryMembershipClaim claim)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                runtimeInstanceIdPrefix);
            ArgumentNullException.ThrowIfNull(claim);

            return string.Concat(
                runtimeInstanceIdPrefix.Trim(),
                "-pod-replacement-",
                CreateToken(claim));
        }

        private static string CreateToken(
            AiRuntimePoolRecoveryMembershipClaim claim)
        {
            var authority =
                string.Join(
                    "\n",
                    claim.ClaimId,
                    claim.FailureId,
                    claim.PoolId,
                    claim.HostId,
                    claim.MembershipFingerprint,
                    claim.InventoryFingerprint);

            return Convert
                .ToHexString(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes(authority)))
                .ToLowerInvariant()[..ReplacementTokenLength];
        }
    }
}
