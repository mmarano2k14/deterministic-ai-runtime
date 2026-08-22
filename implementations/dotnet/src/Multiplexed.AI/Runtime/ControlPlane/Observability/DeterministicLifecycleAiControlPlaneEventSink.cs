using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;

namespace Multiplexed.AI.Runtime.ControlPlane.Observability
{
    /// <summary>
    /// Provides the realtime projection used by deterministic lifecycle waits in production tests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This sink is part of the existing Event Manager projection pipeline; it is not a second event bus.
    /// A bounded in-memory history closes the subscribe-after-emission race inside the running process.
    /// </para>
    /// <para>
    /// For durable canonical facts, registered evidence readers are checked before subscription and again
    /// after the waiter is registered. This implements the durable-evidence → subscribe → durable-evidence
    /// race-closing pattern without turning polling into the primary synchronization mechanism.
    /// </para>
    /// </remarks>
    public sealed class DeterministicLifecycleAiControlPlaneEventSink :
        IAiControlPlaneEventProjectionSink,
        IAiDeterministicLifecycleObserver
    {
        private const int HistoryCapacity = 2048;
        private readonly object gate = new();
        private readonly LinkedList<AiControlPlaneEvent> history = new();
        private readonly List<PendingWait> pendingWaits = [];
        private readonly IReadOnlyList<IAiDeterministicLifecycleEvidenceReader> evidenceReaders;

        /// <summary>
        /// Initializes a new instance of the <see cref="DeterministicLifecycleAiControlPlaneEventSink"/> class
        /// without durable evidence readers.
        /// </summary>
        public DeterministicLifecycleAiControlPlaneEventSink()
            : this(Array.Empty<IAiDeterministicLifecycleEvidenceReader>())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DeterministicLifecycleAiControlPlaneEventSink"/> class.
        /// </summary>
        /// <param name="evidenceReaders">Read-only adapters over existing durable observability stores.</param>
        public DeterministicLifecycleAiControlPlaneEventSink(
            IEnumerable<IAiDeterministicLifecycleEvidenceReader> evidenceReaders)
        {
            ArgumentNullException.ThrowIfNull(evidenceReaders);
            this.evidenceReaders = evidenceReaders.ToArray();
        }

        /// <inheritdoc />
        public AiEngineEventProjectionTarget ProjectionTarget => AiEngineEventProjectionTarget.Realtime;

        /// <inheritdoc />
        public Task RecordAsync(
            AiControlPlaneEvent controlPlaneEvent,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(controlPlaneEvent);
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(controlPlaneEvent.SemanticEventType))
            {
                return Task.CompletedTask;
            }

            List<TaskCompletionSource<AiControlPlaneEvent>> completions = [];

            lock (this.gate)
            {
                this.AddToHistory(controlPlaneEvent);

                for (var index = this.pendingWaits.Count - 1; index >= 0; index--)
                {
                    var pendingWait = this.pendingWaits[index];

                    if (!AiDeterministicLifecycleEventMatcher.Matches(controlPlaneEvent, pendingWait.Criteria))
                    {
                        continue;
                    }

                    this.pendingWaits.RemoveAt(index);
                    completions.Add(pendingWait.Completion);
                }
            }

            foreach (var completion in completions)
            {
                completion.TrySetResult(controlPlaneEvent);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task<AiControlPlaneEvent> WaitForAsync(
            AiDeterministicLifecycleEventCriteria criteria,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(criteria);
            ArgumentException.ThrowIfNullOrWhiteSpace(criteria.SemanticEventType);
            cancellationToken.ThrowIfCancellationRequested();

            var projection = AiEngineEventProjectionCatalog.GetRequired(criteria.SemanticEventType);

            if (projection.Realtime == AiEngineEventProjectionRequirement.None)
            {
                throw new InvalidOperationException(
                    $"Canonical engine event '{criteria.SemanticEventType}' is not configured for realtime observation.");
            }

            var durable = projection.Durability != AiEngineEventDurability.TransientObservation;

            if (durable)
            {
                var existingEvidence = await this
                    .TryFindDurableEvidenceAsync(criteria, cancellationToken)
                    .ConfigureAwait(false);

                if (existingEvidence is not null)
                {
                    this.RememberDurableEvidence(existingEvidence);
                    return existingEvidence;
                }
            }

            PendingWait pendingWait;

            lock (this.gate)
            {
                var existingRealtime = this.FindInHistory(criteria);

                if (existingRealtime is not null)
                {
                    return existingRealtime;
                }

                pendingWait = new PendingWait(criteria);
                this.pendingWaits.Add(pendingWait);
            }

            try
            {
                if (durable)
                {
                    var evidenceAfterSubscription = await this
                        .TryFindDurableEvidenceAsync(criteria, cancellationToken)
                        .ConfigureAwait(false);

                    if (evidenceAfterSubscription is not null)
                    {
                        lock (this.gate)
                        {
                            this.pendingWaits.Remove(pendingWait);
                            this.AddToHistory(evidenceAfterSubscription);
                        }

                        pendingWait.Completion.TrySetResult(evidenceAfterSubscription);
                    }
                }

                return await pendingWait.Completion.Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                lock (this.gate)
                {
                    this.pendingWaits.Remove(pendingWait);
                }
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<AiControlPlaneEvent> GetRecentEvents()
        {
            lock (this.gate)
            {
                return this.history.ToArray();
            }
        }

        private async Task<AiControlPlaneEvent?> TryFindDurableEvidenceAsync(
            AiDeterministicLifecycleEventCriteria criteria,
            CancellationToken cancellationToken)
        {
            foreach (var evidenceReader in this.evidenceReaders)
            {
                var evidence = await evidenceReader
                    .FindAsync(criteria, cancellationToken)
                    .ConfigureAwait(false);

                if (evidence is not null && AiDeterministicLifecycleEventMatcher.Matches(evidence, criteria))
                {
                    return evidence;
                }
            }

            return null;
        }

        private void RememberDurableEvidence(AiControlPlaneEvent evidence)
        {
            lock (this.gate)
            {
                this.AddToHistory(evidence);
            }
        }

        private void AddToHistory(AiControlPlaneEvent controlPlaneEvent)
        {
            if (this.history.Any(item => string.Equals(
                    item.EventId,
                    controlPlaneEvent.EventId,
                    StringComparison.Ordinal)))
            {
                return;
            }

            this.history.AddLast(controlPlaneEvent);

            while (this.history.Count > HistoryCapacity)
            {
                this.history.RemoveFirst();
            }
        }

        private AiControlPlaneEvent? FindInHistory(AiDeterministicLifecycleEventCriteria criteria)
        {
            for (var node = this.history.Last; node is not null; node = node.Previous)
            {
                if (AiDeterministicLifecycleEventMatcher.Matches(node.Value, criteria))
                {
                    return node.Value;
                }
            }

            return null;
        }

        private sealed class PendingWait
        {
            public PendingWait(AiDeterministicLifecycleEventCriteria criteria)
            {
                this.Criteria = criteria;
                this.Completion = new TaskCompletionSource<AiControlPlaneEvent>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public AiDeterministicLifecycleEventCriteria Criteria { get; }

            public TaskCompletionSource<AiControlPlaneEvent> Completion { get; }
        }
    }
}
