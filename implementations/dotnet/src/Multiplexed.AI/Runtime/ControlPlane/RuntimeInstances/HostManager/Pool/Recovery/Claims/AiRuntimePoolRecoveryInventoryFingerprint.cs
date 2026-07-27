using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims
{
    /// <summary>
    /// Calculates a deterministic fingerprint for one ordered exact assigned-work inventory.
    /// </summary>
    public static class AiRuntimePoolRecoveryInventoryFingerprint
    {
        /// <summary>
        /// Calculates the exact inventory fingerprint.
        /// </summary>
        /// <param name="inventory">The read-only exact-runtime inventory.</param>
        /// <returns>The lowercase SHA-256 fingerprint.</returns>
        public static string Calculate(
            AiRuntimePoolAssignedWorkInventory inventory)
        {
            ArgumentNullException.ThrowIfNull(inventory);

            var builder =
                new StringBuilder();

            Append(
                builder,
                inventory.FailureId,
                inventory.PoolId,
                inventory.HostId,
                inventory.RuntimeInstanceId,
                inventory.RouteId);

            foreach (var candidate in inventory.Candidates)
            {
                Append(
                    builder,
                    candidate.LocalRunId,
                    candidate.ExecutionId,
                    candidate.Status,
                    candidate.TenantId,
                    candidate.TenantGroupId,
                    candidate.SharedRunId,
                    ((int)candidate.Kind).ToString(
                        CultureInfo.InvariantCulture),
                    candidate.CreatedAtUtc
                        .ToUniversalTime()
                        .ToString(
                            "O",
                            CultureInfo.InvariantCulture));
            }

            var hash =
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        builder.ToString()));

            return Convert.ToHexString(hash)
                .ToLowerInvariant();
        }

        /// <summary>
        /// Appends length-prefixed values to prevent delimiter ambiguity.
        /// </summary>
        private static void Append(
            StringBuilder builder,
            params string?[] values)
        {
            foreach (var value in values)
            {
                var normalized =
                    value ?? string.Empty;

                builder.Append(
                    normalized.Length.ToString(
                        CultureInfo.InvariantCulture));

                builder.Append(':');
                builder.Append(normalized);
                builder.Append('|');
            }

            builder.AppendLine();
        }
    }
}
