using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle
{
    /// <summary>
    /// Stores runtime lifecycle events in memory.
    /// </summary>
    public sealed class InMemoryAiRuntimeLifecycleJournal : IAiRuntimeLifecycleJournal
    {
        private readonly ConcurrentDictionary<string, AiRuntimeLifecycleEvent> _events =
            new(StringComparer.Ordinal);

        /// <inheritdoc />
        public Task AppendAsync(
            AiRuntimeLifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalized = AiRuntimeLifecycleEventNormalization.Normalize(lifecycleEvent);

            if (_events.TryAdd(normalized.EventId, normalized))
            {
                return Task.CompletedTask;
            }

            var existing = _events[normalized.EventId];

            if (AiRuntimeLifecycleEventNormalization.AreEquivalent(existing, normalized))
            {
                return Task.CompletedTask;
            }

            throw new InvalidOperationException(
                $"Runtime lifecycle event '{normalized.EventId}' already exists with a different immutable payload.");
        }

        /// <inheritdoc />
        public Task<AiRuntimeLifecycleEvent?> GetByEventIdAsync(
            string eventId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
            cancellationToken.ThrowIfCancellationRequested();

            _events.TryGetValue(eventId, out var lifecycleEvent);

            return Task.FromResult(lifecycleEvent);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByControlPlaneIdAsync(
            string controlPlaneId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);

            return QueryAsync(
                lifecycleEvent => string.Equals(
                    lifecycleEvent.ControlPlaneId,
                    controlPlaneId,
                    StringComparison.Ordinal),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByPoolIdAsync(
            string poolId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);

            return QueryAsync(
                lifecycleEvent => string.Equals(
                    lifecycleEvent.PoolId,
                    poolId,
                    StringComparison.Ordinal),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByHostIdAsync(
            string hostId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(hostId);

            return QueryAsync(
                lifecycleEvent => string.Equals(
                    lifecycleEvent.HostId,
                    hostId,
                    StringComparison.Ordinal),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByKubernetesPodUidAsync(
            string kubernetesPodUid,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(kubernetesPodUid);

            return QueryAsync(
                lifecycleEvent => string.Equals(
                    lifecycleEvent.KubernetesPodUid,
                    kubernetesPodUid,
                    StringComparison.Ordinal),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByRuntimeInstanceIdAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            return QueryAsync(
                lifecycleEvent => string.Equals(
                    lifecycleEvent.RuntimeInstanceId,
                    runtimeInstanceId,
                    StringComparison.Ordinal),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByRuntimeFailureIncidentIdAsync(
            string runtimeFailureIncidentId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeFailureIncidentId);

            return QueryAsync(
                lifecycleEvent => string.Equals(
                    lifecycleEvent.RuntimeFailureIncidentId,
                    runtimeFailureIncidentId,
                    StringComparison.Ordinal),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListBySharedRunIdAsync(
            string tenantId,
            string sharedRunId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);

            return QueryAsync(
                lifecycleEvent =>
                    string.Equals(lifecycleEvent.TenantId, tenantId, StringComparison.Ordinal) &&
                    string.Equals(lifecycleEvent.SharedRunId, sharedRunId, StringComparison.Ordinal),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByExecutionIdAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            return QueryAsync(
                lifecycleEvent => string.Equals(
                    lifecycleEvent.ExecutionId,
                    executionId,
                    StringComparison.Ordinal),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByCorrelationIdAsync(
            string correlationId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

            return QueryAsync(
                lifecycleEvent => string.Equals(
                    lifecycleEvent.CorrelationId,
                    correlationId,
                    StringComparison.Ordinal),
                cancellationToken);
        }

        private Task<IReadOnlyList<AiRuntimeLifecycleEvent>> QueryAsync(
            Func<AiRuntimeLifecycleEvent, bool> predicate,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<AiRuntimeLifecycleEvent> result = _events.Values
                .Where(predicate)
                .OrderBy(lifecycleEvent => lifecycleEvent.TimestampUtc)
                .ThenBy(lifecycleEvent => lifecycleEvent.EventId, StringComparer.Ordinal)
                .ToList();

            return Task.FromResult(result);
        }
    }
}
