using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.Signals;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.AI.Concurrency;
using Multiplexed.AI.Runtime.Execution.Context;
using Multiplexed.AI.Runtime.Execution.Convergence;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Engine.Finalization;
using Multiplexed.AI.Runtime.Execution.Engine.Helpers;
using Multiplexed.AI.Runtime.Execution.Engine.Retention;
using Multiplexed.AI.Runtime.Execution.Engine.Steps;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Runtime.Execution.Engine.Distributed
{
    /// <summary>
    /// Executes DAG pipelines using the distributed execution path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This runner advances a distributed DAG execution by claiming and executing a single
    /// ready step at a time.
    /// </para>
    ///
    /// <para>
    /// Distributed claiming is concurrency-aware. The claim service may acquire a distributed
    /// concurrency lease before the DAG step is actually claimed. Once the claimed step has
    /// completed, parked, failed, or thrown, this runner releases the corresponding concurrency lease.
    /// </para>
    ///
    /// <para>
    /// The pipeline key used for throttling is stable across executions of the same pipeline.
    /// This allows multiple executions of the same logical pipeline to share the same
    /// pipeline-level concurrency limit while keeping each concrete execution isolated by
    /// its own execution identifier.
    /// </para>
    /// </remarks>
    public sealed class AiDagDistributedExecutionRunner
    {
        private static readonly IAiConcurrencyDefinitionResolver ConcurrencyDefinitionResolver =
            new DefaultAiConcurrencyDefinitionResolver();

        private readonly IAiDagExecutionEngineServices _engineServices;
        private readonly AiDagStepClaimService _claimService;
        private readonly AiDagClaimedStepExecutor _claimedStepExecutor;
        private readonly AiDagRetentionCoordinator _retentionCoordinator;
        private readonly AiDagExecutionFinalizationService _finalizationService;
        private readonly AiDagExecutionLifecycleHelper _lifecycleHelper;
        private readonly IAiRuntimeSignalPublisher? _runtimeSignalPublisher;
        private readonly IAiControlPlaneIdResolver? _controlPlaneIdResolver;


        /// <summary>
        /// Initializes a new instance of the <see cref="AiDagDistributedExecutionRunner"/> class.
        /// </summary>
        /// <param name="engineServices">The DAG execution engine services.</param>
        /// <param name="claimService">The distributed step claim service.</param>
        /// <param name="claimedStepExecutor">The claimed step executor.</param>
        /// <param name="retentionCoordinator">The retention coordinator.</param>
        /// <param name="finalizationService">The distributed finalization service.</param>
        /// <param name="lifecycleHelper">The terminal lifecycle helper.</param>
        /// <param name="runtimeSignalPublisher">
        /// The optional best-effort runtime signal publisher.
        /// </param>
        /// <param name="controlPlaneIdResolver">
        /// The optional logical control-plane identifier resolver.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when one of the required services is <see langword="null"/>.
        /// </exception>
        public AiDagDistributedExecutionRunner(
            IAiDagExecutionEngineServices engineServices,
            AiDagStepClaimService claimService,
            AiDagClaimedStepExecutor claimedStepExecutor,
            AiDagRetentionCoordinator retentionCoordinator,
            AiDagExecutionFinalizationService finalizationService,
            AiDagExecutionLifecycleHelper lifecycleHelper,
            IAiRuntimeSignalPublisher? runtimeSignalPublisher,
            IAiControlPlaneIdResolver? controlPlaneIdResolver)
        {
            _engineServices = engineServices
                ?? throw new ArgumentNullException(nameof(engineServices));

            _claimService = claimService
                ?? throw new ArgumentNullException(nameof(claimService));

            _claimedStepExecutor = claimedStepExecutor
                ?? throw new ArgumentNullException(nameof(claimedStepExecutor));

            _retentionCoordinator = retentionCoordinator
                ?? throw new ArgumentNullException(nameof(retentionCoordinator));

            _finalizationService = finalizationService
                ?? throw new ArgumentNullException(nameof(finalizationService));

            _lifecycleHelper = lifecycleHelper
                ?? throw new ArgumentNullException(nameof(lifecycleHelper));

            _runtimeSignalPublisher = runtimeSignalPublisher
                ?? throw new ArgumentNullException(nameof(runtimeSignalPublisher));

            _controlPlaneIdResolver = controlPlaneIdResolver
                ?? throw new ArgumentNullException(nameof(controlPlaneIdResolver));
        }

        /// <summary>
        /// Executes the next distributed DAG step.
        /// </summary>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="loadContextAndSetAsync">Delegate used to load and set the RBAC context.</param>
        /// <param name="buildExecutionContext">Delegate used to build execution contexts.</param>
        /// <param name="persistAsync">Delegate used to persist execution state.</param>
        /// <param name="ensurePipelineName">Delegate used to validate pipeline names.</param>
        /// <param name="validateExecutionId">Delegate used to validate execution identifiers.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The updated execution record.</returns>
        /// <remarks>
        /// <para>
        /// The execution sequence is:
        /// </para>
        ///
        /// <list type="number">
        /// <item><description>Load the execution record.</description></item>
        /// <item><description>Resolve the pipeline and derive a stable pipeline key.</description></item>
        /// <item><description>Recover and claim one ready distributed DAG step.</description></item>
        /// <item><description>Execute the claimed step.</description></item>
        /// <item><description>Persist completion or failure through Redis.</description></item>
        /// <item><description>Release the distributed concurrency lease for the claimed step.</description></item>
        /// <item><description>Evaluate convergence and persist the resulting execution record.</description></item>
        /// </list>
        ///
        /// <para>
        /// The concurrency lease is released in a <c>finally</c> block after a successful claim.
        /// This ensures that distributed capacity is not held until TTL expiration when execution,
        /// completion, failure handling, retention, or convergence throws.
        /// </para>
        /// </remarks>
        public async Task<AiExecutionRecord> ExecuteNextAsync(
            string executionId,
            Func<string, Task> loadContextAndSetAsync,
            Func<AiExecutionRecord, AiExecutionState, CancellationToken, AiExecutionContext> buildExecutionContext,
            Func<AiExecutionRecord, string, AiExecutionState, CancellationToken, Task> persistAsync,
            Action<AiExecutionRecord> ensurePipelineName,
            Action<string> validateExecutionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            _engineServices.Logger.Engine.LogInformation(
                $"[AI DAG RUNNER SELECTED] Runner='Distributed', ExecutionId='{executionId}'.");

            validateExecutionId(executionId);

            var workerId = _engineServices.RuntimeInstanceIdentity.RuntimeInstanceId;

            var record = await _engineServices.DagStore!.GetRecordAsync(
                executionId,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Execution '{executionId}' was not found.");

            if (record.IsTerminal)
            {
                _engineServices.Logger.Engine.ExecutionAlreadyCompleted(record);

                var terminalState = await _engineServices.DagStore.GetStateAsync(
                    executionId,
                    CancellationToken.None).ConfigureAwait(false)
                    ?? new AiExecutionState
                    {
                        ExecutionId = executionId,
                        PipelineName = record.PipelineName
                    };

                await _lifecycleHelper.EnsureTerminalLifecycleAsync(
                    record,
                    terminalState,
                    CancellationToken.None).ConfigureAwait(false);

                return record;
            }

            if (record.ExecutionMode != AiExecutionMode.Dag)
            {
                throw new InvalidOperationException(
                    $"Execution '{executionId}' is configured for mode '{record.ExecutionMode}' and cannot be executed by the DAG engine.");
            }

            ensurePipelineName(record);

            await loadContextAndSetAsync(record.ContextKey).ConfigureAwait(false);

            var resolvedPipeline = await AiExecutionBoundPipelineResolver.PrepareAsync(
                _engineServices,
                record,
                cancellationToken).ConfigureAwait(false);

            if (resolvedPipeline is null)
            {
                throw new InvalidOperationException(
                    $"Pipeline '{record.PipelineName}' could not be resolved for execution '{executionId}'.");
            }

            var pipelineKey = CreatePipelineKey(resolvedPipeline);

            var claimed = await _claimService.ClaimNextAsync(
                executionId,
                resolvedPipeline,
                pipelineKey,
                workerId,
                cancellationToken).ConfigureAwait(false);

            var state = await _engineServices.DagStore.GetStateAsync(
                executionId,
                cancellationToken).ConfigureAwait(false)
                ?? new AiExecutionState
                {
                    ExecutionId = executionId,
                    PipelineName = record.PipelineName,
                    PipelineConfig = new Dictionary<string, object?>(
                        resolvedPipeline.Config,
                        StringComparer.Ordinal)
                };

            await _engineServices.StepResolver.WarmAsync(
                executionId,
                state,
                cancellationToken).ConfigureAwait(false);

            try
            {
                if (claimed is null)
                {
                    record.Steps = resolvedPipeline.Steps
                        .Select(x => x.Name)
                        .ToList();

                    var convergence = await AiDagExecutionConvergenceEvaluator.EvaluateAsync(
                        resolvedPipeline,
                        state,
                        _engineServices.StateWriter,
                        _engineServices.StepResolver,
                        DateTime.UtcNow,
                        cancellationToken).ConfigureAwait(false);

                    _engineServices.Logger.Engine.LogInformation(
                        $"[AI DAG] No distributed claim acquired. ExecutionId='{record.ExecutionId}', ConvergenceStatus='{convergence.Status}', Worker='{workerId}'.");

                    AiDagExecutionRecordFinalizer.ApplyConvergenceToRecord(
                        record,
                        convergence,
                        state,
                        AiDagExecutionHelpers.GetDeclaredStepNames(resolvedPipeline));

                    var expectedStepKey = AiDagExecutionHelpers.GetRequiredExecutionStepKey(record);

                    await _finalizationService.PersistDistributedConvergedRecordAsync(
                        record,
                        convergence,
                        expectedStepKey,
                        state,
                        resolvedPipeline,
                        buildExecutionContext,
                        persistAsync,
                        cancellationToken).ConfigureAwait(false);

                    if (record.IsTerminal)
                    {
                        _engineServices.Logger.Engine.ExecutionCompleted(record);

                        _engineServices.ObservabilityService.Metrics.Execution.RecordExecutionCompleted(
                            record.ExecutionId);

                        await _lifecycleHelper.EnsureTerminalLifecycleAsync(
                            record,
                            state,
                            cancellationToken).ConfigureAwait(false);
                    }

                    return record;
                }

                var concurrencyLeaseReleased = false;

                try
                {
                    _engineServices.Logger.Engine.LogInformation(
                        $"[AI DAG] Step claimed. ExecutionId='{record.ExecutionId}', StepName='{claimed.StepName}', Worker='{workerId}', ClaimToken='{claimed.ClaimToken}'.");

                    record.MarkRunning();
                    record.CurrentStep = claimed.StepName;

                    AiStepResult stepResult;

                    try
                    {
                        stepResult = await _claimedStepExecutor.ExecuteAsync(
                            record,
                            state,
                            resolvedPipeline,
                            claimed,
                            buildExecutionContext,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await _engineServices.ObservabilityService.Tracer.TraceStorageAsync(
                            new AiStorageTraceContext
                            {
                                ExecutionId = executionId,
                                StepId = claimed.StepName,
                                Backend = "Redis",
                                Operation = "TryFailStepException"
                            },
                            async trace =>
                            {
                                var result = await _engineServices.DagStore.TryFailStepAsync(
                                    executionId,
                                    claimed.StepName,
                                    claimed.ClaimToken,
                                    ex.Message,
                                    cancellationToken).ConfigureAwait(false);

                                trace.SetTag("failed", result);
                                trace.SetTag(AiWorkerMetadataKeys.CamelCaseWorkerId, workerId);
                                trace.SetTag(AiExecutionClaimMetadataKeys.CamelCaseClaimToken, claimed.ClaimToken);
                                trace.SetTag("errorType", ex.GetType().FullName);

                                return result;
                            }).ConfigureAwait(false);

                        _engineServices.ObservabilityService.Metrics.Execution.RecordStepFailed(
                            executionId,
                            claimed.StepName);

                        _engineServices.Logger.Engine.StepException(
                            record.ExecutionId,
                            claimed.StepName,
                            ex);

                        var failedState = await _engineServices.DagStore.GetStateAsync(
                            executionId,
                            cancellationToken).ConfigureAwait(false) ?? state;

                        var failedStepState = failedState.Steps.TryGetValue(
                            claimed.StepName,
                            out var reloadedFailedStep)
                            ? reloadedFailedStep
                            : null;

                        var claimedStep = resolvedPipeline.Steps.First(
                           x => x.Name == claimed.StepName);

                        await AiDagExecutionHelpers.RecordRetryLedgerEventsAsync(
                                 _engineServices,
                                 executionId,
                                 pipelineKey,
                                 claimedStep.Name,
                                 claimedStep.StepKey,
                                 workerId,
                                 claimed.ClaimToken,
                                 failedStepState,
                                 ex.Message,
                                 "exception",
                                 cancellationToken)
                             .ConfigureAwait(false);

                        var executionContext = buildExecutionContext(
                            record,
                            failedState,
                            cancellationToken);

                        var stepContext = new AiStepExecutionContext(
                            executionContext,
                            claimedStep);

                        await _retentionCoordinator.ApplyRetentionPersistAndWarmAsync(
                            executionId,
                            failedState,
                            stepContext,
                            cancellationToken).ConfigureAwait(false);

                        record.Steps = resolvedPipeline.Steps
                            .Select(x => x.Name)
                            .ToList();

                        var convergence = await AiDagExecutionConvergenceEvaluator.EvaluateAsync(
                            resolvedPipeline,
                            failedState,
                            _engineServices.StateWriter,
                            _engineServices.StepResolver,
                            DateTime.UtcNow,
                            cancellationToken).ConfigureAwait(false);

                        AiDagExecutionRecordFinalizer.ApplyConvergenceToRecord(
                            record,
                            convergence,
                            failedState,
                            AiDagExecutionHelpers.GetDeclaredStepNames(resolvedPipeline));

                        var expectedStepKey = record.ExecutionStepKey;

                        if (string.IsNullOrWhiteSpace(expectedStepKey))
                        {
                            throw new InvalidOperationException(
                                "ExecutionStepKey must be set before persisting execution state.");
                        }

                        await _finalizationService.PersistDistributedConvergedRecordAsync(
                            record,
                            convergence,
                            expectedStepKey,
                            failedState,
                            resolvedPipeline,
                            buildExecutionContext,
                            persistAsync,
                            cancellationToken).ConfigureAwait(false);

                        if (record.IsTerminal)
                        {
                            _engineServices.Logger.Engine.ExecutionCompleted(record);

                            _engineServices.ObservabilityService.Metrics.Execution.RecordExecutionCompleted(
                                record.ExecutionId);

                            await _lifecycleHelper.EnsureTerminalLifecycleAsync(
                                record,
                                failedState,
                                cancellationToken).ConfigureAwait(false);
                        }

                        throw;
                    }

                    if (stepResult.EffectiveOutcome == AiStepExecutionOutcome.Park)
                    {
                        var parked = await _engineServices.ObservabilityService.Tracer.TraceStorageAsync(
                            new AiStorageTraceContext
                            {
                                ExecutionId = executionId,
                                StepId = claimed.StepName,
                                Backend = "Redis",
                                Operation = "TryParkStep"
                            },
                            async trace =>
                            {
                                var result = await _engineServices.DagStore.TryParkStepAsync(
                                    executionId,
                                    claimed.StepName,
                                    claimed.ClaimToken,
                                    cancellationToken).ConfigureAwait(false);

                                trace.SetTag("parked", result);
                                trace.SetTag(AiWorkerMetadataKeys.CamelCaseWorkerId, workerId);
                                trace.SetTag(AiExecutionClaimMetadataKeys.CamelCaseClaimToken, claimed.ClaimToken);

                                return result;
                            }).ConfigureAwait(false);

                        if (!parked)
                        {
                            throw new InvalidOperationException(
                                $"Failed to park claimed step '{claimed.StepName}' for execution '{executionId}'.");
                        }

                        _engineServices.Logger.Engine.LogInformation(
                            $"[AI DAG] Step parked for external wait. ExecutionId='{record.ExecutionId}', StepName='{claimed.StepName}', Worker='{workerId}'.");

                        concurrencyLeaseReleased = await TryReleaseConcurrencyLeaseAsync(
                            executionId,
                            pipelineKey,
                            claimed.StepName,
                            claimed.ClaimToken,
                            workerId,
                            state,
                            resolvedPipeline).ConfigureAwait(false);

                        state = await _engineServices.DagStore.GetStateAsync(
                            executionId,
                            cancellationToken).ConfigureAwait(false) ?? state;
                    }
                    else if (!stepResult.Success)
                    {
                        await _engineServices.ObservabilityService.Tracer.TraceStorageAsync(
                            new AiStorageTraceContext
                            {
                                ExecutionId = executionId,
                                StepId = claimed.StepName,
                                Backend = "Redis",
                                Operation = "TryFailStepResult"
                            },
                            async trace =>
                            {
                                var result = await _engineServices.DagStore.TryFailStepAsync(
                                    executionId,
                                    claimed.StepName,
                                    claimed.ClaimToken,
                                    stepResult.Error,
                                    cancellationToken).ConfigureAwait(false);

                                trace.SetTag("failed", result);
                                trace.SetTag(AiWorkerMetadataKeys.CamelCaseWorkerId, workerId);
                                trace.SetTag(AiExecutionClaimMetadataKeys.CamelCaseClaimToken, claimed.ClaimToken);
                                trace.SetTag("error", stepResult.Error);

                                return result;
                            }).ConfigureAwait(false);

                        _engineServices.ObservabilityService.Metrics.Execution.RecordStepFailed(
                            executionId,
                            claimed.StepName);

                        _engineServices.Logger.Engine.StepFailed(
                            record.ExecutionId,
                            claimed.StepName,
                            stepResult.Error);

                        var failedState = await _engineServices.DagStore.GetStateAsync(
                            executionId,
                            cancellationToken).ConfigureAwait(false) ?? state;

                        var failedStepState = failedState.Steps.TryGetValue(
                            claimed.StepName,
                            out var reloadedFailedStep)
                            ? reloadedFailedStep
                            : null;

                        var claimedStep = AiDagExecutionHelpers.FindPipelineStep(resolvedPipeline, claimed.StepName);

                        await AiDagExecutionHelpers.RecordRetryLedgerEventsAsync(
                                _engineServices,
                                executionId,
                                pipelineKey,
                                claimedStep.Name,
                                claimedStep.StepKey,
                                workerId,
                                claimed.ClaimToken,
                                failedStepState,
                                stepResult.Error,
                                "result",
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (failedStepState?.Status == AiStepExecutionStatus.WaitingForRetry)
                        {
                            _engineServices.ObservabilityService.Metrics.Execution.RecordStepRetried(
                                executionId,
                                claimed.StepName);

                            _engineServices.Logger.Engine.StepRetryScheduled(
                                record.ExecutionId,
                                claimed.StepName,
                                failedStepState?.RetryState?.RetryCount ?? 0,
                                failedStepState?.RetryState?.NextRetryAtUtc);
                        }

                        state = failedState;
                    }
                    else
                    {
                        var completed = await _engineServices.ObservabilityService.Tracer.TraceStorageAsync(
                            new AiStorageTraceContext
                            {
                                ExecutionId = executionId,
                                StepId = claimed.StepName,
                                Backend = "Redis",
                                Operation = "TryCompleteStep"
                            },
                            async trace =>
                            {
                                var result = await _engineServices.DagStore.TryCompleteStepAsync(
                                    executionId,
                                    claimed.StepName,
                                    claimed.ClaimToken,
                                    stepResult,
                                    cancellationToken).ConfigureAwait(false);

                                trace.SetTag("completed", result);
                                trace.SetTag(AiWorkerMetadataKeys.CamelCaseWorkerId, workerId);
                                trace.SetTag(AiExecutionClaimMetadataKeys.CamelCaseClaimToken, claimed.ClaimToken);

                                return result;
                            }).ConfigureAwait(false);

                        if (!completed)
                        {
                            throw new InvalidOperationException(
                                $"Failed to complete claimed step '{claimed.StepName}' for execution '{executionId}'.");
                        }

                        _engineServices.Logger.Engine.StepCompleted(
                            record,
                            claimed.StepName);

                        _engineServices.ObservabilityService.Metrics.Execution.RecordStepCompleted(
                            executionId,
                            claimed.StepName);

                        var completedState = await _engineServices.DagStore.GetStateAsync(
                            executionId,
                            cancellationToken).ConfigureAwait(false) ?? state;

                        await PublishDagProgressChangedAsync(
                                record,
                                completedState,
                                resolvedPipeline.Steps.Count,
                                workerId)
                            .ConfigureAwait(false);

                        var claimedStep = resolvedPipeline.Steps.First(
                            x => x.Name == claimed.StepName);

                        var executionContext = buildExecutionContext(
                            record,
                            completedState,
                            cancellationToken);

                        var stepContext = new AiStepExecutionContext(
                            executionContext,
                            claimedStep);

                        await _retentionCoordinator.ApplyRetentionPersistAndWarmAsync(
                            executionId,
                            completedState,
                            stepContext,
                            cancellationToken).ConfigureAwait(false);

                        state = completedState;
                    }

                    var finalState = await _engineServices.DagStore.GetStateAsync(
                        executionId,
                        cancellationToken).ConfigureAwait(false) ?? state;

                    record.Steps = resolvedPipeline.Steps
                        .Select(x => x.Name)
                        .ToList();

                    var finalConvergence = await AiDagExecutionConvergenceEvaluator.EvaluateAsync(
                        resolvedPipeline,
                        finalState,
                        _engineServices.StateWriter,
                        _engineServices.StepResolver,
                        DateTime.UtcNow,
                        cancellationToken).ConfigureAwait(false);

                    _engineServices.Logger.Engine.LogInformation(
                        $"[AI DAG] Distributed convergence evaluated. ExecutionId='{record.ExecutionId}', ConvergenceStatus='{finalConvergence.Status}'.");

                    AiDagExecutionRecordFinalizer.ApplyConvergenceToRecord(
                        record,
                        finalConvergence,
                        finalState,
                        AiDagExecutionHelpers.GetDeclaredStepNames(resolvedPipeline));

                    if (record.Status == AiExecutionStatus.Failed)
                    {
                        _engineServices.ObservabilityService.Metrics.Execution.RecordExecutionFailed(
                            record.ExecutionId);
                    }

                    var expectedStepKeyFinal = record.ExecutionStepKey;

                    if (string.IsNullOrWhiteSpace(expectedStepKeyFinal))
                    {
                        throw new InvalidOperationException(
                            "ExecutionStepKey must be set before persisting execution state.");
                    }

                    await _finalizationService.PersistDistributedConvergedRecordAsync(
                        record,
                        finalConvergence,
                        expectedStepKeyFinal,
                        finalState,
                        resolvedPipeline,
                        buildExecutionContext,
                        persistAsync,
                        cancellationToken).ConfigureAwait(false);

                    if (record.IsTerminal)
                    {
                        _engineServices.Logger.Engine.ExecutionCompleted(record);

                        _engineServices.ObservabilityService.Metrics.Execution.RecordExecutionCompleted(
                            record.ExecutionId);

                        await _lifecycleHelper.EnsureTerminalLifecycleAsync(
                            record,
                            finalState,
                            cancellationToken).ConfigureAwait(false);
                    }

                    return record;
                }
                finally
                {
                    if (!concurrencyLeaseReleased)
                    {
                        await TryReleaseConcurrencyLeaseAsync(
                            executionId,
                            pipelineKey,
                            claimed.StepName,
                            claimed.ClaimToken,
                            workerId,
                            state,
                            resolvedPipeline)
                            .ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _engineServices.Accessor.Clear();
            }
        }

        /// <summary>
        /// Publishes a best-effort signal after a durable DAG step completion.
        /// </summary>
        /// <param name="record">The execution record.</param>
        /// <param name="state">The reconstructed durable DAG state.</param>
        /// <param name="totalStepCount">The total configured DAG step count.</param>
        /// <param name="runtimeInstanceId">The runtime instance progressing the execution.</param>
        /// <returns>A task representing the asynchronous signal publication.</returns>
        private async Task PublishDagProgressChangedAsync(
            AiExecutionRecord record,
            AiExecutionState state,
            int totalStepCount,
            string runtimeInstanceId)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            if (_runtimeSignalPublisher is null)
            {
                _engineServices.Logger.Engine.LogWarning(
                    $"[AI RUNTIME SIGNAL][PUBLISHER UNAVAILABLE] " +
                    $"IAiRuntimeSignalPublisher could not be injected into the distributed DAG runner. " +
                    $"ExecutionId='{record.ExecutionId}', RuntimeInstanceId='{runtimeInstanceId}'.");

                return;
            }

            if (_controlPlaneIdResolver is null)
            {
                _engineServices.Logger.Engine.LogWarning(
                    $"[AI RUNTIME SIGNAL][CONTROL PLANE RESOLVER UNAVAILABLE] " +
                    $"IAiControlPlaneIdResolver could not be injected into the distributed DAG runner. " +
                    $"ExecutionId='{record.ExecutionId}', RuntimeInstanceId='{runtimeInstanceId}'.");

                return;
            }

            try
            {
                var controlPlaneId = await _controlPlaneIdResolver
                    .ResolveAsync(
                        new AiControlPlaneIdResolutionRequest
                        {
                            Source = "dag-progress-changed-signal",
                            AllowGeneratedFallback = false
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(controlPlaneId))
                {
                    _engineServices.Logger.Engine.LogWarning(
                        $"[AI RUNTIME SIGNAL][CONTROL PLANE ID UNRESOLVED] " +
                        $"DAG progress signal was not published because the logical control-plane id could not be resolved. " +
                        $"ExecutionId='{record.ExecutionId}', RuntimeInstanceId='{runtimeInstanceId}'.");

                    return;
                }

                var completedStepCount = state.Steps.Values.Count(
                    step => step.Status == AiStepExecutionStatus.Completed);

                var signal = new AiRuntimeSignal
                {
                    Type = AiRuntimeSignalType.DagProgressChanged,
                    ControlPlaneId = controlPlaneId,
                    TenantId = record.ExecutionContextSnapshot?.TenantId,
                    ExecutionId = record.ExecutionId,
                    RuntimeInstanceId = runtimeInstanceId,
                    CompletedStepCount = completedStepCount,
                    TotalStepCount = totalStepCount,
                    ExecutionVersion = record.Version
                };

                _engineServices.Logger.Engine.LogInformation(
                    $"[AI RUNTIME SIGNAL][DAG PUBLISH REQUESTED] " +
                    $"ControlPlaneId='{signal.ControlPlaneId}', ExecutionId='{signal.ExecutionId}', " +
                    $"RuntimeInstanceId='{signal.RuntimeInstanceId}', CompletedStepCount='{signal.CompletedStepCount}', " +
                    $"TotalStepCount='{signal.TotalStepCount}', ExecutionVersion='{signal.ExecutionVersion}'.");

                await _runtimeSignalPublisher
                    .PublishAsync(
                        signal,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                _engineServices.Logger.Engine.LogInformation(
                    $"[AI RUNTIME SIGNAL][DAG PUBLISH COMPLETED] " +
                    $"ControlPlaneId='{signal.ControlPlaneId}', ExecutionId='{signal.ExecutionId}', " +
                    $"RuntimeInstanceId='{signal.RuntimeInstanceId}', CompletedStepCount='{signal.CompletedStepCount}'.");
            }
            catch (Exception exception)
            {
                /*
                 * Runtime signals are wake-up notifications only. A signal
                 * publication or identity-resolution failure must never invalidate
                 * the durable step completion that already succeeded.
                 */
                _engineServices.Logger.Engine.LogWarning(
                    $"[AI RUNTIME SIGNAL][DAG PUBLISH FAILED] " +
                    $"ExecutionId='{record.ExecutionId}', RuntimeInstanceId='{runtimeInstanceId}', " +
                    $"ExceptionType='{exception.GetType().FullName}', ExceptionMessage='{exception.Message}'.");
            }
        }

        /// <summary>
        /// Creates a stable pipeline key used by distributed concurrency scopes.
        /// </summary>
        /// <param name="pipeline">The resolved AI pipeline.</param>
        /// <returns>A stable pipeline key.</returns>
        /// <remarks>
        /// <para>
        /// The key must be stable across multiple executions of the same pipeline.
        /// This allows pipeline-level throttling to apply globally across executions.
        /// </para>
        ///
        /// <para>
        /// When a pipeline version is available, the key uses <c>{Name}:{Version}</c>.
        /// Otherwise, it falls back to the pipeline name.
        /// </para>
        /// </remarks>
        private static string CreatePipelineKey(ResolvedAiPipeline pipeline)
        {
            ArgumentNullException.ThrowIfNull(pipeline);

            if (string.IsNullOrWhiteSpace(pipeline.Name))
            {
                throw new InvalidOperationException(
                    "Resolved pipeline name is required to build a stable concurrency pipeline key.");
            }

            return string.IsNullOrWhiteSpace(pipeline.Version)
                ? pipeline.Name
                : $"{pipeline.Name}:{pipeline.Version}";
        }

        /// <summary>
        /// Releases a claimed-step concurrency lease without allowing release or ledger
        /// failures to invalidate an already-persisted step transition.
        /// </summary>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="pipelineKey">The stable pipeline key used during admission.</param>
        /// <param name="stepName">The claimed step name.</param>
        /// <param name="claimToken">The claim token associated with the physical step attempt.</param>
        /// <param name="workerId">The worker identifier used during admission.</param>
        /// <param name="state">The execution state containing the step configuration.</param>
        /// <param name="pipeline">The resolved pipeline used for concurrency definition resolution.</param>
        /// <returns><c>true</c> when release completed successfully; otherwise <c>false</c>.</returns>
        /// <remarks>
        /// Release is best-effort. If release cannot complete, the Redis ZSET lease model
        /// still recovers capacity after lease expiration. Parked steps attempt release before
        /// convergence so durable suspension returns capacity as early as safely possible.
        /// </remarks>
        private async Task<bool> TryReleaseConcurrencyLeaseAsync(
            string executionId,
            string pipelineKey,
            string stepName,
            string claimToken,
            string workerId,
            AiExecutionState state,
            ResolvedAiPipeline pipeline)
        {
            Console.WriteLine(
                $"[AI DAG DISTRIBUTED PHASE] Phase='lease-release-started', ExecutionId='{executionId}', StepName='{stepName}', ClaimToken='{claimToken}', WorkerId='{workerId}', PipelineKey='{pipelineKey}'.");

            try
            {
                await ReleaseConcurrencyLeaseAsync(
                        executionId,
                        pipelineKey,
                        stepName,
                        claimToken,
                        workerId,
                        state,
                        pipeline,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                Console.WriteLine(
                    $"[AI DAG DISTRIBUTED PHASE] Phase='lease-release-completed', ExecutionId='{executionId}', StepName='{stepName}', ClaimToken='{claimToken}', WorkerId='{workerId}', PipelineKey='{pipelineKey}'.");

                return true;
            }
            catch (Exception exception)
            {
                Console.WriteLine(
                    $"[AI DAG DISTRIBUTED PHASE] Phase='lease-release-failed', ExecutionId='{executionId}', StepName='{stepName}', ClaimToken='{claimToken}', WorkerId='{workerId}', PipelineKey='{pipelineKey}', ExceptionType='{exception.GetType().FullName}', Message='{exception.Message}'.");

                return false;
            }
        }

        /// <summary>
        /// Releases the distributed concurrency lease associated with a claimed step.
        /// </summary>
        private async Task ReleaseConcurrencyLeaseAsync(
            string executionId,
            string pipelineKey,
            string stepName,
            string claimToken,
            string workerId,
            AiExecutionState state,
            ResolvedAiPipeline pipeline,
            CancellationToken cancellationToken)
        {
            if (!state.Steps.TryGetValue(stepName, out var stepState))
            {
                return;
            }

            var stepDefinition = pipeline.Steps.FirstOrDefault(x =>
                string.Equals(x.Name, stepName, StringComparison.OrdinalIgnoreCase));

            if (stepDefinition is null)
            {
                return;
            }

            var pipelineStepDefinition = new AiPipelineStepDefinition
            {
                Name = stepDefinition.Name,
                StepKey = stepDefinition.StepKey,
                Config = stepDefinition.Config ?? new Dictionary<string, object?>(),
                DependsOn = stepDefinition.DependsOn ?? Array.Empty<string>()
            };

            var concurrencyAdmission =
                AiDagExecutionHelpers.CreateConcurrencyAdmission(
                    executionId,
                    pipelineKey,
                    stepName,
                    workerId,
                    stepState,
                    pipeline.Config,
                    pipelineStepDefinition,
                    ConcurrencyDefinitionResolver);

            if (!concurrencyAdmission.Definition.Enabled)
            {
                return;
            }

            await _engineServices.ConcurrencyGate.ReleaseAsync(
                    concurrencyAdmission.Context,
                    concurrencyAdmission.Definition,
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                        _engineServices,
                        executionId,
                        pipelineKey,
                        stepName,
                        concurrencyAdmission.Context.StepKey,
                        workerId,
                        claimToken,
                        concurrencyAdmission.Context,
                        AiDecisionLedgerCategory.Concurrency,
                        AiEngineEvents.Concurrency.LeaseReleased,
                        AiDecisionLedgerOutcome.Released,
                        "Concurrency lease released by the claim-owning DAG runner.",
                        new Dictionary<string, string>
                        {
                            [AiPipelineMetadataKeys.Name] = pipeline.Name ?? string.Empty,
                            [AiPipelineMetadataKeys.Version] = pipeline.Version ?? string.Empty,
                            [AiStepMetadataKeys.StepName] = stepName,
                            [AiStepMetadataKeys.StepKey] = concurrencyAdmission.Context.StepKey ?? string.Empty,
                            [AiWorkerMetadataKeys.WorkerId] = workerId,
                            [AiExecutionClaimMetadataKeys.ClaimToken] = claimToken,
                            [AiConcurrencyMetadataKeys.LeaseId] = concurrencyAdmission.Context.LeaseId ?? string.Empty
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Console.WriteLine(
                    $"[AI DAG DISTRIBUTED PHASE] Phase='lease-ledger-failed', ExecutionId='{executionId}', StepName='{stepName}', ClaimToken='{claimToken}', WorkerId='{workerId}', LeaseId='{concurrencyAdmission.Context.LeaseId}', ExceptionType='{exception.GetType().FullName}', Message='{exception.Message}'.");
            }
        }



    }
}