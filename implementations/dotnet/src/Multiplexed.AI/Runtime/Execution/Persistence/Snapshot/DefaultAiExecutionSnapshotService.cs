using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Persistence.Snapshot;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.AI.Runtime.Execution.Persistence.Snapshot.Normalization;
using Multiplexed.AI.Runtime.Observability.Helpers;
using Multiplexed.Abstractions.AI.Execution.Context;

namespace Multiplexed.AI.Runtime.Execution.Persistence.Snapshot
{
    /// <summary>
    /// Default implementation of <see cref="IAiExecutionSnapshotService{TContextSnapshot}"/>.
    /// </summary>
    /// <typeparam name="TContextSnapshot">
    /// The serializable external context snapshot associated with the execution.
    /// </typeparam>
    public sealed class DefaultAiExecutionSnapshotService<TContextSnapshot>
        : IAiExecutionSnapshotService<TContextSnapshot>
    {
        private const string SnapshotPipelineKey = "execution-snapshot";
        private const string SnapshotStepName = "_snapshot";
        private const string SnapshotWorkerId = "snapshot-service";

        private readonly IAiExecutionSnapshotFactory<TContextSnapshot> _factory;
        private readonly IAiExecutionSnapshotStore<TContextSnapshot> _store;
        private readonly IAiRuntimeObservability? _observability;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultAiExecutionSnapshotService{TContextSnapshot}"/> class.
        /// </summary>
        /// <param name="factory">Snapshot factory responsible for document creation.</param>
        /// <param name="store">Snapshot store responsible for persistence.</param>
        public DefaultAiExecutionSnapshotService(
            IAiExecutionSnapshotFactory<TContextSnapshot> factory,
            IAiExecutionSnapshotStore<TContextSnapshot> store)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultAiExecutionSnapshotService{TContextSnapshot}"/> class.
        /// </summary>
        /// <param name="factory">Snapshot factory responsible for document creation.</param>
        /// <param name="store">Snapshot store responsible for persistence.</param>
        /// <param name="observability">The runtime observability facade.</param>
        public DefaultAiExecutionSnapshotService(
            IAiExecutionSnapshotFactory<TContextSnapshot> factory,
            IAiExecutionSnapshotStore<TContextSnapshot> store,
            IAiRuntimeObservability observability)
            : this(factory, store)
        {
            _observability = observability ?? throw new ArgumentNullException(nameof(observability));
        }

        /// <inheritdoc />
        public async Task TryPersistAsync(
            AiExecutionRecord record,
            AiExecutionState state,
            string? contextKey,
            TContextSnapshot? contextSnapshot,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(state);

            if (!record.IsTerminal)
            {
                return;
            }

            try
            {
                var snapshot = _factory.Create(
                    record,
                    state,
                    contextKey,
                    contextSnapshot);

                snapshot = AiExecutionSnapshotNormalizer.Normalize(
                    snapshot);

                await PersistTerminalSnapshotAsync(
                    snapshot).ConfigureAwait(false);

                await RecordSnapshotLedgerEventAsync(
                        record,
                        snapshot,
                        AiDecisionLedgerEvents.Snapshot.Created,
                        AiDecisionLedgerOutcome.Persisted,
                        "Terminal execution snapshot persisted.",
                        null,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await RecordStorageLedgerEventAsync(
                        record,
                        AiDecisionLedgerEvents.Storage.StatePersistenceFailed,
                        AiDecisionLedgerOutcome.Failed,
                        exception.Message,
                        new Dictionary<string, string>
                        {
                            [AiExceptionMetadataKeys.ExceptionType] = exception.GetType().Name,
                            ["storage.artifact"] = "execution.snapshot"
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);

                // INTENTIONALLY SWALLOWED
                // Snapshot persistence is best-effort and must not affect execution reliability.
            }
        }

        /// <summary>
        /// Persists a terminal execution snapshot as a durable replay artifact.
        /// </summary>
        /// <param name="snapshot">The terminal execution snapshot document.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        /// <remarks>
        /// <para>
        /// Terminal snapshots are replay-critical durability artifacts. They must not
        /// be cancelled by worker-loop or worker-group cancellation after terminal
        /// convergence has already been observed.
        /// </para>
        /// <para>
        /// For this reason, terminal snapshot persistence intentionally does not use
        /// the caller cancellation token. The service remains best-effort because any
        /// exception is still swallowed by <see cref="TryPersistAsync"/>.
        /// </para>
        /// </remarks>
        private async Task PersistTerminalSnapshotAsync(
            AiExecutionSnapshotDocument<TContextSnapshot> snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            await _store.UpsertAsync(
                snapshot,
                CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// Records a snapshot decision ledger event.
        /// </summary>
        private async Task RecordSnapshotLedgerEventAsync(
            AiExecutionRecord record,
            AiExecutionSnapshotDocument<TContextSnapshot> snapshot,
            string eventType,
            AiDecisionLedgerOutcome outcome,
            string? reason,
            IReadOnlyDictionary<string, string>? additionalMetadata,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            var metadata = CreateBaseMetadata(record);

            metadata["snapshot.id"] = snapshot.Id ?? string.Empty;
            metadata[AiExecutionContextMetadataKeys.ContextKey] = snapshot.ContextKey ?? string.Empty;
            metadata[AiExecutionMetadataKeys.ExecutionStatus] = record.Status.ToString();

            MergeMetadata(
                metadata,
                additionalMetadata);

            await RecordLedgerEventAsync(
                    record.ExecutionId,
                    AiDecisionLedgerCategory.Snapshot,
                    eventType,
                    outcome,
                    reason,
                    metadata,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Records a storage decision ledger event.
        /// </summary>
        private async Task RecordStorageLedgerEventAsync(
            AiExecutionRecord record,
            string eventType,
            AiDecisionLedgerOutcome outcome,
            string? reason,
            IReadOnlyDictionary<string, string>? additionalMetadata,
            CancellationToken cancellationToken)
        {
            var metadata = CreateBaseMetadata(record);

            MergeMetadata(
                metadata,
                additionalMetadata);

            await RecordLedgerEventAsync(
                    record.ExecutionId,
                    AiDecisionLedgerCategory.Storage,
                    eventType,
                    outcome,
                    reason,
                    metadata,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Records a generic decision ledger event for snapshot persistence.
        /// </summary>
        private async Task RecordLedgerEventAsync(
            string executionId,
            AiDecisionLedgerCategory category,
            string eventType,
            AiDecisionLedgerOutcome outcome,
            string? reason,
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken cancellationToken)
        {
            if (_observability?.Ledger is null)
            {
                return;
            }

            var correlationContext = AiRuntimeCorrelationContextHelper.Create(
                executionId,
                SnapshotPipelineKey,
                SnapshotStepName,
                SnapshotWorkerId,
                claimToken: null,
                concurrencyContext: null);

            await _observability.Ledger
                .RecordAsync(
                    correlationContext,
                    category,
                    eventType,
                    outcome,
                    reason,
                    metadata,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Creates base snapshot metadata shared by snapshot and storage ledger events.
        /// </summary>
        private static Dictionary<string, string> CreateBaseMetadata(
            AiExecutionRecord record)
        {
            return new Dictionary<string, string>
            {
                [AiExecutionMetadataKeys.ExecutionId] = record.ExecutionId,
                [AiPipelineMetadataKeys.Name] = record.PipelineName ?? string.Empty,
                [AiExecutionMetadataKeys.ExecutionStatus] = record.Status.ToString()
            };
        }

        /// <summary>
        /// Merges optional metadata into the target metadata dictionary.
        /// </summary>
        private static void MergeMetadata(
            IDictionary<string, string> target,
            IReadOnlyDictionary<string, string>? source)
        {
            if (source is null)
            {
                return;
            }

            foreach (var pair in source)
            {
                target[pair.Key] = pair.Value;
            }
        }
    }
}