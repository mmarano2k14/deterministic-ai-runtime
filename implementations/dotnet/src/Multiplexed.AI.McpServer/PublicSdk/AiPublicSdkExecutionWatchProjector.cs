using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag;
using Multiplexed.Abstractions.AI.Observability.Events;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Sdk.Contracts.Executions;
using Multiplexed.AI.Sdk.Contracts.Observation;
using Multiplexed.AI.Sdk.Contracts.Watch;

namespace Multiplexed.AI.McpServer.PublicSdk
{
    /// <summary>
    /// Projects the existing durable Decision Ledger into the versioned public execution-watch protocol.
    /// The ledger remains an internal audit source; its sequence numbers, metadata and runtime identities are
    /// never exposed directly. Public sequence numbers are assigned only to explicitly mapped public events.
    /// </summary>
    public sealed class AiPublicSdkExecutionWatchProjector
    {
        private const int MaximumLedgerReadBatchSize = 4096;
        private readonly IAiDecisionLedger _ledger;
        private readonly int _retainedPublicEventLimit;
        private readonly int _ledgerReadBatchSize;
        private readonly TimeSpan _pollInterval;

        public AiPublicSdkExecutionWatchProjector(
            IAiDecisionLedger ledger,
            AiPublicSdkExecutionWatchOptions? options = null)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            var resolvedOptions = options ?? new AiPublicSdkExecutionWatchOptions();
            if (resolvedOptions.RetainedPublicEventLimit <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "RetainedPublicEventLimit must be greater than zero.");
            }

            if (resolvedOptions.LedgerReadBatchSize <= 0 ||
                resolvedOptions.LedgerReadBatchSize > MaximumLedgerReadBatchSize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    $"LedgerReadBatchSize must be between 1 and {MaximumLedgerReadBatchSize}.");
            }

            if (resolvedOptions.PollInterval < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "PollInterval must not be negative.");
            }

            _retainedPublicEventLimit = resolvedOptions.RetainedPublicEventLimit;
            _ledgerReadBatchSize = resolvedOptions.LedgerReadBatchSize;
            _pollInterval = resolvedOptions.PollInterval;
        }

        /// <summary>
        /// Gets the current public sequence boundary for one execution. Internal ledger entries that do not map
        /// to the public Watch taxonomy do not consume public sequence numbers. The scan is always paged and
        /// never materializes the full execution ledger in memory.
        /// </summary>
        public async Task<long> GetLatestSequenceAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            long publicSequence = 0;
            long internalSequence = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entries = await ReadLedgerBatchAsync(
                        executionId,
                        internalSequence + 1,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (entries.Count == 0) return publicSequence;

                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    internalSequence = Math.Max(internalSequence, entry.Sequence);
                    if (TryProject(entry, out _)) publicSequence++;
                }

                if (entries.Count < _ledgerReadBatchSize) return publicSequence;
            }
        }

        /// <summary>
        /// Waits for the next public event after <paramref name="afterSequence"/>. The wait is request-scoped:
        /// cancelling the HTTP/MCP request only stops this observation and never changes durable execution state.
        /// No per-subscriber event queue is maintained. Each request holds at most one projected candidate plus
        /// one bounded ledger batch; a consumer that falls behind the public retention window must resynchronize.
        /// </summary>
        public async Task<AiSdkExecutionWatchEvent> WaitForNextAsync(
            string executionId,
            IReadOnlyList<AiSdkExecutionWatchChannel> channels,
            long afterSequence,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentNullException.ThrowIfNull(channels);

            var selectedChannels = channels.Count == 0
                ? null
                : new HashSet<AiSdkExecutionWatchChannel>(channels);

            long publicSequence = 0;
            long internalSequence = 0;
            AiSdkExecutionWatchEvent? firstDeliverable = null;

            // Initial scan is paged and constant-memory. We continue to the current high-water even after finding
            // a candidate so cursor retention can be validated against the actual latest public sequence before
            // any event is returned.
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entries = await ReadLedgerBatchAsync(
                        executionId,
                        internalSequence + 1,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (entries.Count == 0) break;

                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    internalSequence = Math.Max(internalSequence, entry.Sequence);

                    if (!TryProject(entry, out var projection)) continue;

                    publicSequence++;
                    if (firstDeliverable is null &&
                        publicSequence > afterSequence &&
                        ShouldDeliver(projection, selectedChannels))
                    {
                        firstDeliverable = ToPublicEvent(
                            executionId,
                            publicSequence,
                            entry,
                            projection);
                    }
                }

                if (entries.Count < _ledgerReadBatchSize) break;
            }

            var cursorFailure = ValidateCursor(executionId, afterSequence, publicSequence);
            if (cursorFailure is not null) return cursorFailure;
            if (firstDeliverable is not null) return firstDeliverable;

            // A slow consumer does not create server-side buffered state. New entries are read in bounded pages;
            // once its public cursor falls outside the retention window, the request converges to ResyncRequired.
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                AiSdkExecutionWatchEvent? nextDeliverable = null;

                while (true)
                {
                    var newEntries = await ReadLedgerBatchAsync(
                            executionId,
                            internalSequence + 1,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (newEntries.Count == 0) break;

                    foreach (var entry in newEntries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        internalSequence = Math.Max(internalSequence, entry.Sequence);

                        if (!TryProject(entry, out var projection)) continue;

                        publicSequence++;

                        // Once the lag itself proves that the cursor is outside the retained public window,
                        // no additional scan is required to make the slow-consumer decision.
                        var retentionFailure = ValidateRetentionCursor(
                            executionId,
                            afterSequence,
                            publicSequence);
                        if (retentionFailure is not null) return retentionFailure;

                        if (nextDeliverable is null &&
                            publicSequence > afterSequence &&
                            ShouldDeliver(projection, selectedChannels))
                        {
                            nextDeliverable = ToPublicEvent(
                                executionId,
                                publicSequence,
                                entry,
                                projection);
                        }
                    }

                    // A short batch establishes the current ledger high-water. Full batches are drained before
                    // returning the candidate so retention is evaluated against all immediately available history.
                    if (newEntries.Count < _ledgerReadBatchSize) break;
                }

                if (nextDeliverable is not null) return nextDeliverable;

                await DelayBeforeNextPollAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private Task<IReadOnlyList<AiDecisionLedgerEntry>> ReadLedgerBatchAsync(
            string executionId,
            long sequenceFrom,
            CancellationToken cancellationToken) =>
            _ledger.QueryAsync(
                new AiDecisionLedgerQuery
                {
                    ExecutionId = executionId,
                    SequenceFrom = sequenceFrom,
                    Limit = _ledgerReadBatchSize
                },
                cancellationToken);

        private Task DelayBeforeNextPollAsync(CancellationToken cancellationToken)
        {
            if (_pollInterval == TimeSpan.Zero)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            return Task.Delay(_pollInterval, cancellationToken);
        }

        private AiSdkExecutionWatchEvent? ValidateCursor(
            string executionId,
            long afterSequence,
            long latestSequence)
        {
            if (afterSequence < 0 || afterSequence > latestSequence)
            {
                return CreateInvalidCursor(executionId, afterSequence, latestSequence);
            }

            return ValidateRetentionCursor(executionId, afterSequence, latestSequence);
        }

        private AiSdkExecutionWatchEvent? ValidateRetentionCursor(
            string executionId,
            long afterSequence,
            long latestSequence)
        {
            if (latestSequence <= 0) return null;

            var earliestAvailableSequence = Math.Max(1, latestSequence - _retainedPublicEventLimit + 1);
            var earliestResumableCursor = earliestAvailableSequence - 1;
            if (afterSequence >= earliestResumableCursor) return null;

            return CreateResyncRequired(
                executionId,
                AiSdkExecutionWatchResyncReason.HistoryUnavailable,
                afterSequence,
                earliestAvailableSequence,
                latestSequence,
                "The requested public Watch cursor is older than the retained public event window.");
        }

        private static bool ShouldDeliver(
            Projection projection,
            HashSet<AiSdkExecutionWatchChannel>? selectedChannels)
        {
            // Terminal lifecycle facts always converge the stream, even when a caller selected a narrower
            // diagnostic channel set. Without this rule a policy-only or step-only Watch could never terminate.
            return projection.IsTerminal ||
                selectedChannels is null ||
                selectedChannels.Contains(projection.Channel);
        }

        private static AiSdkExecutionWatchEvent ToPublicEvent(
            string executionId,
            long publicSequence,
            AiDecisionLedgerEntry entry,
            Projection projection)
        {
            return new AiSdkExecutionWatchEvent
            {
                ExecutionId = executionId,
                Sequence = publicSequence,
                Kind = AiSdkExecutionWatchEventKind.Event,
                OccurredAtUtc = entry.TimestampUtc,
                Channel = projection.Channel,
                EventType = projection.EventType,
                PayloadSchemaVersion = projection.Payload.HasValue ? 1 : null,
                Payload = projection.Payload
            };
        }

        private static AiSdkExecutionWatchEvent CreateInvalidCursor(
            string executionId,
            long requestedAfterSequence,
            long latestSequence) =>
            CreateResyncRequired(
                executionId,
                AiSdkExecutionWatchResyncReason.InvalidCursor,
                requestedAfterSequence,
                latestSequence > 0 ? 1 : null,
                latestSequence,
                "The requested public Watch cursor is outside the available execution stream.");

        private static AiSdkExecutionWatchEvent CreateResyncRequired(
            string executionId,
            AiSdkExecutionWatchResyncReason reason,
            long? requestedAfterSequence,
            long? earliestAvailableSequence,
            long? latestSequence,
            string message)
        {
            return new AiSdkExecutionWatchEvent
            {
                ExecutionId = executionId,
                Kind = AiSdkExecutionWatchEventKind.ResyncRequired,
                OccurredAtUtc = DateTimeOffset.UtcNow,
                ResyncRequired = new AiSdkExecutionWatchResyncRequired
                {
                    Reason = reason,
                    RequestedAfterSequence = requestedAfterSequence,
                    EarliestAvailableSequence = earliestAvailableSequence,
                    LatestSequence = latestSequence,
                    Message = message
                }
            };
        }

        /// <summary>
        /// Maps one internal ledger entry to a deliberately small public semantic event. Unknown or internal-only
        /// events are ignored. Adding a new mapping changes the v1 public sequence space and therefore requires
        /// explicit protocol/version review rather than automatic serialization of new runtime event types.
        /// </summary>
        private static bool TryProject(
            AiDecisionLedgerEntry entry,
            out Projection projection)
        {
            ArgumentNullException.ThrowIfNull(entry);

            projection = default;

            if (string.Equals(entry.EventType, AiEngineEvents.Execution.Created, StringComparison.Ordinal))
            {
                projection = new Projection(
                    AiSdkExecutionWatchChannel.Lifecycle,
                    "execution.submitted",
                    Json(new { status = AiSdkExecutionStatus.Pending }),
                    IsTerminal: false);
                return true;
            }

            if (string.Equals(entry.EventType, AiEngineEvents.Execution.Started, StringComparison.Ordinal))
            {
                projection = new Projection(
                    AiSdkExecutionWatchChannel.Lifecycle,
                    "execution.running",
                    Json(new { status = AiSdkExecutionStatus.Running }),
                    IsTerminal: false);
                return true;
            }

            if (string.Equals(entry.EventType, AiEngineEvents.Execution.Completed, StringComparison.Ordinal))
            {
                projection = new Projection(
                    AiSdkExecutionWatchChannel.Lifecycle,
                    AiEngineEvents.Execution.Completed,
                    Json(new { status = AiSdkExecutionStatus.Completed }),
                    IsTerminal: true);
                return true;
            }

            if (string.Equals(entry.EventType, AiEngineEvents.Execution.Failed, StringComparison.Ordinal))
            {
                projection = new Projection(
                    AiSdkExecutionWatchChannel.Lifecycle,
                    AiEngineEvents.Execution.Failed,
                    Json(new { status = AiSdkExecutionStatus.Failed }),
                    IsTerminal: true);
                return true;
            }

            if (string.Equals(entry.EventType, AiEngineEvents.Execution.Cancelled, StringComparison.Ordinal))
            {
                projection = new Projection(
                    AiSdkExecutionWatchChannel.Lifecycle,
                    AiEngineEvents.Execution.Cancelled,
                    Json(new { status = AiSdkExecutionStatus.Cancelled }),
                    IsTerminal: true);
                return true;
            }

            if (TryProjectStep(entry, out projection)) return true;
            if (TryProjectPolicy(entry, out projection)) return true;
            if (TryProjectChild(entry, out projection)) return true;
            if (TryProjectRecovery(entry, out projection)) return true;

            // Human-input facts remain internal in the v1 Watch contract because no dedicated public channel
            // has been versioned for them. A parked step already exposes the generic WaitingForExternal state.
            // Effects are also intentionally not synthesized from invocation/evidence internals here: their
            // durable owner is the effect-evidence store, not this Decision Ledger cursor. They can be exposed
            // only after a durable public effect source has a proven cursor/ordering integration.
            return false;
        }

        private static bool TryProjectStep(
            AiDecisionLedgerEntry entry,
            out Projection projection)
        {
            projection = default;

            string? eventType = entry.EventType switch
            {
                var value when string.Equals(value, AiEngineEvents.Step.Started, StringComparison.Ordinal) => AiEngineEvents.Step.Started,
                var value when string.Equals(value, AiEngineEvents.Step.Parked, StringComparison.Ordinal) => AiEngineEvents.Step.Parked,
                var value when string.Equals(value, AiEngineEvents.Step.Completed, StringComparison.Ordinal) => AiEngineEvents.Step.Completed,
                var value when string.Equals(value, AiEngineEvents.Step.Failed, StringComparison.Ordinal) => AiEngineEvents.Step.Failed,
                _ => null
            };

            if (eventType is null) return false;

            var name = Metadata(entry, AiStepMetadataKeys.StepName)
                ?? entry.CorrelationContext.StepId
                ?? entry.CorrelationContext.StepKey;
            var stepKey = Metadata(entry, AiStepMetadataKeys.StepKey)
                ?? entry.CorrelationContext.StepKey;

            if (string.IsNullOrWhiteSpace(name)) return false;

            var status = eventType switch
            {
                AiEngineEvents.Step.Started => AiSdkExecutionStepStatus.Running,
                AiEngineEvents.Step.Parked => AiSdkExecutionStepStatus.WaitingForExternal,
                AiEngineEvents.Step.Completed => AiSdkExecutionStepStatus.Completed,
                _ => AiSdkExecutionStepStatus.Failed
            };

            projection = new Projection(
                AiSdkExecutionWatchChannel.Steps,
                eventType,
                Json(new
                {
                    name,
                    stepKey = string.IsNullOrWhiteSpace(stepKey) ? null : stepKey,
                    status
                }),
                IsTerminal: false);
            return true;
        }

        private static bool TryProjectPolicy(
            AiDecisionLedgerEntry entry,
            out Projection projection)
        {
            projection = default;

            string? eventType = entry.EventType switch
            {
                var value when string.Equals(value, AiEngineEvents.Policy.Evaluated, StringComparison.Ordinal) => AiEngineEvents.Policy.Evaluated,
                var value when string.Equals(value, AiEngineEvents.Policy.Allowed, StringComparison.Ordinal) => AiEngineEvents.Policy.Allowed,
                var value when string.Equals(value, AiEngineEvents.Policy.Denied, StringComparison.Ordinal) => AiEngineEvents.Policy.Denied,
                var value when string.Equals(value, AiEngineEvents.Policy.Failed, StringComparison.Ordinal) => AiEngineEvents.Policy.Failed,
                _ => null
            };

            // policy.skipped is deliberately not public until a validated deliberate-skip semantic exists.
            if (eventType is null) return false;

            var policyName = entry.CorrelationContext.PolicyKey
                ?? Metadata(entry, AiPolicyMetadataKeys.Name);
            var stepName = Metadata(entry, AiStepMetadataKeys.StepName)
                ?? entry.CorrelationContext.StepId;

            projection = new Projection(
                AiSdkExecutionWatchChannel.Policies,
                eventType,
                Json(new
                {
                    policy = string.IsNullOrWhiteSpace(policyName) ? null : policyName,
                    step = string.IsNullOrWhiteSpace(stepName) ? null : stepName,
                    decision = eventType["policy.".Length..]
                }),
                IsTerminal: false);
            return true;
        }

        private static bool TryProjectChild(
            AiDecisionLedgerEntry entry,
            out Projection projection)
        {
            projection = default;

            string? eventType = entry.EventType switch
            {
                var value when string.Equals(value, AiEngineEvents.ChildDag.ExecutionCreated, StringComparison.Ordinal) => "child.created",
                var value when string.Equals(value, AiEngineEvents.ChildDag.ExecutionStarted, StringComparison.Ordinal) => "child.started",
                var value when string.Equals(value, AiEngineEvents.ChildDag.ExecutionCompleted, StringComparison.Ordinal) => "child.completed",
                var value when string.Equals(value, AiEngineEvents.ChildDag.ExecutionFailed, StringComparison.Ordinal) => "child.failed",
                _ => null
            };

            if (eventType is null) return false;

            var childExecutionId = Metadata(entry, AiChildDagMetadataKeys.ExecutionId)
                ?? entry.CorrelationContext.ExecutionId;
            var parentExecutionId = Metadata(entry, AiChildDagMetadataKeys.ParentExecutionId);
            var callSiteId = Metadata(entry, AiChildDagMetadataKeys.ParentCallSiteId);

            projection = new Projection(
                AiSdkExecutionWatchChannel.Children,
                eventType,
                Json(new
                {
                    executionId = childExecutionId,
                    parentExecutionId = string.IsNullOrWhiteSpace(parentExecutionId) ? null : parentExecutionId,
                    callSiteId = string.IsNullOrWhiteSpace(callSiteId) ? null : callSiteId
                }),
                IsTerminal: false);
            return true;
        }

        private static bool TryProjectRecovery(
            AiDecisionLedgerEntry entry,
            out Projection projection)
        {
            projection = default;

            string? eventType = entry.EventType switch
            {
                var value when string.Equals(value, AiEngineEvents.Recovery.Detected, StringComparison.Ordinal) => "recovery.started",
                var value when string.Equals(value, AiEngineEvents.Recovery.Applied, StringComparison.Ordinal) => "recovery.resumed",
                var value when string.Equals(value, AiEngineEvents.Recovery.StepRecovered, StringComparison.Ordinal) => "recovery.resumed",
                var value when string.Equals(value, AiEngineEvents.Recovery.ExecutionRecovered, StringComparison.Ordinal) => "recovery.completed",
                _ => null
            };

            if (eventType is null) return false;

            projection = new Projection(
                AiSdkExecutionWatchChannel.Recovery,
                eventType,
                Payload: null,
                IsTerminal: false);
            return true;
        }

        private static string? Metadata(AiDecisionLedgerEntry entry, string key)
        {
            if (entry.Metadata is null ||
                !entry.Metadata.TryGetValue(key, out var value) ||
                string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value;
        }

        private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

        private readonly record struct Projection(
            AiSdkExecutionWatchChannel Channel,
            string EventType,
            JsonElement? Payload,
            bool IsTerminal);
    }
}
