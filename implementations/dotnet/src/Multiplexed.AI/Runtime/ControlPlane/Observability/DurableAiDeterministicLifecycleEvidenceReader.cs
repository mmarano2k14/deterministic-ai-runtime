using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery.Observability;

namespace Multiplexed.AI.Runtime.ControlPlane.Observability
{
    /// <summary>
    /// Rehydrates canonical lifecycle events from the existing durable observability stores.
    /// </summary>
    /// <remarks>
    /// The reader is intentionally read-only and reuses the existing Decision Ledger, Runtime Lifecycle
    /// Journal, and Recovery Forensics stores. It exists only to close deterministic-observation races;
    /// it never participates in execution or recovery decisions.
    /// </remarks>
    public sealed class DurableAiDeterministicLifecycleEvidenceReader : IAiDeterministicLifecycleEvidenceReader
    {
        private readonly IReadOnlyList<IAiDecisionLedger> ledgers;
        private readonly IReadOnlyList<IAiRuntimeLifecycleJournal> lifecycleJournals;
        private readonly IReadOnlyList<IAiRuntimeRecoveryForensicsStore> recoveryForensicsStores;

        /// <summary>
        /// Initializes a new instance of the <see cref="DurableAiDeterministicLifecycleEvidenceReader"/> class.
        /// </summary>
        /// <param name="ledgers">The configured existing Decision Ledger implementations.</param>
        /// <param name="lifecycleJournals">The configured existing Runtime Lifecycle Journal implementations.</param>
        /// <param name="recoveryForensicsStores">The configured existing Recovery Forensics stores.</param>
        public DurableAiDeterministicLifecycleEvidenceReader(
            IEnumerable<IAiDecisionLedger> ledgers,
            IEnumerable<IAiRuntimeLifecycleJournal> lifecycleJournals,
            IEnumerable<IAiRuntimeRecoveryForensicsStore> recoveryForensicsStores)
        {
            ArgumentNullException.ThrowIfNull(ledgers);
            ArgumentNullException.ThrowIfNull(lifecycleJournals);
            ArgumentNullException.ThrowIfNull(recoveryForensicsStores);

            this.ledgers = ledgers.ToArray();
            this.lifecycleJournals = lifecycleJournals.ToArray();
            this.recoveryForensicsStores = recoveryForensicsStores.ToArray();
        }

        /// <inheritdoc />
        public async Task<AiControlPlaneEvent?> FindAsync(
            AiDeterministicLifecycleEventCriteria criteria,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(criteria);
            ArgumentException.ThrowIfNullOrWhiteSpace(criteria.SemanticEventType);
            cancellationToken.ThrowIfCancellationRequested();

            var descriptor = AiEngineEventProjectionCatalog.GetRequired(criteria.SemanticEventType);

            if (descriptor.LifecycleJournal != AiEngineEventProjectionRequirement.None)
            {
                foreach (var lifecycleJournal in this.lifecycleJournals)
                {
                    var lifecycleEvidence = await FindLifecycleJournalEvidenceAsync(
                            lifecycleJournal,
                            criteria,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (lifecycleEvidence is not null)
                    {
                        return lifecycleEvidence;
                    }
                }
            }

            if (descriptor.RecoveryForensics != AiEngineEventProjectionRequirement.None)
            {
                foreach (var recoveryForensicsStore in this.recoveryForensicsStores)
                {
                    var recoveryEvidence = await FindRecoveryForensicsEvidenceAsync(
                            recoveryForensicsStore,
                            criteria,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (recoveryEvidence is not null)
                    {
                        return recoveryEvidence;
                    }
                }
            }

            if (descriptor.Ledger != AiEngineEventProjectionRequirement.None)
            {
                foreach (var ledger in this.ledgers)
                {
                    var ledgerEvidence = await FindLedgerEvidenceAsync(
                            ledger,
                            criteria,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (ledgerEvidence is not null)
                    {
                        return ledgerEvidence;
                    }
                }
            }

            return null;
        }

        private static async Task<AiControlPlaneEvent?> FindLifecycleJournalEvidenceAsync(
            IAiRuntimeLifecycleJournal lifecycleJournal,
            AiDeterministicLifecycleEventCriteria criteria,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<AiRuntimeLifecycleEvent> events;

            if (!string.IsNullOrWhiteSpace(criteria.EventId))
            {
                var lifecycleEvent = await lifecycleJournal
                    .GetByEventIdAsync(criteria.EventId, cancellationToken)
                    .ConfigureAwait(false);

                events = lifecycleEvent is null
                    ? Array.Empty<AiRuntimeLifecycleEvent>()
                    : new[] { lifecycleEvent };
            }
            else if (!string.IsNullOrWhiteSpace(criteria.ExecutionId))
            {
                events = await lifecycleJournal
                    .ListByExecutionIdAsync(criteria.ExecutionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (!string.IsNullOrWhiteSpace(criteria.RuntimeInstanceId))
            {
                events = await lifecycleJournal
                    .ListByRuntimeInstanceIdAsync(criteria.RuntimeInstanceId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (!string.IsNullOrWhiteSpace(criteria.CorrelationId))
            {
                events = await lifecycleJournal
                    .ListByCorrelationIdAsync(criteria.CorrelationId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                return null;
            }

            return events
                .Where(item => string.Equals(
                    item.EventType,
                    criteria.SemanticEventType,
                    StringComparison.Ordinal))
                .OrderByDescending(item => item.TimestampUtc)
                .Select(AiRuntimeLifecycleEngineEventFactory.Create)
                .FirstOrDefault(item => AiDeterministicLifecycleEventMatcher.Matches(item, criteria));
        }

        private static async Task<AiControlPlaneEvent?> FindRecoveryForensicsEvidenceAsync(
            IAiRuntimeRecoveryForensicsStore recoveryForensicsStore,
            AiDeterministicLifecycleEventCriteria criteria,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<AiRuntimeRecoveryForensicsRecord> records;

            if (!string.IsNullOrWhiteSpace(criteria.ForensicsId))
            {
                var record = await recoveryForensicsStore
                    .GetByForensicsIdAsync(criteria.ForensicsId, cancellationToken)
                    .ConfigureAwait(false);

                records = record is null
                    ? Array.Empty<AiRuntimeRecoveryForensicsRecord>()
                    : new[] { record };
            }
            else if (!string.IsNullOrWhiteSpace(criteria.ExecutionId))
            {
                records = await recoveryForensicsStore
                    .ListByExecutionIdAsync(criteria.ExecutionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (!string.IsNullOrWhiteSpace(criteria.SharedRunId))
            {
                records = await recoveryForensicsStore
                    .ListBySharedRunIdAsync(criteria.SharedRunId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (!string.IsNullOrWhiteSpace(criteria.RuntimeInstanceId))
            {
                records = await recoveryForensicsStore
                    .ListByRuntimeInstanceIdAsync(criteria.RuntimeInstanceId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                return null;
            }

            return records
                .SelectMany(record => record.Events.Select(evt => CreateRecoveryEvent(record, evt)))
                .Where(item => string.Equals(
                    item.SemanticEventType,
                    criteria.SemanticEventType,
                    StringComparison.Ordinal))
                .OrderByDescending(item => item.TimestampUtc)
                .FirstOrDefault(item => AiDeterministicLifecycleEventMatcher.Matches(item, criteria));
        }

        private static AiControlPlaneEvent CreateRecoveryEvent(
            AiRuntimeRecoveryForensicsRecord record,
            AiRuntimeRecoveryForensicsEvent recoveryEvent)
        {
            return AiRecoveryEngineEventFactory.Create(
                semanticEventType: recoveryEvent.EventType,
                eventId: recoveryEvent.EventId,
                forensicsId: recoveryEvent.ForensicsId,
                timestampUtc: recoveryEvent.TimestampUtc,
                outcome: recoveryEvent.Outcome,
                reason: recoveryEvent.Reason,
                executionId: recoveryEvent.ExecutionId,
                sharedRunId: recoveryEvent.SharedRunId ?? record.Identity.SharedRunId,
                localRunId: recoveryEvent.LocalRunId,
                runtimeInstanceId: recoveryEvent.RuntimeInstanceId,
                metadata: recoveryEvent.Metadata);
        }

        private static async Task<AiControlPlaneEvent?> FindLedgerEvidenceAsync(
            IAiDecisionLedger ledger,
            AiDeterministicLifecycleEventCriteria criteria,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(criteria.ExecutionId) &&
                string.IsNullOrWhiteSpace(criteria.RunId) &&
                string.IsNullOrWhiteSpace(criteria.RuntimeInstanceId) &&
                string.IsNullOrWhiteSpace(criteria.CorrelationId))
            {
                // The existing Ledger API has no direct EventId lookup. Avoid an unbounded global scan;
                // deterministic durable Ledger waits should provide at least one existing correlation identity.
                return null;
            }

            var entries = await ledger
                .QueryAsync(
                    new AiDecisionLedgerQuery
                    {
                        ExecutionId = criteria.ExecutionId,
                        RunId = criteria.RunId,
                        EventType = criteria.SemanticEventType,
                        RuntimeInstanceId = criteria.RuntimeInstanceId,
                        CorrelationId = criteria.CorrelationId,
                        Limit = 128
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return entries
                .OrderByDescending(item => item.TimestampUtc)
                .Select(CreateLedgerEvent)
                .FirstOrDefault(item => AiDeterministicLifecycleEventMatcher.Matches(item, criteria));
        }

        private static AiControlPlaneEvent CreateLedgerEvent(AiDecisionLedgerEntry entry)
        {
            var metadata = entry.Metadata ?? new Dictionary<string, string>();
            var eventId = TryGetMetadata(metadata, "event.id") ?? entry.EntryId;
            var correlationId = entry.CorrelationContext.CorrelationId ?? eventId;
            var properties = metadata.ToDictionary(
                pair => pair.Key,
                pair => (object?)pair.Value,
                StringComparer.Ordinal);

            return new AiControlPlaneEvent
            {
                EventId = eventId,
                SemanticEventType = entry.EventType,
                EventType = ResolveEnvelopeType(entry),
                Area = ResolveArea(entry, metadata),
                Operation = TryGetMetadata(metadata, "operation")
                    ?? entry.CorrelationContext.Operation
                    ?? entry.EventType,
                Outcome = ResolveOperationOutcome(entry),
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = correlationId,
                    RunId = entry.CorrelationContext.RunId,
                    ExecutionId = entry.CorrelationContext.ExecutionId,
                    PipelineName = entry.CorrelationContext.PipelineName,
                    PipelineVersion = entry.CorrelationContext.PipelineVersion,
                    RuntimeInstanceId = entry.CorrelationContext.RuntimeInstanceId,
                    WorkerId = entry.CorrelationContext.WorkerId
                },
                CausationId = TryGetMetadata(metadata, "event.causationId"),
                TimestampUtc = entry.TimestampUtc,
                DurationMs = TryParseLong(TryGetMetadata(metadata, "duration.ms")),
                Message = entry.Reason,
                FailureReason = entry.Outcome == AiDecisionLedgerOutcome.Failed
                    ? entry.Reason
                    : null,
                Properties = properties
            };
        }

        private static AiControlPlaneEventType ResolveEnvelopeType(AiDecisionLedgerEntry entry)
        {
            var persisted = TryGetMetadata(entry.Metadata, "event.type");

            if (Enum.TryParse<AiControlPlaneEventType>(persisted, ignoreCase: true, out var eventType))
            {
                return eventType;
            }

            return entry.Outcome switch
            {
                AiDecisionLedgerOutcome.Started => AiControlPlaneEventType.OperationStarted,
                AiDecisionLedgerOutcome.Failed => AiControlPlaneEventType.OperationFailed,
                AiDecisionLedgerOutcome.Denied or AiDecisionLedgerOutcome.Blocked => AiControlPlaneEventType.OperationDenied,
                _ => AiControlPlaneEventType.OperationCompleted
            };
        }

        private static AiControlPlaneOperationOutcome? ResolveOperationOutcome(AiDecisionLedgerEntry entry)
        {
            var persisted = TryGetMetadata(entry.Metadata, "outcome");

            if (Enum.TryParse<AiControlPlaneOperationOutcome>(persisted, ignoreCase: true, out var outcome))
            {
                return outcome;
            }

            return entry.Outcome switch
            {
                AiDecisionLedgerOutcome.Failed => AiControlPlaneOperationOutcome.Failed,
                AiDecisionLedgerOutcome.Denied or AiDecisionLedgerOutcome.Blocked => AiControlPlaneOperationOutcome.Denied,
                AiDecisionLedgerOutcome.CompletedWithIssues => AiControlPlaneOperationOutcome.CompletedWithIssues,
                AiDecisionLedgerOutcome.None or AiDecisionLedgerOutcome.Started => null,
                _ => AiControlPlaneOperationOutcome.Succeeded
            };
        }

        private static AiControlPlaneArea ResolveArea(
            AiDecisionLedgerEntry entry,
            IReadOnlyDictionary<string, string> metadata)
        {
            var persisted = TryGetMetadata(metadata, "area");

            if (Enum.TryParse<AiControlPlaneArea>(persisted, ignoreCase: true, out var area))
            {
                return area;
            }

            return entry.Category switch
            {
                AiDecisionLedgerCategory.Replay => AiControlPlaneArea.Replay,
                AiDecisionLedgerCategory.Control => AiControlPlaneArea.ExecutionControl,
                AiDecisionLedgerCategory.Run => AiControlPlaneArea.RunControl,
                AiDecisionLedgerCategory.RuntimeInstance => AiControlPlaneArea.InstanceRegistry,
                AiDecisionLedgerCategory.Admission => AiControlPlaneArea.Admission,
                AiDecisionLedgerCategory.Queue => AiControlPlaneArea.SharedQueue,
                AiDecisionLedgerCategory.SharedController => AiControlPlaneArea.SharedController,
                AiDecisionLedgerCategory.Scaling => AiControlPlaneArea.Scaling,
                AiDecisionLedgerCategory.Recovery => AiControlPlaneArea.Recovery,
                AiDecisionLedgerCategory.Dag => AiControlPlaneArea.ChildDag,
                AiDecisionLedgerCategory.Policy => AiControlPlaneArea.Policy,
                _ => AiControlPlaneArea.ExecutionControl
            };
        }

        private static string? TryGetMetadata(
            IReadOnlyDictionary<string, string>? metadata,
            string key)
        {
            return metadata is not null && metadata.TryGetValue(key, out var value)
                ? value
                : null;
        }

        private static long? TryParseLong(string? value)
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }
    }
}
