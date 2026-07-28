using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims
{
    /// <summary>
    /// Creates deterministic recovery claim identities for exact failed memberships.
    /// </summary>
    public static class AiRuntimePoolRecoveryMembershipClaimIdentityFactory
    {
        /// <summary>
        /// Creates one deterministic membership claim identifier.
        /// </summary>
        public static string CreateClaimId(
            AiRuntimePoolRecoveryMembershipClaimRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            var authority =
                string.Join(
                    "\n",
                    request.FailureId,
                    request.PoolId,
                    request.HostId,
                    request.MembershipFingerprint,
                    request.MemberCount.ToString(
                        CultureInfo.InvariantCulture),
                    request.InventoryFingerprint,
                    request.CandidateCount.ToString(
                        CultureInfo.InvariantCulture));

            var hash =
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(authority));

            return string.Concat(
                "recovery-membership-claim-",
                Convert.ToHexString(hash)
                    .ToLowerInvariant());
        }
    }
}
