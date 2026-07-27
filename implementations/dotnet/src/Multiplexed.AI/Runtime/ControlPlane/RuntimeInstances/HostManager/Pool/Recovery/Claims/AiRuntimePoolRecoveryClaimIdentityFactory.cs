using System;
using System.Security.Cryptography;
using System.Text;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims
{
    /// <summary>
    /// Creates deterministic recovery claim identities from exact authority and inventory.
    /// </summary>
    public static class AiRuntimePoolRecoveryClaimIdentityFactory
    {
        /// <summary>
        /// Creates one deterministic claim identifier.
        /// </summary>
        /// <param name="request">The normalized exact claim request.</param>
        /// <returns>The deterministic claim identifier.</returns>
        public static string CreateClaimId(
            AiRuntimePoolRecoveryClaimRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            var authority =
                string.Join(
                    "\n",
                    request.FailureId,
                    request.PoolId,
                    request.HostId,
                    request.RuntimeInstanceId,
                    request.RouteId,
                    request.InventoryFingerprint,
                    request.CandidateCount.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));

            var hash =
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(authority));

            return string.Concat(
                "recovery-claim-",
                Convert.ToHexString(hash)
                    .ToLowerInvariant());
        }
    }
}
