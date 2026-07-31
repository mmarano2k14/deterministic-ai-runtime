using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle
{
    /// <summary>
    /// Appends idempotent lifecycle facts and resolves durable infrastructure identity
    /// from previously recorded lifecycle events.
    /// </summary>
    public sealed class AiRuntimeLifecycleEventWriter
    {
        private const string FallbackControlPlaneId = "runtime-lifecycle";
        private readonly IAiRuntimeLifecycleJournal journal;

        public AiRuntimeLifecycleEventWriter(
            IAiRuntimeLifecycleJournal journal)
        {
            this.journal = journal
                ?? throw new ArgumentNullException(nameof(journal));
        }

        /// <summary>
        /// Appends one immutable event unless the same stable event id already exists.
        /// </summary>
        public async Task AppendOnceAsync(
            AiRuntimeLifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(lifecycleEvent);
            cancellationToken.ThrowIfCancellationRequested();

            var existing = await this.journal
                .GetByEventIdAsync(
                    lifecycleEvent.EventId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                return;
            }

            await this.journal
                .AppendAsync(lifecycleEvent, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Resolves the latest known typed infrastructure identity for a runtime, host, or pool.
        /// </summary>
        public async Task<AiRuntimeLifecycleInfrastructureContext> ResolveContextAsync(
            string? runtimeInstanceId,
            string? hostId,
            string? poolId,
            string? fallbackControlPlaneId = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var events = new List<AiRuntimeLifecycleEvent>();

            if (!string.IsNullOrWhiteSpace(runtimeInstanceId))
            {
                events.AddRange(
                    await this.journal
                        .ListByRuntimeInstanceIdAsync(
                            runtimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false));
            }

            if (!string.IsNullOrWhiteSpace(hostId))
            {
                events.AddRange(
                    await this.journal
                        .ListByHostIdAsync(
                            hostId,
                            cancellationToken)
                        .ConfigureAwait(false));
            }

            if (!string.IsNullOrWhiteSpace(poolId))
            {
                events.AddRange(
                    await this.journal
                        .ListByPoolIdAsync(
                            poolId,
                            cancellationToken)
                        .ConfigureAwait(false));
            }

            var ordered = events
                .GroupBy(item => item.EventId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderByDescending(item => item.TimestampUtc)
                .ThenByDescending(item => item.EventId, StringComparer.Ordinal)
                .ToArray();

            return new AiRuntimeLifecycleInfrastructureContext
            {
                ControlPlaneId = FirstNonEmpty(
                    ordered.Select(item => item.ControlPlaneId))
                    ?? fallbackControlPlaneId
                    ?? FallbackControlPlaneId,
                HostCreationMode = ordered
                    .Select(item => item.HostCreationMode)
                    .FirstOrDefault(value => value.HasValue),
                ProviderName = FirstNonEmpty(
                    ordered.Select(item => item.ProviderName)),
                PoolId = FirstNonEmpty(
                    ordered.Select(item => item.PoolId))
                    ?? poolId,
                HostId = FirstNonEmpty(
                    ordered.Select(item => item.HostId))
                    ?? hostId,
                KubernetesPodUid = FirstNonEmpty(
                    ordered.Select(item => item.KubernetesPodUid)),
                KubernetesNamespace = FirstNonEmpty(
                    ordered.Select(item => item.KubernetesNamespace)),
                KubernetesPodName = FirstNonEmpty(
                    ordered.Select(item => item.KubernetesPodName)),
                KubernetesNodeName = FirstNonEmpty(
                    ordered.Select(item => item.KubernetesNodeName)),
                RuntimeInstanceId = FirstNonEmpty(
                    ordered.Select(item => item.RuntimeInstanceId))
                    ?? runtimeInstanceId,
                RuntimeId = FirstNonEmpty(
                    ordered.Select(item => item.RuntimeId)),
                ProcessId = ordered
                    .Select(item => item.ProcessId)
                    .FirstOrDefault(value => value.HasValue),
                CorrelationId = FirstNonEmpty(
                    ordered.Select(item => item.CorrelationId))
            };
        }

        /// <summary>
        /// Creates a stable append-only event id for one incident subject and event type.
        /// </summary>
        public static string CreateEventId(
            string eventType,
            string subjectId,
            string? runtimeFailureIncidentId = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
            ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);

            return string.Join(
                ":",
                eventType.Trim(),
                string.IsNullOrWhiteSpace(runtimeFailureIncidentId)
                    ? "lifecycle"
                    : runtimeFailureIncidentId.Trim(),
                subjectId.Trim());
        }

        private static string? FirstNonEmpty(
            IEnumerable<string?> values)
        {
            return values.FirstOrDefault(
                value => !string.IsNullOrWhiteSpace(value));
        }
    }

    /// <summary>
    /// Represents typed infrastructure identity reconstructed from durable lifecycle events.
    /// </summary>
    public sealed record AiRuntimeLifecycleInfrastructureContext
    {
        public required string ControlPlaneId { get; init; }
        public AiRuntimeHostCreationMode? HostCreationMode { get; init; }
        public string? ProviderName { get; init; }
        public string? PoolId { get; init; }
        public string? HostId { get; init; }
        public string? KubernetesPodUid { get; init; }
        public string? KubernetesNamespace { get; init; }
        public string? KubernetesPodName { get; init; }
        public string? KubernetesNodeName { get; init; }
        public string? RuntimeInstanceId { get; init; }
        public string? RuntimeId { get; init; }
        public int? ProcessId { get; init; }
        public string? CorrelationId { get; init; }
    }
}
