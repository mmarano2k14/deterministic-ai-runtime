using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Execution.Context;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Engine.Helpers;
using Multiplexed.Abstractions.AI.Observability;

namespace Multiplexed.AI.Runtime.Execution.Engine.Steps
{
    /// <summary>
    /// Executes already-claimed DAG steps.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Centralizes physical DAG step execution.
    /// - Keeps distributed orchestration separated from step execution logic.
    /// - Allows batch and distributed runners to reuse the same execution behavior.
    ///
    /// IMPORTANT:
    /// - This class does not claim steps.
    /// - This class does not finalize steps.
    /// - This class does not release distributed concurrency capacity.
    /// - The batch/distributed runner that owns the claim owns the matching lease release.
    /// - This class records execution-correlated ledger events without changing runtime behavior.
    ///
    /// DISTRIBUTED OWNERSHIP:
    /// - Step ownership is represented by the claim token.
    /// - Concurrency ownership is represented by a deterministic lease id.
    /// - Claim validation remains enforced by the DAG store.
    /// </remarks>
    public sealed class AiDagClaimedStepExecutor
    {
        private readonly IAiDagExecutionEngineServices _services;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiDagClaimedStepExecutor"/> class.
        /// </summary>
        /// <param name="services">The DAG execution engine services.</param>
        public AiDagClaimedStepExecutor(
            IAiDagExecutionEngineServices services)
        {
            _services = services
                ?? throw new ArgumentNullException(nameof(services));
        }

        /// <summary>
        /// Executes an already-claimed DAG step.
        /// </summary>
        /// <remarks>
        /// Claim and concurrency lease ownership remain with the calling runner. The runner
        /// releases the lease only after the step result has been persisted durably.
        /// </remarks>
        public async Task<AiStepResult> ExecuteAsync(
            AiExecutionRecord record,
            AiExecutionState state,
            ResolvedAiPipeline resolvedPipeline,
            AiClaimedStep claimedStep,
            Func<AiExecutionRecord, AiExecutionState, CancellationToken, AiExecutionContext> buildExecutionContext,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(resolvedPipeline);
            ArgumentNullException.ThrowIfNull(claimedStep);
            ArgumentNullException.ThrowIfNull(buildExecutionContext);

            var resolvedStep = resolvedPipeline.Steps
                .FirstOrDefault(x =>
                    string.Equals(
                        x.Name,
                        claimedStep.StepName,
                        StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"Claimed step '{claimedStep.StepName}' was not found in resolved pipeline '{resolvedPipeline.Name}'.");

            var executionContext = buildExecutionContext(
                record,
                state,
                cancellationToken);

            var stepContext = new AiStepExecutionContext(
                executionContext,
                resolvedStep);

            var stepState = state.Steps.TryGetValue(
                claimedStep.StepName,
                out var existingStepState)
                ? existingStepState
                : null;

            var pipelineName =
                !string.IsNullOrWhiteSpace(resolvedPipeline.Name)
                    ? resolvedPipeline.Name
                    : throw new InvalidOperationException(
                        "Resolved pipeline name is required to build the execution correlation pipeline key.");

            var pipelineKey =
                string.IsNullOrWhiteSpace(resolvedPipeline.Version)
                    ? pipelineName
                    : $"{pipelineName}:{resolvedPipeline.Version}";
            var runtimeInstanceId = _services.RuntimeInstanceIdentity.RuntimeInstanceId;

            var concurrencyContext = new AiConcurrencyContext
            {
                ExecutionId = record.ExecutionId,
                PipelineKey = pipelineKey,
                StepId = claimedStep.StepName,
                StepKey = string.IsNullOrWhiteSpace(resolvedStep.StepKey)
                    ? claimedStep.StepName
                    : resolvedStep.StepKey,
                RuntimeInstanceId = runtimeInstanceId,
                LeaseId = $"{record.ExecutionId}:{claimedStep.StepName}:{runtimeInstanceId}",
                Provider = AiDagExecutionHelpers.TryReadString(stepState?.Config, "provider"),
                Model = AiDagExecutionHelpers.TryReadString(stepState?.Config, "model"),
                Operation =
                    AiDagExecutionHelpers.TryReadString(stepState?.Config, "operation")
                    ?? AiDagExecutionHelpers.TryReadString(stepState?.Config, "type")
            };

            await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                    _services,
                    record.ExecutionId,
                    pipelineKey,
                    stepContext.StepName,
                    stepContext.StepKey,
                    runtimeInstanceId,
                    claimedStep.ClaimToken,
                    concurrencyContext,
                    AiDecisionLedgerCategory.Step,
                    AiDecisionLedgerEvents.Step.Started,
                    AiDecisionLedgerOutcome.Started,
                    "Step execution started.",
                    new Dictionary<string, string>
                    {
                        [AiPipelineMetadataKeys.Name] = resolvedPipeline.Name ?? string.Empty,
                        [AiPipelineMetadataKeys.Version] = resolvedPipeline.Version ?? string.Empty,
                        [AiStepMetadataKeys.StepName] = claimedStep.StepName ?? string.Empty,
                        [AiStepMetadataKeys.StepKey] = concurrencyContext.StepKey ?? string.Empty,
                        [AiWorkerMetadataKeys.WorkerId] = runtimeInstanceId,
                        [AiExecutionClaimMetadataKeys.ClaimToken] = claimedStep.ClaimToken ?? string.Empty
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                    var result = await _services.ObservabilityService.Tracer.TraceStepAsync(
                        new AiStepTraceContext
                        {
                            ExecutionId = record.ExecutionId,
                            StepId = claimedStep.StepName,
                            StepType = resolvedStep.Step.GetType().Name,
                            StepKey = resolvedStep.StepKey,
                            Status = "Running",
                            RetryCount = stepState?.RetryState?.RetryCount ?? 0,
                            RecoveryCount = stepState?.RecoveryCount ?? 0,
                            WorkerId = runtimeInstanceId,
                            ClaimToken = claimedStep.ClaimToken
                        },
                        async () =>
                        {
                            var result = await resolvedStep.Step.ExecuteAsync(
                                stepContext,
                                cancellationToken).ConfigureAwait(false);

                            if (result.EffectiveOutcome != AiStepExecutionOutcome.Park)
                            {
                                await _services.PayloadCompactor.CompactAsync(
                                    result,
                                    cancellationToken).ConfigureAwait(false);
                            }

                            return result;
                        }).ConfigureAwait(false);

                    await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                            _services,
                            record.ExecutionId,
                            pipelineKey,
                            resolvedStep.Name,
                            resolvedStep.StepKey,
                            runtimeInstanceId,
                            claimedStep.ClaimToken,
                            concurrencyContext,
                            AiDecisionLedgerCategory.Step,
                            result.EffectiveOutcome switch
                            {
                                AiStepExecutionOutcome.Park => AiDecisionLedgerEvents.Step.Parked,
                                AiStepExecutionOutcome.Complete => AiDecisionLedgerEvents.Step.Completed,
                                _ => AiDecisionLedgerEvents.Step.Failed
                            },
                            result.EffectiveOutcome switch
                            {
                                AiStepExecutionOutcome.Park => AiDecisionLedgerOutcome.Applied,
                                AiStepExecutionOutcome.Complete => AiDecisionLedgerOutcome.Completed,
                                _ => AiDecisionLedgerOutcome.Failed
                            },
                            result.EffectiveOutcome switch
                            {
                                AiStepExecutionOutcome.Park => "Step execution requested durable external suspension.",
                                AiStepExecutionOutcome.Complete => "Step execution completed.",
                                _ => result.Error ?? "Step execution failed."
                            },
                            new Dictionary<string, string>
                            {
                                [AiPipelineMetadataKeys.Name] = resolvedPipeline.Name ?? string.Empty,
                                [AiPipelineMetadataKeys.Version] = resolvedPipeline.Version ?? string.Empty,
                                [AiStepMetadataKeys.StepName] = claimedStep.StepName ?? string.Empty,
                                [AiStepMetadataKeys.StepKey] = concurrencyContext.StepKey ?? string.Empty,
                                [AiWorkerMetadataKeys.WorkerId] = runtimeInstanceId ?? string.Empty,
                                [AiExecutionClaimMetadataKeys.ClaimToken] = claimedStep.ClaimToken ?? string.Empty
                            },
                            cancellationToken)
                        .ConfigureAwait(false);


                    return result;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _services.Logger.Engine.LogWarning(
                        $"[AI DAG] Step exception converted to failed result. ExecutionId='{record.ExecutionId}', StepName='{claimedStep.StepName}', ClaimToken='{claimedStep.ClaimToken}', Error='{ex.Message}'.");

                    await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                            _services,
                            record.ExecutionId,
                            pipelineKey,
                            resolvedStep.Name,
                            resolvedStep.StepKey,
                            runtimeInstanceId,
                            claimedStep.ClaimToken,
                            concurrencyContext,
                            AiDecisionLedgerCategory.Step,
                            AiDecisionLedgerEvents.Step.Failed,
                            AiDecisionLedgerOutcome.Failed,
                            ex.Message,
                            new Dictionary<string, string>
                            {
                                [AiPipelineMetadataKeys.Name] = resolvedPipeline.Name ?? string.Empty,
                                [AiPipelineMetadataKeys.Version] = resolvedPipeline.Version ?? string.Empty,
                                [AiStepMetadataKeys.StepName] = resolvedStep.Name ?? string.Empty,
                                [AiStepMetadataKeys.StepKey] = resolvedStep.StepKey ?? string.Empty,
                                [AiWorkerMetadataKeys.WorkerId] = runtimeInstanceId ?? string.Empty,
                                [AiExecutionClaimMetadataKeys.ClaimToken] = claimedStep.ClaimToken ?? string.Empty,
                                [AiExceptionMetadataKeys.ExceptionType] = ex.GetType().Name ?? string.Empty
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                    return AiStepResult.Fail(ex.Message);
            }
        }
    }
}