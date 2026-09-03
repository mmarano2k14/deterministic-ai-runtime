using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.AI.Runtime.Execution.Context;
using Multiplexed.AI.Runtime.Execution.Convergence;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Engine.Helpers;
using Multiplexed.AI.Runtime.Execution.Engine.Models;
using Multiplexed.AI.Runtime.Execution.Engine.Retention;
using Multiplexed.AI.Runtime.Execution.Persistence.Replay;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Runtime.Execution.Engine.Finalization
{
    public sealed class AiDagExecutionFinalizationService
    {
        private readonly IAiDagExecutionEngineServices _engineServices;
        private readonly AiDagRetentionCoordinator _retentionCoordinator;

        public AiDagExecutionFinalizationService(
            IAiDagExecutionEngineServices engineServices,
            AiDagRetentionCoordinator retentionCoordinator)
        {
            _engineServices = engineServices ?? throw new ArgumentNullException(nameof(engineServices));
            _retentionCoordinator = retentionCoordinator ?? throw new ArgumentNullException(nameof(retentionCoordinator));
        }

        public async Task PersistDistributedConvergedRecordAsync(
            AiExecutionRecord record,
            AiDagExecutionConvergenceResult convergence,
            string expectedStepKey,
            AiExecutionState state,
            ResolvedAiPipeline resolvedPipeline,
            Func<AiExecutionRecord, AiExecutionState, CancellationToken, AiExecutionContext> buildExecutionContext,
            Func<AiExecutionRecord, string, AiExecutionState, CancellationToken, Task> persistAsync,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(convergence);
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedStepKey);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(resolvedPipeline);
            ArgumentNullException.ThrowIfNull(buildExecutionContext);
            ArgumentNullException.ThrowIfNull(persistAsync);

            var utcNow = DateTime.UtcNow;
            var pipelineKey = $"{resolvedPipeline.Name}:{resolvedPipeline.Version}";
            var runtimeInstanceId = _engineServices.RuntimeInstanceIdentity.RuntimeInstanceId;

            if (_engineServices.DagStore is null)
            {
                record.TouchVersion();
                record.RenewExecutionStepKey();

                await persistAsync(record, expectedStepKey, state, cancellationToken)
                    .ConfigureAwait(false);

                return;
            }

            var completedSteps = record.CompletedSteps
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            if (convergence.IsTerminal)
            {
                var finalStatus = await ResolveFinalStatusAsync(
                        record.ExecutionId,
                        convergence.Status,
                        cancellationToken)
                    .ConfigureAwait(false);

                await RecordFinalizationStartedAsync(
                        record,
                        convergence,
                        finalStatus,
                        expectedStepKey,
                        resolvedPipeline,
                        completedSteps.Count,
                        pipelineKey,
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (finalStatus == AiExecutionStatus.Cancelled &&
                    convergence.Status != AiExecutionStatus.Cancelled)
                {
                    await RecordCancellationOverrideAppliedAsync(
                            record,
                            convergence,
                            finalStatus,
                            expectedStepKey,
                            resolvedPipeline,
                            pipelineKey,
                            runtimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                try
                {
                    var request = new AiDagExecutionFinalizationRequest
                    {
                        ExecutionId = record.ExecutionId,
                        ExpectedExecutionStepKey = expectedStepKey,
                        Status = finalStatus,
                        CompletedAtUtc = utcNow,
                        CompletedSteps = completedSteps,
                        CurrentStep = string.Empty,
                        WorkerId = runtimeInstanceId
                    };

                    var success = await _engineServices.ObservabilityService.Tracer.TraceStorageAsync(
                        new AiStorageTraceContext
                        {
                            ExecutionId = record.ExecutionId,
                            Backend = "Redis",
                            Operation = "TryFinalizeExecution"
                        },
                        async trace =>
                        {
                            var result = await _engineServices.DagStore.TryFinalizeExecutionAsync(
                                    request,
                                    cancellationToken)
                                .ConfigureAwait(false);

                            trace.SetTag("finalized", result);
                            trace.SetTag("status", finalStatus.ToString());
                            trace.SetTag(AiWorkerMetadataKeys.CamelCaseWorkerId, runtimeInstanceId);
                            trace.SetTag("expectedStepKey", expectedStepKey);
                            trace.SetTag("completedSteps", completedSteps.Count);

                            return result;
                        }).ConfigureAwait(false);

                    if (!success)
                    {
                        await RecordFinalizationRaceLostAsync(
                                record,
                                finalStatus,
                                expectedStepKey,
                                resolvedPipeline,
                                completedSteps.Count,
                                pipelineKey,
                                runtimeInstanceId,
                                "Finalization race lost. Another worker finalized or updated the execution first.",
                                cancellationToken)
                            .ConfigureAwait(false);

                        _engineServices.Logger.Engine.FinalizationRaceLost(
                            record.ExecutionId,
                            finalStatus);

                        var refreshed = await _engineServices.DagStore.GetRecordAsync(
                                record.ExecutionId,
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (refreshed is not null)
                        {
                            AiDagExecutionRecordFinalizer.ApplyAuthoritativeRecord(record, refreshed);
                        }

                        return;
                    }

                    await RecordFinalizationCompletedAsync(
                            record,
                            finalStatus,
                            expectedStepKey,
                            resolvedPipeline,
                            completedSteps.Count,
                            pipelineKey,
                            runtimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                    await _engineServices.ReplayMetadataService.SaveTerminalFingerprintAsync(
                            record,
                            state,
                            cancellationToken)
                        .ConfigureAwait(false);

                    await RecordExecutionTerminalEventsAsync(
                            record,
                            finalStatus,
                            resolvedPipeline,
                            completedSteps.Count,
                            pipelineKey,
                            runtimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                    var retentionStep = resolvedPipeline.Steps.FirstOrDefault()
                        ?? throw new InvalidOperationException(
                            $"Pipeline '{resolvedPipeline.Name}' does not contain any resolved step for retention context creation.");

                    var executionContext = buildExecutionContext(
                        record,
                        state,
                        cancellationToken);

                    var retentionStepContext = new AiStepExecutionContext(
                        executionContext,
                        retentionStep);

                    var retentionMutatedState = await _retentionCoordinator.ApplyBatchRetentionPersistAndWarmAsync(
                            record.ExecutionId,
                            state,
                            retentionStepContext,
                            state.Steps.Keys.ToArray(),
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (retentionMutatedState)
                    {
                        var retainedState = await _engineServices.DagStore.GetStateAsync(
                                record.ExecutionId,
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (retainedState is not null)
                        {
                            state.Steps = retainedState.Steps;
                            state.UpdatedAtUtc = retainedState.UpdatedAtUtc;
                        }
                    }

                    _engineServices.Logger.Engine.FinalizationSucceeded(
                        record.ExecutionId,
                        finalStatus);

                    var updated = await _engineServices.DagStore.GetRecordAsync(
                            record.ExecutionId,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (updated is not null)
                    {
                        AiDagExecutionRecordFinalizer.ApplyAuthoritativeRecord(record, updated);
                    }

                    return;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await RecordFinalizationFailedAsync(
                            record,
                            finalStatus,
                            expectedStepKey,
                            resolvedPipeline,
                            completedSteps.Count,
                            pipelineKey,
                            runtimeInstanceId,
                            exception.Message,
                            cancellationToken)
                        .ConfigureAwait(false);

                    throw;
                }
            }

            record.TouchVersion();
            record.RenewExecutionStepKey();

            await _engineServices.DagStore.SaveRecordAsync(record, cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<AiExecutionStatus> ResolveFinalStatusAsync(
            string executionId,
            AiExecutionStatus convergedStatus,
            CancellationToken cancellationToken)
        {
            if (convergedStatus == AiExecutionStatus.Cancelled)
            {
                return AiExecutionStatus.Cancelled;
            }

            if (_engineServices.ExecutionControlGate is null)
            {
                return convergedStatus;
            }

            var decision = await _engineServices.ExecutionControlGate
                .CheckBeforeAdvanceAsync(executionId, cancellationToken)
                .ConfigureAwait(false);

            return decision is { ShouldCancel: true }
                ? AiExecutionStatus.Cancelled
                : convergedStatus;
        }

        private async Task RecordFinalizationStartedAsync(
            AiExecutionRecord record,
            AiDagExecutionConvergenceResult convergence,
            AiExecutionStatus finalStatus,
            string expectedStepKey,
            ResolvedAiPipeline resolvedPipeline,
            int completedStepsCount,
            string pipelineKey,
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                    _engineServices,
                    record.ExecutionId,
                    pipelineKey,
                    "_finalization",
                    "_finalization",
                    runtimeInstanceId,
                    null,
                    null,
                    AiDecisionLedgerCategory.Finalization,
                    AiEngineEvents.Finalization.Started,
                    AiDecisionLedgerOutcome.Started,
                    "Execution finalization started.",
                    new Dictionary<string, string>
                    {
                        [AiPipelineMetadataKeys.Name] = resolvedPipeline.Name ?? string.Empty,
                        [AiPipelineMetadataKeys.Version] = resolvedPipeline.Version ?? string.Empty,
                        ["converged.status"] = convergence.Status.ToString(),
                        ["final.status"] = finalStatus.ToString(),
                        ["expected.step.key"] = expectedStepKey ?? string.Empty,
                        ["completed.steps.count"] = completedStepsCount.ToString()
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task RecordCancellationOverrideAppliedAsync(
            AiExecutionRecord record,
            AiDagExecutionConvergenceResult convergence,
            AiExecutionStatus finalStatus,
            string expectedStepKey,
            ResolvedAiPipeline resolvedPipeline,
            string pipelineKey,
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                    _engineServices,
                    record.ExecutionId,
                    pipelineKey,
                    "_finalization",
                    "_finalization",
                    runtimeInstanceId,
                    null,
                    null,
                    AiDecisionLedgerCategory.Finalization,
                    AiEngineEvents.Finalization.CancellationOverrideApplied,
                    AiDecisionLedgerOutcome.Applied,
                    "Final status overridden by execution control cancellation.",
                    new Dictionary<string, string>
                    {
                        [AiPipelineMetadataKeys.Name] = resolvedPipeline.Name ?? string.Empty,
                        [AiPipelineMetadataKeys.Version] = resolvedPipeline.Version ?? string.Empty,
                        ["converged.status"] = convergence.Status.ToString(),
                        ["final.status"] = finalStatus.ToString(),
                        ["expected.step.key"] = expectedStepKey ?? string.Empty
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task RecordFinalizationCompletedAsync(
            AiExecutionRecord record,
            AiExecutionStatus finalStatus,
            string expectedStepKey,
            ResolvedAiPipeline resolvedPipeline,
            int completedStepsCount,
            string pipelineKey,
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                    _engineServices,
                    record.ExecutionId,
                    pipelineKey,
                    "_finalization",
                    "_finalization",
                    runtimeInstanceId,
                    null,
                    null,
                    AiDecisionLedgerCategory.Finalization,
                    AiEngineEvents.Finalization.Completed,
                    AiDecisionLedgerOutcome.Completed,
                    "Execution finalization completed.",
                    new Dictionary<string, string>
                    {
                        [AiPipelineMetadataKeys.Name] = resolvedPipeline.Name ?? string.Empty,
                        [AiPipelineMetadataKeys.Version] = resolvedPipeline.Version ?? string.Empty,
                        ["final.status"] = finalStatus.ToString(),
                        ["expected.step.key"] = expectedStepKey ?? string.Empty,
                        ["completed.steps.count"] = completedStepsCount.ToString()
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task RecordFinalizationRaceLostAsync(
            AiExecutionRecord record,
            AiExecutionStatus finalStatus,
            string expectedStepKey,
            ResolvedAiPipeline resolvedPipeline,
            int completedStepsCount,
            string pipelineKey,
            string runtimeInstanceId,
            string reason,
            CancellationToken cancellationToken)
        {
            await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                    _engineServices,
                    record.ExecutionId,
                    pipelineKey,
                    "_finalization",
                    "_finalization",
                    runtimeInstanceId,
                    null,
                    null,
                    AiDecisionLedgerCategory.Finalization,
                    AiEngineEvents.Finalization.RaceLost,
                    AiDecisionLedgerOutcome.Denied,
                    reason,
                    new Dictionary<string, string>
                    {
                        [AiPipelineMetadataKeys.Name] = resolvedPipeline.Name ?? string.Empty,
                        [AiPipelineMetadataKeys.Version] = resolvedPipeline.Version ?? string.Empty,
                        ["final.status"] = finalStatus.ToString(),
                        ["expected.step.key"] = expectedStepKey ?? string.Empty,
                        ["completed.steps.count"] = completedStepsCount.ToString(),
                        ["race.type"] = "distributed-finalization"
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task RecordFinalizationFailedAsync(
            AiExecutionRecord record,
            AiExecutionStatus finalStatus,
            string expectedStepKey,
            ResolvedAiPipeline resolvedPipeline,
            int completedStepsCount,
            string pipelineKey,
            string runtimeInstanceId,
            string reason,
            CancellationToken cancellationToken)
        {
            await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                    _engineServices,
                    record.ExecutionId,
                    pipelineKey,
                    "_finalization",
                    "_finalization",
                    runtimeInstanceId,
                    null,
                    null,
                    AiDecisionLedgerCategory.Finalization,
                    AiEngineEvents.Finalization.Failed,
                    AiDecisionLedgerOutcome.Failed,
                    reason,
                    new Dictionary<string, string>
                    {
                        [AiPipelineMetadataKeys.Name] = resolvedPipeline.Name ?? string.Empty,
                        [AiPipelineMetadataKeys.Version] = resolvedPipeline.Version ?? string.Empty,
                        ["final.status"] = finalStatus.ToString(),
                        ["expected.step.key"] = expectedStepKey ?? string.Empty,
                        ["completed.steps.count"] = completedStepsCount.ToString()
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task RecordExecutionTerminalEventsAsync(
            AiExecutionRecord record,
            AiExecutionStatus finalStatus,
            ResolvedAiPipeline resolvedPipeline,
            int completedStepsCount,
            string pipelineKey,
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                    _engineServices,
                    record.ExecutionId,
                    pipelineKey,
                    "_execution",
                    "_execution",
                    runtimeInstanceId,
                    null,
                    null,
                    AiDecisionLedgerCategory.Execution,
                    AiEngineEvents.Execution.Finalized,
                    ToExecutionOutcome(finalStatus),
                    "Execution finalized.",
                    new Dictionary<string, string>
                    {
                        [AiPipelineMetadataKeys.Name] = resolvedPipeline.Name ?? string.Empty,
                        [AiPipelineMetadataKeys.Version] = resolvedPipeline.Version ?? string.Empty,
                        ["final.status"] = finalStatus.ToString(),
                        ["completed.steps.count"] = completedStepsCount.ToString()
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                    _engineServices,
                    record.ExecutionId,
                    pipelineKey,
                    "_execution",
                    "_execution",
                    runtimeInstanceId,
                    null,
                    null,
                    AiDecisionLedgerCategory.Execution,
                    ToExecutionEventType(finalStatus),
                    ToExecutionOutcome(finalStatus),
                    $"Execution {finalStatus.ToString().ToLowerInvariant()}.",
                    new Dictionary<string, string>
                    {
                        [AiPipelineMetadataKeys.Name] = resolvedPipeline.Name ?? string.Empty,
                        [AiPipelineMetadataKeys.Version] = resolvedPipeline.Version ?? string.Empty,
                        ["final.status"] = finalStatus.ToString(),
                        ["completed.steps.count"] = completedStepsCount.ToString()
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private static string ToExecutionEventType(AiExecutionStatus status)
        {
            return status switch
            {
                AiExecutionStatus.Completed => AiEngineEvents.Execution.Completed,
                AiExecutionStatus.Failed => AiEngineEvents.Execution.Failed,
                AiExecutionStatus.Cancelled => AiEngineEvents.Execution.Cancelled,
                _ => AiEngineEvents.Execution.Finalized
            };
        }

        private static AiDecisionLedgerOutcome ToExecutionOutcome(AiExecutionStatus status)
        {
            return status switch
            {
                AiExecutionStatus.Completed => AiDecisionLedgerOutcome.Completed,
                AiExecutionStatus.Failed => AiDecisionLedgerOutcome.Failed,
                AiExecutionStatus.Cancelled => AiDecisionLedgerOutcome.Cancelled,
                _ => AiDecisionLedgerOutcome.Completed
            };
        }
    }
}