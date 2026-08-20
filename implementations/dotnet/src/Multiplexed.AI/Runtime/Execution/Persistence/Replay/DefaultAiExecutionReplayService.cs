using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay.Models;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay.Reports;
using Multiplexed.Abstractions.AI.Execution.Persistence.Snapshot;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.AI.Runtime.Execution.Persistence.Snapshot.Normalization;
using Multiplexed.AI.Runtime.Observability.Helpers;
using Multiplexed.AI.Runtime.Observability.Logging;
using Multiplexed.AI.Stores;

namespace Multiplexed.AI.Runtime.Execution.Persistence.Replay
{
    /// <summary>
    /// Restores persisted AI execution snapshots and produces replay validation reports.
    /// </summary>
    /// <typeparam name="TContext">
    /// The serialized execution context snapshot type stored with the execution snapshot.
    /// </typeparam>
    /// <remarks>
    /// This service loads persisted execution snapshots, validates replay determinism,
    /// optionally restores runtime state, records replay lifecycle events into the
    /// decision ledger, and enriches replay reports with ledger and trace timeline
    /// data when requested.
    /// </remarks>
    public sealed class DefaultAiExecutionReplayService<TContext> : IAiExecutionReplayService
    {
        private const string ReplayModeMetadataKey = "replay.mode";

        private const string ReplayPipelineKey = "execution-replay";
        private const string ReplayStepName = "_replay";
        private const string ReplayWorkerId = "replay-service";

        private readonly IAiExecutionSnapshotStore<TContext> _snapshotStore;
        private readonly IAiExecutionStore _executionStore;
        private readonly IAiExecutionSnapshotService<TContext> _snapshotService;
        private readonly IAiRuntimeLogger _logger;
        private readonly IAiExecutionReplayExecutor _replayExecutor;
        private readonly IAiDagExecutionStore? _dagStore;
        private readonly IAiRuntimeObservability _observability;
        private readonly IAiDecisionLedger _decisionLedger;
        private readonly IAiTraceTimeline _traceTimeline;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultAiExecutionReplayService{TContext}"/> class.
        /// </summary>
        public DefaultAiExecutionReplayService(
            IAiExecutionSnapshotStore<TContext> snapshotStore,
            IAiExecutionStore executionStore,
            IAiExecutionSnapshotService<TContext> snapshotService,
            IAiRuntimeLogger logger,
            IAiRuntimeObservability observability,
            IAiExecutionReplayExecutor replayExecutor,
            IAiDecisionLedger decisionLedger,
            IAiTraceTimeline traceTimeline,
            IAiDagExecutionStore? dagStore = null)
        {
            _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
            _executionStore = executionStore ?? throw new ArgumentNullException(nameof(executionStore));
            _snapshotService = snapshotService ?? throw new ArgumentNullException(nameof(snapshotService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _observability = observability ?? throw new ArgumentNullException(nameof(observability));
            _replayExecutor = replayExecutor ?? throw new ArgumentNullException(nameof(replayExecutor));
            _decisionLedger = decisionLedger ?? throw new ArgumentNullException(nameof(decisionLedger));
            _traceTimeline = traceTimeline ?? throw new ArgumentNullException(nameof(traceTimeline));
            _dagStore = dagStore;
        }

        /// <inheritdoc />
        public async Task<AiExecutionReplayReport> ReplayAsync(
            AiExecutionReplayRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (string.IsNullOrWhiteSpace(request.ExecutionId))
            {
                throw new ArgumentException(
                    "ExecutionId cannot be null or empty.",
                    nameof(request.ExecutionId));
            }

            var executionId = request.ExecutionId;

            try
            {
                return await _observability.Tracer
                    .TraceExecutionAsync(
                        new AiExecutionTraceContext
                        {
                            ExecutionId = executionId,
                            ExecutionMode = "Replay",
                            Status = request.Mode.ToString(),
                            WorkerId = ReplayWorkerId
                        },
                        async () =>
                        {
                            await RecordReplayLedgerEventAsync(
                                    executionId,
                                    AiDecisionLedgerEvents.Replay.Requested,
                                    AiDecisionLedgerOutcome.Started,
                                    "Replay request received.",
                                    new Dictionary<string, string>
                                    {
                                        [ReplayModeMetadataKey] = request.Mode.ToString()
                                    },
                                    cancellationToken)
                                .ConfigureAwait(false);

                            var snapshot = await _snapshotStore.GetAsync(
                                    executionId,
                                    cancellationToken)
                                .ConfigureAwait(false);

                            if (snapshot is null)
                            {
                                _logger.Engine.ExecutionReplaySkipped(
                                    executionId,
                                    "Snapshot not found.");

                                await RecordReplayLedgerEventAsync(
                                        executionId,
                                        AiDecisionLedgerEvents.Replay.Failed,
                                        AiDecisionLedgerOutcome.Failed,
                                        "Replay failed because snapshot was not found.",
                                        new Dictionary<string, string>
                                        {
                                            [ReplayModeMetadataKey] = request.Mode.ToString(),
                                            ["snapshot.found"] = false.ToString()
                                        },
                                        cancellationToken)
                                    .ConfigureAwait(false);

                                return CreateInvalidReport(
                                    executionId,
                                    request.Mode,
                                    executionFound: false,
                                    snapshotFound: false,
                                    "Snapshot not found.");
                            }

                            await RecordReplayLedgerEventAsync(
                                    executionId,
                                    AiDecisionLedgerEvents.Replay.Started,
                                    AiDecisionLedgerOutcome.Started,
                                    "Replay snapshot loaded.",
                                    new Dictionary<string, string>
                                    {
                                        [ReplayModeMetadataKey] = request.Mode.ToString(),
                                        ["snapshot.found"] = true.ToString()
                                    },
                                    cancellationToken)
                                .ConfigureAwait(false);

                            AiExecutionSnapshotRemapper.Remap(snapshot);

                            var validationError = ValidateSnapshot(snapshot);

                            if (validationError is not null)
                            {
                                _logger.Engine.ExecutionReplaySkipped(
                                    executionId,
                                    validationError);

                                await RecordReplayLedgerEventAsync(
                                        executionId,
                                        AiDecisionLedgerEvents.Replay.Failed,
                                        AiDecisionLedgerOutcome.Failed,
                                        validationError,
                                        new Dictionary<string, string>
                                        {
                                            [ReplayModeMetadataKey] = request.Mode.ToString(),
                                            ["snapshot.found"] = true.ToString(),
                                            ["snapshot.valid"] = false.ToString()
                                        },
                                        cancellationToken)
                                    .ConfigureAwait(false);

                                return CreateInvalidReport(
                                    executionId,
                                    request.Mode,
                                    executionFound: true,
                                    snapshotFound: true,
                                    validationError);
                            }

                            IReadOnlyList<AiDecisionLedgerEntry> ledgerEvents = Array.Empty<AiDecisionLedgerEntry>();
                            IReadOnlyList<AiTraceEvent> timelineEvents = Array.Empty<AiTraceEvent>();

                            var replayReport = await _replayExecutor.ExecuteAsync(
                                    request,
                                    snapshot.Record!,
                                    snapshot.State!,
                                    cancellationToken)
                                .ConfigureAwait(false);

                            ledgerEvents = await LoadLedgerEventsAsync(
                                    request,
                                    executionId,
                                    cancellationToken)
                                .ConfigureAwait(false);

                            timelineEvents = LoadTimelineEvents(
                                request,
                                executionId);

                            await RecordReplayLedgerEventAsync(
                                    executionId,
                                    AiDecisionLedgerEvents.Replay.ComparisonCompleted,
                                    replayReport.ReplayValid
                                        ? AiDecisionLedgerOutcome.Completed
                                        : AiDecisionLedgerOutcome.Failed,
                                    replayReport.ReplayValid
                                        ? "Replay validation completed successfully."
                                        : replayReport.FailureReason,
                                    new Dictionary<string, string>
                                    {
                                        [ReplayModeMetadataKey] = request.Mode.ToString(),
                                        ["replay.valid"] = replayReport.ReplayValid.ToString(),
                                        ["fingerprint.matches"] = replayReport.FingerprintMatches.ToString(),
                                        ["issues.count"] = replayReport.Issues.Count.ToString()
                                    },
                                    cancellationToken)
                                .ConfigureAwait(false);

                            var existingRecord = await GetExistingRecordAsync(
                                    executionId,
                                    cancellationToken)
                                .ConfigureAwait(false);

                            if (existingRecord is not null &&
                                IsCompatibleExistingRecord(existingRecord, snapshot.Record!))
                            {
                                _logger.Engine.ExecutionReplaySkipped(
                                    executionId,
                                    "Replay skipped because a compatible runtime execution already exists.");

                                await RecordReplayLedgerEventAsync(
                                        executionId,
                                        AiDecisionLedgerEvents.Replay.Completed,
                                        AiDecisionLedgerOutcome.Completed,
                                        "Replay completed without restore because a compatible runtime execution already exists.",
                                        new Dictionary<string, string>
                                        {
                                            [ReplayModeMetadataKey] = request.Mode.ToString(),
                                            ["replay.valid"] = replayReport.ReplayValid.ToString(),
                                            ["restore.applied"] = false.ToString(),
                                            ["existing.execution"] = true.ToString()
                                        },
                                        cancellationToken)
                                    .ConfigureAwait(false);

                                return MergeReport(
                                    replayReport,
                                    request.Mode,
                                    existingRecord.PipelineName,
                                    existingRecord.Status.ToString(),
                                    ledgerEvents,
                                    timelineEvents);
                            }

                            if (request.Mode == AiExecutionReplayMode.AuditOnly)
                            {
                                await RecordReplayLedgerEventAsync(
                                        executionId,
                                        AiDecisionLedgerEvents.Replay.Completed,
                                        AiDecisionLedgerOutcome.Completed,
                                        "Replay audit completed without restoring runtime state.",
                                        new Dictionary<string, string>
                                        {
                                            [ReplayModeMetadataKey] = request.Mode.ToString(),
                                            ["replay.valid"] = replayReport.ReplayValid.ToString(),
                                            ["restore.applied"] = false.ToString()
                                        },
                                        cancellationToken)
                                    .ConfigureAwait(false);

                                return MergeReport(
                                    replayReport,
                                    request.Mode,
                                    snapshot.Record!.PipelineName,
                                    snapshot.Record.Status.ToString(),
                                    ledgerEvents,
                                    timelineEvents);
                            }

                            AiExecutionReplayPreparation.Prepare(
                                snapshot.Record,
                                snapshot.State);

                            await RestoreToAuthoritativeStoreAsync(
                                    snapshot.Record!,
                                    snapshot.State!,
                                    cancellationToken)
                                .ConfigureAwait(false);

                            await _snapshotService.TryPersistAsync(
                                    snapshot.Record!,
                                    snapshot.State!,
                                    snapshot.ContextKey,
                                    snapshot.ContextSnapshot,
                                    cancellationToken)
                                .ConfigureAwait(false);

                            _logger.Engine.ExecutionReplayRestored(
                                executionId,
                                snapshot.Record!.Status,
                                snapshot.State?.Steps?.Count ?? 0);

                            await RecordReplayLedgerEventAsync(
                                    executionId,
                                    AiDecisionLedgerEvents.Replay.Completed,
                                    AiDecisionLedgerOutcome.Completed,
                                    "Replay completed and runtime state was restored.",
                                    new Dictionary<string, string>
                                    {
                                        [ReplayModeMetadataKey] = request.Mode.ToString(),
                                        ["replay.valid"] = replayReport.ReplayValid.ToString(),
                                        ["restore.applied"] = true.ToString()
                                    },
                                    cancellationToken)
                                .ConfigureAwait(false);

                            return MergeReport(
                                replayReport,
                                request.Mode,
                                snapshot.Record.PipelineName,
                                snapshot.Record.Status.ToString(),
                                ledgerEvents,
                                timelineEvents);
                        })
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await RecordReplayLedgerEventAsync(
                        executionId,
                        AiDecisionLedgerEvents.Replay.Failed,
                        AiDecisionLedgerOutcome.Failed,
                        exception.Message,
                        new Dictionary<string, string>
                        {
                            [ReplayModeMetadataKey] = request.Mode.ToString(),
                            [AiExceptionMetadataKeys.ExceptionType] = exception.GetType().FullName ?? exception.GetType().Name
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);

                throw;
            }
        }

        /// <summary>
        /// Gets an existing execution record from the authoritative DAG store when available,
        /// otherwise from the generic execution store.
        /// </summary>
        private async Task<AiExecutionRecord?> GetExistingRecordAsync(
            string executionId,
            CancellationToken cancellationToken)
        {
            if (_dagStore is not null)
            {
                var dagRecord = await _dagStore.GetRecordAsync(
                        executionId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (dagRecord is not null)
                {
                    return dagRecord;
                }
            }

            return await _executionStore.GetRecordAsync(
                    executionId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Restores an execution record and state into the authoritative runtime store.
        /// </summary>
        private async Task RestoreToAuthoritativeStoreAsync(
            AiExecutionRecord record,
            AiExecutionState state,
            CancellationToken cancellationToken)
        {
            if (_dagStore is not null)
            {
                await _dagStore.RestoreAsync(
                        record,
                        state,
                        cancellationToken)
                    .ConfigureAwait(false);

                return;
            }

            await _executionStore.RestoreAsync(
                    record,
                    state,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Validates that the snapshot contains the minimum data required for replay.
        /// </summary>
        private static string? ValidateSnapshot(
            AiExecutionSnapshotDocument<TContext> snapshot)
        {
            if (snapshot.Record is null)
            {
                return "Snapshot record is missing.";
            }

            if (snapshot.State is null)
            {
                return "Snapshot state is missing.";
            }

            if (string.IsNullOrWhiteSpace(snapshot.ExecutionId))
            {
                return "Snapshot execution id is missing.";
            }

            if (!string.Equals(
                    snapshot.ExecutionId,
                    snapshot.Record.ExecutionId,
                    StringComparison.Ordinal))
            {
                return "Snapshot execution id does not match record execution id.";
            }

            if (!string.Equals(
                    snapshot.ExecutionId,
                    snapshot.State.ExecutionId,
                    StringComparison.Ordinal))
            {
                return "Snapshot execution id does not match state execution id.";
            }

            if (!string.Equals(
                    snapshot.Record.ExecutionId,
                    snapshot.State.ExecutionId,
                    StringComparison.Ordinal))
            {
                return "Record execution id does not match state execution id.";
            }

            if (string.IsNullOrWhiteSpace(snapshot.Record.PipelineName))
            {
                return "Snapshot record pipeline name is missing.";
            }

            return null;
        }

        /// <summary>
        /// Determines whether an existing runtime record represents the same replay target.
        /// </summary>
        private static bool IsCompatibleExistingRecord(
            AiExecutionRecord existingRecord,
            AiExecutionRecord snapshotRecord)
        {
            return string.Equals(
                    existingRecord.ExecutionId,
                    snapshotRecord.ExecutionId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    existingRecord.PipelineName,
                    snapshotRecord.PipelineName,
                    StringComparison.Ordinal) &&
                string.Equals(
                    existingRecord.ContextKey,
                    snapshotRecord.ContextKey,
                    StringComparison.Ordinal);
        }

        /// <summary>
        /// Creates an invalid replay report for load or snapshot validation failures.
        /// </summary>
        private static AiExecutionReplayReport CreateInvalidReport(
            string executionId,
            AiExecutionReplayMode mode,
            bool executionFound,
            bool snapshotFound,
            string failureReason)
        {
            return new AiExecutionReplayReport
            {
                ExecutionId = executionId,
                Mode = mode,
                ExecutionFound = executionFound,
                SnapshotFound = snapshotFound,
                ReplayValid = false,
                FailureReason = failureReason,
                Issues =
                [
                    new AiExecutionReplayIssue
                    {
                        Code = "replay.snapshot.invalid",
                        Message = failureReason
                    }
                ]
            };
        }

        /// <summary>
        /// Applies replay execution context and optional observability information
        /// to a replay validation report.
        /// </summary>
        private static AiExecutionReplayReport MergeReport(
            AiExecutionReplayReport replayReport,
            AiExecutionReplayMode mode,
            string? pipelineName,
            string? status,
            IReadOnlyList<AiDecisionLedgerEntry> ledgerEvents,
            IReadOnlyList<AiTraceEvent> timelineEvents)
        {
            ArgumentNullException.ThrowIfNull(replayReport);
            ArgumentNullException.ThrowIfNull(ledgerEvents);
            ArgumentNullException.ThrowIfNull(timelineEvents);

            return new AiExecutionReplayReport
            {
                ExecutionId = replayReport.ExecutionId,
                Mode = mode,
                PipelineName = pipelineName,
                PipelineKey = replayReport.PipelineKey,
                Status = status,

                ExecutionFound = true,
                SnapshotFound = true,

                ReplayMetadata = replayReport.ReplayMetadata,

                FingerprintFound = replayReport.FingerprintFound,
                OriginalFingerprint = replayReport.OriginalFingerprint,
                ReconstructedFingerprint = replayReport.ReconstructedFingerprint,
                FingerprintMatches = replayReport.FingerprintMatches,

                DependencyGraphValid = replayReport.DependencyGraphValid,
                StepStateValid = replayReport.StepStateValid,
                PayloadReferencesValid = replayReport.PayloadReferencesValid,

                ReplayValid = replayReport.ReplayValid,
                FailureReason = replayReport.FailureReason,

                TotalSteps = replayReport.TotalSteps,
                CompletedSteps = replayReport.CompletedSteps,
                FailedSteps = replayReport.FailedSteps,
                WaitingForRetrySteps = replayReport.WaitingForRetrySteps,
                RunningSteps = replayReport.RunningSteps,

                RetryCount = replayReport.RetryCount,
                RecoveryCount = replayReport.RecoveryCount,

                Issues = replayReport.Issues,
                Steps = replayReport.Steps,

                LedgerEvents = ledgerEvents,
                TimelineEvents = timelineEvents
            };
        }

        /// <summary>
        /// Records a replay lifecycle event in the execution-correlated decision ledger.
        /// </summary>
        private async Task RecordReplayLedgerEventAsync(
            string executionId,
            string eventType,
            AiDecisionLedgerOutcome outcome,
            string? reason,
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken cancellationToken)
        {
            var correlationContext = AiRuntimeCorrelationContextHelper.Create(
                executionId,
                ReplayPipelineKey,
                ReplayStepName,
                ReplayStepName,
                ReplayWorkerId,
                claimToken: null,
                concurrencyContext: null);

            await _observability.Ledger
                .RecordAsync(
                    correlationContext,
                    AiDecisionLedgerCategory.Replay,
                    eventType,
                    outcome,
                    reason,
                    metadata,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Loads execution-correlated decision ledger events when requested.
        /// </summary>
        private async Task<IReadOnlyList<AiDecisionLedgerEntry>> LoadLedgerEventsAsync(
            AiExecutionReplayRequest request,
            string executionId,
            CancellationToken cancellationToken)
        {
            if (!request.IncludeLedgerEvents)
            {
                return Array.Empty<AiDecisionLedgerEntry>();
            }

            return await _decisionLedger
                .GetByExecutionAsync(
                    executionId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Loads execution trace timeline events when requested.
        /// </summary>
        private IReadOnlyList<AiTraceEvent> LoadTimelineEvents(
            AiExecutionReplayRequest request,
            string executionId)
        {
            if (!request.IncludeTimeline)
            {
                return Array.Empty<AiTraceEvent>();
            }

            return _traceTimeline.Get(executionId);
        }
    }
}