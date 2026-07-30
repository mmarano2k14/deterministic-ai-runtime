using System;
using System.Collections.Generic;
using System.Linq;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle
{
    /// <summary>
    /// Normalizes and compares runtime lifecycle events before persistence.
    /// </summary>
    internal static class AiRuntimeLifecycleEventNormalization
    {
        /// <summary>
        /// Validates and normalizes one lifecycle event.
        /// </summary>
        public static AiRuntimeLifecycleEvent Normalize(AiRuntimeLifecycleEvent lifecycleEvent)
        {
            ArgumentNullException.ThrowIfNull(lifecycleEvent);
            ArgumentException.ThrowIfNullOrWhiteSpace(lifecycleEvent.EventId);
            ArgumentException.ThrowIfNullOrWhiteSpace(lifecycleEvent.EventType);
            ArgumentException.ThrowIfNullOrWhiteSpace(lifecycleEvent.ControlPlaneId);

            if (lifecycleEvent.TimestampUtc == default)
            {
                throw new ArgumentException(
                    "Runtime lifecycle events require an explicit non-default UTC timestamp.",
                    nameof(lifecycleEvent));
            }

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in lifecycleEvent.Metadata ?? new Dictionary<string, string>())
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
                ArgumentNullException.ThrowIfNull(pair.Value);
                metadata[pair.Key] = pair.Value;
            }

            return lifecycleEvent with
            {
                Metadata = metadata
            };
        }

        /// <summary>
        /// Determines whether two events represent the same immutable append-only fact.
        /// </summary>
        public static bool AreEquivalent(
            AiRuntimeLifecycleEvent left,
            AiRuntimeLifecycleEvent right)
        {
            ArgumentNullException.ThrowIfNull(left);
            ArgumentNullException.ThrowIfNull(right);

            return string.Equals(left.EventId, right.EventId, StringComparison.Ordinal) &&
                   string.Equals(left.EventType, right.EventType, StringComparison.Ordinal) &&
                   left.TimestampUtc == right.TimestampUtc &&
                   string.Equals(left.ControlPlaneId, right.ControlPlaneId, StringComparison.Ordinal) &&
                   left.HostCreationMode == right.HostCreationMode &&
                   string.Equals(left.ProviderName, right.ProviderName, StringComparison.Ordinal) &&
                   string.Equals(left.PoolId, right.PoolId, StringComparison.Ordinal) &&
                   string.Equals(left.HostId, right.HostId, StringComparison.Ordinal) &&
                   string.Equals(left.KubernetesPodUid, right.KubernetesPodUid, StringComparison.Ordinal) &&
                   string.Equals(left.KubernetesNamespace, right.KubernetesNamespace, StringComparison.Ordinal) &&
                   string.Equals(left.KubernetesPodName, right.KubernetesPodName, StringComparison.Ordinal) &&
                   string.Equals(left.KubernetesNodeName, right.KubernetesNodeName, StringComparison.Ordinal) &&
                   string.Equals(left.RuntimeInstanceId, right.RuntimeInstanceId, StringComparison.Ordinal) &&
                   string.Equals(left.RuntimeId, right.RuntimeId, StringComparison.Ordinal) &&
                   left.ProcessId == right.ProcessId &&
                   string.Equals(left.TenantId, right.TenantId, StringComparison.Ordinal) &&
                   string.Equals(left.TenantGroupId, right.TenantGroupId, StringComparison.Ordinal) &&
                   string.Equals(left.SharedRunId, right.SharedRunId, StringComparison.Ordinal) &&
                   string.Equals(left.LocalRunId, right.LocalRunId, StringComparison.Ordinal) &&
                   string.Equals(left.ExecutionId, right.ExecutionId, StringComparison.Ordinal) &&
                   string.Equals(left.RuntimeFailureIncidentId, right.RuntimeFailureIncidentId, StringComparison.Ordinal) &&
                   string.Equals(left.LedgerEntryId, right.LedgerEntryId, StringComparison.Ordinal) &&
                   string.Equals(left.ForensicsId, right.ForensicsId, StringComparison.Ordinal) &&
                   string.Equals(left.CorrelationId, right.CorrelationId, StringComparison.Ordinal) &&
                   string.Equals(left.CausationId, right.CausationId, StringComparison.Ordinal) &&
                   string.Equals(left.PreviousStatus, right.PreviousStatus, StringComparison.Ordinal) &&
                   string.Equals(left.CurrentStatus, right.CurrentStatus, StringComparison.Ordinal) &&
                   string.Equals(left.Reason, right.Reason, StringComparison.Ordinal) &&
                   MetadataEquals(left.Metadata, right.Metadata);
        }

        private static bool MetadataEquals(
            IReadOnlyDictionary<string, string> left,
            IReadOnlyDictionary<string, string> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            return left.All(pair =>
                right.Any(candidate =>
                    string.Equals(candidate.Key, pair.Key, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.Value, pair.Value, StringComparison.Ordinal)));
        }
    }
}
