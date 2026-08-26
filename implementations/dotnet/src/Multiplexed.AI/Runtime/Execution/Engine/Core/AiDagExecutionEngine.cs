using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Pipeline;

namespace Multiplexed.AI.Runtime.Execution.Engine.Core
{
    /// <summary>
    /// Executes AI pipelines using DAG-based orchestration.
    /// </summary>
    public sealed class AiDagExecutionEngine : AiExecutionEngine
    {
        private readonly IAiDagExecutionEngineServices _engineServices;
        private readonly IAiDagExecutionEngineRuntimeServices _runtime;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiDagExecutionEngine"/> class.
        /// </summary>
        /// <param name="engineServices">
        /// The DAG execution engine services.
        /// </param>
        /// <param name="runtime">
        /// The composed DAG execution runtime services.
        /// </param>
        public AiDagExecutionEngine(
            IAiDagExecutionEngineServices engineServices,
            IAiDagExecutionEngineRuntimeServices runtime)
            : base(
                engineServices.Store,
                engineServices.ContextStore,
                engineServices.Accessor,
                engineServices.ContextFactory,
                engineServices.Services,
                engineServices.PipelineExecutor,
                engineServices.Logger,
                engineServices.StateReader,
                engineServices.StateWriter)
        {
            _engineServices = engineServices
                ?? throw new ArgumentNullException(nameof(engineServices));

            _runtime = runtime
                ?? throw new ArgumentNullException(nameof(runtime));
        }

        /// <inheritdoc />
        public override Task<AiExecutionRecord> CreateAsync(
            string pipelineName,
            string input,
            CancellationToken cancellationToken = default)
        {
            return _runtime.Creator.CreateAsync(
                pipelineName,
                input,
                cancellationToken);
        }

        /// <inheritdoc />
        public override Task<AiExecutionRecord> CreateAsync(
            string pipelineName,
            IDictionary<string, object?> input,
            CancellationToken cancellationToken = default)
        {
            return _runtime.Creator.CreateAsync(
                pipelineName,
                input,
                cancellationToken);
        }

        /// <summary>
        /// Creates the exact preallocated DAG execution from the supplied declarative definition when absent.
        /// </summary>
        /// <param name="executionId">The exact preallocated execution identifier.</param>
        /// <param name="definition">The exact declarative DAG definition to resolve.</param>
        /// <param name="pipelineDefinitionSnapshot">The verified immutable descriptor bound to this execution.</param>
        /// <param name="input">The string input payload.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The newly created or already existing authoritative execution record.</returns>
        public Task<AiExecutionRecord> CreateIfAbsentAsync(
            string executionId,
            AiPipelineDefinition definition,
            AiStoredPayload pipelineDefinitionSnapshot,
            string input,
            CancellationToken cancellationToken = default)
        {
            return _runtime.Creator.CreateIfAbsentAsync(
                executionId,
                definition,
                pipelineDefinitionSnapshot,
                input,
                cancellationToken);
        }

        /// <summary>
        /// Creates the exact preallocated DAG execution from the supplied declarative definition when absent.
        /// </summary>
        /// <param name="executionId">The exact preallocated execution identifier.</param>
        /// <param name="definition">The exact declarative DAG definition to resolve.</param>
        /// <param name="pipelineDefinitionSnapshot">The verified immutable descriptor bound to this execution.</param>
        /// <param name="input">The structured input values to seed into execution state.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The newly created or already existing authoritative execution record.</returns>
        public Task<AiExecutionRecord> CreateIfAbsentAsync(
            string executionId,
            AiPipelineDefinition definition,
            AiStoredPayload pipelineDefinitionSnapshot,
            IDictionary<string, object?> input,
            CancellationToken cancellationToken = default)
        {
            return _runtime.Creator.CreateIfAbsentAsync(
                executionId,
                definition,
                pipelineDefinitionSnapshot,
                input,
                cancellationToken);
        }

        /// <inheritdoc />
        public override async Task<AiExecutionRecord> ExecuteNextAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            if (_engineServices.DagStore is not null)
            {
                return await _runtime.DistributedRunner.ExecuteNextAsync(
                    executionId,
                    contextKey => LoadContextAndSetAsync(executionId, contextKey),
                    BuildExecutionContext,
                    PersistAsync,
                    EnsurePipelineName,
                    ValidateExecutionId,
                    cancellationToken);
            }

            return await _runtime.LocalRunner.ExecuteNextAsync(
                executionId,
                LoadExecutionAsync,
                contextKey => LoadContextAndSetAsync(executionId, contextKey),
                BuildExecutionContext,
                PersistAsync,
                EnsurePipelineName,
                ValidateExecutionId,
                cancellationToken);
        }

        /// <inheritdoc />
        public override async Task<AiExecutionRecord> ExecuteBatchAsync(
            string executionId,
            int maxSteps,
            CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxSteps, 1);

            if (_engineServices.DagStore is not null)
            {
                return await _runtime.BatchRunner.ExecuteBatchAsync(
                    executionId,
                    maxSteps,
                    contextKey => LoadContextAndSetAsync(executionId, contextKey),
                    BuildExecutionContext,
                    PersistAsync,
                    EnsurePipelineName,
                    ValidateExecutionId,
                    cancellationToken);
            }

            return await _runtime.LocalRunner.ExecuteNextAsync(
                executionId,
                LoadExecutionAsync,
                contextKey => LoadContextAndSetAsync(executionId, contextKey),
                BuildExecutionContext,
                PersistAsync,
                EnsurePipelineName,
                ValidateExecutionId,
                cancellationToken);
        }

        /// <summary>
        /// Reactivates one existing DAG step after its external durable condition has been satisfied.
        /// </summary>
        /// <remarks>
        /// This is a normal continuation boundary. It does not create an execution, acquire crash-recovery ownership,
        /// or increment retry or recovery counters. Duplicate physical delivery converges on the already advanced step.
        /// </remarks>
        /// <param name="executionId">The existing durable execution identifier.</param>
        /// <param name="stepName">The exact step that was parked in WaitingForExternal.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The authoritative execution record after the continuation transition or an idempotent redelivery.</returns>
        public async Task<AiExecutionRecord> ResumeExternalWaitingStepAsync(
            string executionId,
            string stepName,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(stepName);

            if (_engineServices.DagStore is not null)
            {
                var record = await _engineServices.DagStore
                    .GetRecordAsync(executionId, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Execution '{executionId}' was not found.");

                EnsureExternalWaitContinuationMode(record, stepName);

                var state = await _engineServices.DagStore
                    .GetStateAsync(executionId, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Execution state '{executionId}' was not found.");

                if (!state.Steps.TryGetValue(stepName, out var step))
                {
                    throw new InvalidOperationException(
                        $"Execution '{executionId}' does not contain step '{stepName}'.");
                }

                if (record.IsTerminal)
                {
                    EnsureTerminalExternalWaitRedeliveryCompatible(
                        record,
                        executionId,
                        stepName,
                        step.Status);

                    return record;
                }

                if (step.Status == AiStepExecutionStatus.WaitingForExternal)
                {
                    var resumed = await _engineServices.DagStore
                        .TryResumeExternalWaitingStepAsync(executionId, stepName, cancellationToken)
                        .ConfigureAwait(false);

                    if (resumed)
                    {
                        return record;
                    }

                    state = await _engineServices.DagStore
                        .GetStateAsync(executionId, cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidOperationException($"Execution state '{executionId}' disappeared during continuation.");

                    if (!state.Steps.TryGetValue(stepName, out step))
                    {
                        throw new InvalidOperationException(
                            $"Execution '{executionId}' lost step '{stepName}' during continuation.");
                    }
                }

                EnsureExternalWaitRedeliveryCompatible(executionId, stepName, step.Status);
                return record;
            }

            var localRecord = await _engineServices.Store
                .GetRecordAsync(executionId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Execution '{executionId}' was not found.");

            EnsureExternalWaitContinuationMode(localRecord, stepName);

            var localState = await _engineServices.Store
                .GetStateAsync(executionId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Execution state '{executionId}' was not found.");

            if (!localState.Steps.TryGetValue(stepName, out var localStep))
            {
                throw new InvalidOperationException(
                    $"Execution '{executionId}' does not contain step '{stepName}'.");
            }

            if (localRecord.IsTerminal)
            {
                EnsureTerminalExternalWaitRedeliveryCompatible(
                    localRecord,
                    executionId,
                    stepName,
                    localStep.Status);

                return localRecord;
            }

            if (localStep.Status == AiStepExecutionStatus.WaitingForExternal)
            {
                var expectedStepKey = localRecord.ExecutionStepKey;
                if (string.IsNullOrWhiteSpace(expectedStepKey))
                {
                    throw new InvalidOperationException(
                        $"Execution '{executionId}' does not contain an optimistic execution step key.");
                }

                localStep.MarkReadyFromExternalWait();
                localRecord.MarkRunning();
                localRecord.TouchVersion();
                localRecord.RenewExecutionStepKey();

                var updated = await _engineServices.Store
                    .TryUpdateAsync(
                        executionId,
                        expectedStepKey,
                        localRecord,
                        localState,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (updated)
                {
                    return localRecord;
                }

                localRecord = await _engineServices.Store
                    .GetRecordAsync(executionId, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Execution '{executionId}' disappeared during continuation.");

                localState = await _engineServices.Store
                    .GetStateAsync(executionId, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Execution state '{executionId}' disappeared during continuation.");

                if (!localState.Steps.TryGetValue(stepName, out localStep))
                {
                    throw new InvalidOperationException(
                        $"Execution '{executionId}' lost step '{stepName}' during continuation.");
                }
            }

            EnsureExternalWaitContinuationMode(localRecord, stepName);

            if (localRecord.IsTerminal)
            {
                EnsureTerminalExternalWaitRedeliveryCompatible(
                    localRecord,
                    executionId,
                    stepName,
                    localStep.Status);

                return localRecord;
            }

            EnsureExternalWaitRedeliveryCompatible(executionId, stepName, localStep.Status);
            return localRecord;
        }

        /// <summary>
        /// Validates that an execution uses the DAG mode required by external-wait continuation.
        /// </summary>
        /// <param name="record">The authoritative execution record.</param>
        /// <param name="stepName">The requested continuation step.</param>
        private static void EnsureExternalWaitContinuationMode(
            AiExecutionRecord record,
            string stepName)
        {
            if (record.ExecutionMode != AiExecutionMode.Dag)
            {
                throw new InvalidOperationException(
                    $"Execution '{record.ExecutionId}' is not a DAG execution and cannot continue external wait step '{stepName}'.");
            }
        }

        /// <summary>
        /// Accepts a late physical external-wait redelivery only when durable terminal state proves that the
        /// continuation has already been consumed by the same parent call-site.
        /// </summary>
        /// <remarks>
        /// A Completed/Failed parent with the target call-site already Completed/Failed is an idempotent no-op.
        /// The execution is never reopened and no step is re-executed. Cancelled parents and non-terminal
        /// call-sites remain invalid so unrelated terminal transitions cannot masquerade as continuation consumption.
        /// </remarks>
        /// <param name="record">The authoritative terminal execution record.</param>
        /// <param name="executionId">The requested execution identifier.</param>
        /// <param name="stepName">The exact external-wait call-site.</param>
        /// <param name="stepStatus">The authoritative call-site status.</param>
        private static void EnsureTerminalExternalWaitRedeliveryCompatible(
            AiExecutionRecord record,
            string executionId,
            string stepName,
            AiStepExecutionStatus stepStatus)
        {
            if (record.Status is AiExecutionStatus.Completed or AiExecutionStatus.Failed &&
                stepStatus is AiStepExecutionStatus.Completed or AiStepExecutionStatus.Failed)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Execution '{executionId}' is terminal and cannot continue external wait step '{stepName}'. " +
                $"ExecutionStatus='{record.Status}', StepStatus='{stepStatus}'.");
        }

        /// <summary>
        /// Accepts only statuses that prove the same external-wait continuation has already advanced physically.
        /// </summary>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="stepName">The step name.</param>
        /// <param name="status">The authoritative step status after a failed continuation CAS or redelivery.</param>
        private static void EnsureExternalWaitRedeliveryCompatible(
            string executionId,
            string stepName,
            AiStepExecutionStatus status)
        {
            if (status is AiStepExecutionStatus.Ready or
                AiStepExecutionStatus.Running or
                AiStepExecutionStatus.WaitingForRetry or
                AiStepExecutionStatus.Completed or
                AiStepExecutionStatus.Failed)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Execution '{executionId}' step '{stepName}' cannot be continued from status '{status}'.");
        }

        /// <summary>
        /// Determines whether a globally waiting DAG execution is blocked specifically by
        /// an external durable step wait and can therefore release its runtime worker.
        /// </summary>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// <c>true</c> when at least one step is waiting externally and no running or
        /// retry-timed step still requires the current worker loop; otherwise <c>false</c>.
        /// </returns>
        /// <remarks>
        /// This distinction preserves existing timed-retry behavior. A global
        /// <see cref="AiExecutionStatus.Waiting"/> caused only by a future retry must keep its
        /// worker loop alive, while <see cref="AiStepExecutionStatus.WaitingForExternal"/> has no
        /// autonomous timer and must release runtime capacity until a durable continuation arrives.
        /// </remarks>
        public async Task<bool> ShouldReleaseForExternalWaitAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            var state = _engineServices.DagStore is not null
                ? await _engineServices.DagStore
                    .GetStateAsync(executionId, cancellationToken)
                    .ConfigureAwait(false)
                : await _engineServices.Store
                    .GetStateAsync(executionId, cancellationToken)
                    .ConfigureAwait(false);

            if (state is null)
            {
                return false;
            }

            var steps = state.Steps.Values;

            var hasExternalWait = steps.Any(step =>
                step.Status == AiStepExecutionStatus.WaitingForExternal);

            if (!hasExternalWait)
            {
                return false;
            }

            return !steps.Any(step =>
                step.Status is AiStepExecutionStatus.Running or
                    AiStepExecutionStatus.WaitingForRetry);
        }

        /// <inheritdoc />
        public override async Task<AiExecutionRecord> ExecuteAllAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            return await _engineServices.ObservabilityService.Tracer.TraceExecutionAsync(
                new AiExecutionTraceContext
                {
                    ExecutionId = executionId,
                    ExecutionMode = "Dag",
                    Status = "Running",
                    WorkerId = _engineServices.RuntimeInstanceIdentity.RuntimeInstanceId
                },
                async () =>
                {
                    AiExecutionRecord record;

                    do
                    {
                        record = await ExecuteNextAsync(
                            executionId,
                            cancellationToken);

                        if (record.Status == AiExecutionStatus.Waiting)
                        {
                            _engineServices.Logger.Engine.LogInformation(
                                $"[AI DAG] ExecuteAll stopped in Waiting. ExecutionId='{record.ExecutionId}', Status='{record.Status}'.");

                            return record;
                        }
                    }
                    while (!record.IsTerminal);

                    _engineServices.Logger.Engine.LogInformation(
                        $"[AI DAG] ExecuteAll reached terminal state. ExecutionId='{record.ExecutionId}', Status='{record.Status}'.");

                    return record;
                });
        }

        /// <summary>
        /// Loads the RBAC execution context required by the DAG state and sets it on the accessor.
        /// </summary>
        /// <param name="executionId">The durable execution identifier being advanced.</param>
        /// <param name="contextKey">The context key stored on the execution or DAG state.</param>
        private async Task LoadContextAndSetAsync(
            string executionId,
            string contextKey)
        {
            var rbacContext =
                await LoadContextForExecutionAsync(
                        executionId,
                        contextKey)
                    .ConfigureAwait(false);

            Accessor.Set(
                rbacContext);
        }
    }
}
