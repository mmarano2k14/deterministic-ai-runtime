using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Calculates deterministic fingerprints for one exact failed Pod membership and inventory.
    /// </summary>
    public static class AiKubernetesRuntimePoolPodRecoveryInventoryFingerprint
    {
        /// <summary>
        /// Calculates shared-registry membership without local route identities.
        /// </summary>
        public static string CalculateMembership(
            AiKubernetesRuntimePoolPodAssignedWorkInventory inventory)
        {
            ArgumentNullException.ThrowIfNull(inventory);

            var builder = new StringBuilder();
            Append(builder, inventory.PoolId, inventory.PodUid);

            foreach (var runtimeInventory in
                inventory.RuntimeInventories
                    .OrderBy(
                        item => item.RuntimeInstanceId,
                        StringComparer.Ordinal))
            {
                Append(builder, runtimeInventory.RuntimeInstanceId);
            }

            return CalculateHash(builder);
        }

        public static string CalculateInventory(
            AiKubernetesRuntimePoolPodAssignedWorkInventory inventory)
        {
            ArgumentNullException.ThrowIfNull(inventory);

            var builder = new StringBuilder();

            Append(
                builder,
                inventory.FailureId,
                inventory.PoolId,
                inventory.PodUid,
                CalculateMembership(inventory),
                inventory.RuntimeInventories.Count.ToString(
                    CultureInfo.InvariantCulture),
                inventory.Candidates.Count.ToString(
                    CultureInfo.InvariantCulture));

            foreach (var runtimeInventory in
                inventory.RuntimeInventories
                    .OrderBy(
                        item => item.RuntimeInstanceId,
                        StringComparer.Ordinal))
            {
                Append(
                    builder,
                    runtimeInventory.RuntimeInstanceId,
                    AiRuntimePoolRecoveryInventoryFingerprint
                        .Calculate(runtimeInventory));
            }

            return CalculateHash(builder);
        }

        private static string CalculateHash(StringBuilder builder)
        {
            var hash =
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(builder.ToString()));

            return Convert.ToHexString(hash)
                .ToLowerInvariant();
        }

        private static void Append(
            StringBuilder builder,
            params string?[] values)
        {
            foreach (var value in values)
            {
                var normalized = value ?? string.Empty;
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
