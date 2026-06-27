using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.ExecutionAssistance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Observability.Helpers;
using Multiplexed.AI.Runtime.Observability.Logging;
using Multiplexed.Rbac.Core.ExecutionContext;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using ExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.AI.Runtime.Execution.Instance.Worker
{
    /// <summary>
    /// Default implementation of <see cref="IAiRuntimePipelineBackgroundController"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The controller owns a bounded queue of pipeline run requests and processes
    /// them in the background.
    /// </para>
    /// <para>
    /// Normal queued requests create one new runtime execution and therefore one
    /// distinct execution identifier.
    /// </para>
    /// <para>
    /// Controlled recovery resume requests are the only supported exception. They
    /// explicitly target an existing durable execution identifier and must not call
    /// the execution creation path again.
    /// </para>
    /// <para>
    /// The execution identifier remains the namespace for the execution record, DAG
    /// state, step states, retention artifacts, externalized payloads, resolver
    /// indexes, snapshots, and replay data.
    /// </para>
    /// <para>
    /// The controller limits the number of active pipeline runs through
    /// <see cref="AiRuntimePipelineBackgroundControllerOptions.MaxConcurrentRuns"/>.
    /// This is controller-level parallelism only. Distributed step-level concurrency
    /// remains controlled by the runtime concurrency engine and Redis gate.
    /// </para>
    /// <para>
    /// Queue-level control is intentionally separate from execution-level control.
    /// Pausing the queue prevents new queued runs from starting but does not pause
    /// already running executions.
    /// </para>
    /// </remarks>
    public sealed class AiRuntimePipelineBackgroundController : IAiRuntimePipelineBackgroundController
    {
        private const string PipelineBackgroundControllerWorkerId =
            "pipeline-background-controller";

        private readonly AiDagExecutionEngine _engine;
        private readonly IAiRuntimeInstanceWorker _worker;
        private readonly IAiRuntimeInstanceWorkerGroup _workerGroup;
        private readonly IAiRuntimeInstanceWorkerFactory _workerFactory;
        private readonly IAiRuntimePipelineRunDefinitionResolver _definitionResolver;
        private readonly IAiRuntimePipelineRunDefinitionPublisher _definitionPublisher;
        private readonly IAiRuntimePipelineRunLifecycleHook _runLifecycleHook;
        private readonly IAiRuntimeLogger _logger;
        private readonly IAiRuntimeObservability _observability;
        private readonly IAiExecutionControlService _executionControlService;
        private readonly IAiRuntimeInstanceIdentityDescriptor _runtimeInstanceIdentity;
        private readonly IAiExecutionAssistanceCandidateStore _assistanceCandidateStore;
        private readonly IAiRuntimeRunExecutionIndex _runExecutionIndex;
        private readonly IExecutionContextAccessor _executionContextAccessor;
        private readonly IAiRuntimeRecoveryForensicsRecorder _forensicsRecorder;

        private readonly AiRuntimePipelineBackgroundControllerOptions _options;
        private readonly Channel<AiRuntimeQueuedPipelineRun> _queue;
        private readonly SemaphoreSlim _parallelismGate;
        private readonly ConcurrentDictionary<string, Task> _activeRuns = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, AiRuntimeQueuedPipelineRun> _queuedRuns = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, AiRuntimeQueuedPipelineRun> _runningRuns = new(StringComparer.Ordinal);
        private readonly object _sync = new();

        private CancellationTokenSource? _controllerCancellation;
        private Task? _controllerTask;
        private bool _started;
        private bool _stopped;

        private volatile bool _queuePaused;
        private string? _queuePauseReason;
        private string? _queuePauseRequestedBy;
        private DateTime? _queuePausedAtUtc;

        private int _activeWorkerCount;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimePipelineBackgroundController"/> class.
        /// </summary>
        /// <param name="engine">The DAG execution engine used to create executions.</param>
        /// <param name="worker">The runtime instance worker used to advance created executions.</param>
        /// <param name="workerGroup">The runtime instance worker group used for distributed multi-instance execution.</param>
        /// <param name="workerFactory">The runtime instance worker factory used to create distributed workers.</param>
        /// <param name="definitionResolver">The pipeline run definition resolver.</param>
        /// <param name="definitionPublisher">The pipeline run definition publisher.</param>
        /// <param name="runLifecycleHook">The pipeline run lifecycle hook.</param>
        /// <param name="executionControlService">The execution control service.</param>
        /// <param name="runtimeInstanceIdentity">The runtime instance identity of the controller host.</param>
        /// <param name="logger">The runtime logger.</param>
        /// <param name="observability">The runtime observability facade.</param>
        /// <param name="assistanceCandidateStore">The execution assistance candidate store.</param>
        /// <param name="runExecutionIndex">The runtime run execution index.</param>
        /// <param name="executionContextAccessor">The RBAC execution context accessor used to restore durable snapshots for background runs.</param>
        /// <param name="options">The controller options.</param>
        public AiRuntimePipelineBackgroundController(
            AiDagExecutionEngine engine,
            IAiRuntimeInstanceWorker worker,
            IAiRuntimeInstanceWorkerGroup workerGroup,
            IAiRuntimeInstanceWorkerFactory workerFactory,
            IAiRuntimePipelineRunDefinitionResolver definitionResolver,
            IAiRuntimePipelineRunDefinitionPublisher definitionPublisher,
            IAiRuntimePipelineRunLifecycleHook runLifecycleHook,
            IAiExecutionControlService executionControlService,
            IAiRuntimeInstanceIdentityDescriptor runtimeInstanceIdentity,
            IAiRuntimeLogger logger,
            IAiRuntimeObservability observability,
            IAiExecutionAssistanceCandidateStore assistanceCandidateStore,
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            IExecutionContextAccessor executionContextAccessor,
            IOptions<AiRuntimePipelineBackgroundControllerOptions> options)
            : this(
                engine,
                worker,
                workerGroup,
                workerFactory,
                definitionResolver,
                definitionPublisher,
                runLifecycleHook,
                executionControlService,
                runtimeInstanceIdentity,
                logger,
                observability,
                assistanceCandidateStore,
                runExecutionIndex,
                executionContextAccessor,
                options,
                new NoopAiRuntimeRecoveryForensicsRecorder())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimePipelineBackgroundController"/> class.
        /// </summary>
        /// <param name="engine">The DAG execution engine used to create executions.</param>
        /// <param name="worker">The runtime instance worker used to advance created executions.</param>
        /// <param name="workerGroup">The runtime instance worker group used for distributed multi-instance execution.</param>
        /// <param name="workerFactory">The runtime instance worker factory used to create distributed workers.</param>
        /// <param name="definitionResolver">The pipeline run definition resolver.</param>
        /// <param name="definitionPublisher">The pipeline run definition publisher.</param>
        /// <param name="runLifecycleHook">The pipeline run lifecycle hook.</param>
        /// <param name="executionControlService">The execution control service.</param>
        /// <param name="runtimeInstanceIdentity">The runtime instance identity of the controller host.</param>
        /// <param name="logger">The runtime logger.</param>
        /// <param name="observability">The runtime observability facade.</param>
        /// <param name="assistanceCandidateStore">The execution assistance candidate store.</param>
        /// <param name="runExecutionIndex">The runtime run execution index.</param>
        /// <param name="executionContextAccessor">The RBAC execution context accessor used to restore durable snapshots for background runs.</param>
        /// <param name="options">The controller options.</param>
        /// <param name="forensicsRecorder">The runtime recovery forensics recorder.</param>
        public AiRuntimePipelineBackgroundController(
            AiDagExecutionEngine engine,
            IAiRuntimeInstanceWorker worker,
            IAiRuntimeInstanceWorkerGroup workerGroup,
            IAiRuntimeInstanceWorkerFactory workerFactory,
            IAiRuntimePipelineRunDefinitionResolver definitionResolver,
            IAiRuntimePipelineRunDefinitionPublisher definitionPublisher,
            IAiRuntimePipelineRunLifecycleHook runLifecycleHook,
            IAiExecutionControlService executionControlService,
            IAiRuntimeInstanceIdentityDescriptor runtimeInstanceIdentity,
            IAiRuntimeLogger logger,
            IAiRuntimeObservability observability,
            IAiExecutionAssistanceCandidateStore assistanceCandidateStore,
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            IExecutionContextAccessor executionContextAccessor,
            IOptions<AiRuntimePipelineBackgroundControllerOptions> options,
            IAiRuntimeRecoveryForensicsRecorder forensicsRecorder)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _worker = worker ?? throw new ArgumentNullException(nameof(worker));
            _workerGroup = workerGroup ?? throw new ArgumentNullException(nameof(workerGroup));
            _workerFactory = workerFactory ?? throw new ArgumentNullException(nameof(workerFactory));
            _definitionResolver = definitionResolver ?? throw new ArgumentNullException(nameof(definitionResolver));
            _definitionPublisher = definitionPublisher ?? throw new ArgumentNullException(nameof(definitionPublisher));
            _runLifecycleHook = runLifecycleHook ?? throw new ArgumentNullException(nameof(runLifecycleHook));
            _executionControlService = executionControlService ?? throw new ArgumentNullException(nameof(executionControlService));
            _runtimeInstanceIdentity = runtimeInstanceIdentity ?? throw new ArgumentNullException(nameof(runtimeInstanceIdentity));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _observability = observability ?? throw new ArgumentNullException(nameof(observability));
            _assistanceCandidateStore = assistanceCandidateStore ?? throw new ArgumentNullException(nameof(assistanceCandidateStore));
            _runExecutionIndex = runExecutionIndex ?? throw new ArgumentNullException(nameof(runExecutionIndex));
            _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _forensicsRecorder = forensicsRecorder ?? throw new ArgumentNullException(nameof(forensicsRecorder));

            var queueCapacity = Math.Max(1, _options.QueueCapacity);
            var maxConcurrentRuns = Math.Max(1, _options.MaxConcurrentRuns);

            _queue = Channel.CreateBounded<AiRuntimeQueuedPipelineRun>(
                new BoundedChannelOptions(queueCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false
                });

            _parallelismGate = new SemaphoreSlim(
                maxConcurrentRuns,
                maxConcurrentRuns);
        }

        /// <inheritdoc />
        public Task StartAsync(
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (_started)
                {
                    return Task.CompletedTask;
                }

                if (_stopped)
                {
                    throw new InvalidOperationException(
                        "The runtime pipeline background controller cannot be restarted after it has been stopped.");
                }

                _controllerCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                _controllerTask = Task.Run(
                    () => RunControllerLoopAsync(_controllerCancellation.Token),
                    CancellationToken.None);

                _started = true;
            }

            _logger.Engine.LogInformation(
                $"[AI PIPELINE CONTROLLER] Started. MaxConcurrentRuns='{_options.MaxConcurrentRuns}', QueueCapacity='{_options.QueueCapacity}'.");

            Console.WriteLine(
                $"[AI PIPELINE CONTROLLER] START CALLED ControllerHash='{GetHashCode()}' RuntimeInstanceId='{_runtimeInstanceIdentity.RuntimeInstanceId}'");

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StopAsync(
            CancellationToken cancellationToken = default)
        {
            Task? controllerTask;

            lock (_sync)
            {
                if (_stopped)
                {
                    return;
                }

                _stopped = true;
                _queue.Writer.TryComplete();
                _controllerCancellation?.Cancel();

                controllerTask = _controllerTask;
            }

            _logger.Engine.LogInformation(
                "[AI PIPELINE CONTROLLER] Stop requested.");

            if (controllerTask is not null)
            {
                try
                {
                    await controllerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when the controller is stopped through cancellation.
                }
            }

            if (!_activeRuns.IsEmpty)
            {
                try
                {
                    await Task.WhenAll(_activeRuns.Values).WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when active runs are cancelled during shutdown.
                }
            }

            _controllerCancellation?.Dispose();

            _logger.Engine.LogInformation(
                "[AI PIPELINE CONTROLLER] Stopped.");
        }

        /// <inheritdoc />
        public ValueTask<AiRuntimeWorkerRunHandle> EnqueueAsync(
            AiRuntimePipelineRunRequest request,
            CancellationToken cancellationToken = default)
        {
            return EnqueueCoreAsync(
                request,
                resumeExecutionId: null,
                cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask<AiRuntimeWorkerRunHandle> EnqueueResumeAsync(
            AiRuntimePipelineRunRequest request,
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            return EnqueueCoreAsync(
                request,
                executionId,
                cancellationToken);
        }

        /// <summary>
        /// Enqueues one pipeline run request for normal execution creation or controlled execution resume.
        /// </summary>
        /// <param name="request">The pipeline run request.</param>
        /// <param name="resumeExecutionId">The optional existing execution identifier to resume.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A handle for the queued runtime run.</returns>
        private async ValueTask<AiRuntimeWorkerRunHandle> EnqueueCoreAsync(
            AiRuntimePipelineRunRequest request,
            string? resumeExecutionId,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.PipelineName);

            Console.WriteLine(
                $"[AI PIPELINE CONTROLLER] ENQUEUE CALLED ControllerHash='{GetHashCode()}' RuntimeInstanceId='{_runtimeInstanceIdentity.RuntimeInstanceId}' ResumeExecutionId='{resumeExecutionId ?? string.Empty}'");

            if (_options.RejectEnqueueWhenStopped && !_started)
            {
                throw new InvalidOperationException(
                    "The runtime pipeline background controller has not been started.");
            }

            if (_stopped)
            {
                throw new InvalidOperationException(
                    "The runtime pipeline background controller has been stopped and cannot accept new work.");
            }

            var runId = Guid.NewGuid().ToString("N");

            var correlation = new AiRuntimeExecutionCorrelationContext
            {
                CorrelationId = runId,
                RunId = runId,
                ExecutionId = resumeExecutionId,
                PipelineName = request.PipelineName,
                RuntimeInstanceId = _runtimeInstanceIdentity.RuntimeInstanceId,
                WorkerId = PipelineBackgroundControllerWorkerId
            };

            var completionSource = new TaskCompletionSource<AiExecutionRecord>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            var handle = string.IsNullOrWhiteSpace(resumeExecutionId)
                ? new AiRuntimeWorkerRunHandle(
                    runId,
                    completionSource.Task)
                : new AiRuntimeWorkerRunHandle(
                    runId,
                    completionSource.Task,
                    resumeExecutionId);

            var queuedRun = new AiRuntimeQueuedPipelineRun(
                request,
                handle,
                completionSource,
                correlation,
                resumeExecutionId);

            _queuedRuns[runId] = queuedRun;

            try
            {
                await _queue.Writer.WriteAsync(
                    queuedRun,
                    cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(resumeExecutionId))
                {
                    await RegisterResumeRunExecutionIndexAsync(
                            queuedRun,
                            resumeExecutionId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                Console.WriteLine(
                     $"[AI PIPELINE CONTROLLER] ENQUEUED RuntimeInstanceId='{_runtimeInstanceIdentity.RuntimeInstanceId}' RunId='{runId}' ResumeExecutionId='{resumeExecutionId ?? string.Empty}'");
            }
            catch
            {
                _queuedRuns.TryRemove(
                    runId,
                    out _);

                throw;
            }

            _logger.Engine.LogInformation(
                $"[AI PIPELINE CONTROLLER] Run queued. RunId='{runId}', Pipeline='{request.PipelineName}', ResumeExecutionId='{resumeExecutionId ?? string.Empty}'.");

            await RecordRunLedgerAsync(
                    runId,
                    request.PipelineName,
                    AiDecisionLedgerEvents.Run.Queued,
                    AiDecisionLedgerOutcome.Persisted,
                    reason: string.IsNullOrWhiteSpace(resumeExecutionId)
                        ? "Pipeline run queued."
                        : "Pipeline run queued for existing execution resume.",
                    metadata: new Dictionary<string, string>
                    {
                        ["run.id"] = runId,
                        ["pipeline.name"] = request.PipelineName,
                        ["recovery.resume"] = (!string.IsNullOrWhiteSpace(resumeExecutionId)).ToString(),
                        ["recovery.execution.id"] = resumeExecutionId ?? string.Empty
                    },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return handle;
        }

        /// <summary>
        /// Registers a queued local runtime run that targets an existing durable execution.
        /// </summary>
        /// <param name="queuedRun">The queued runtime pipeline run.</param>
        /// <param name="resumeExecutionId">The durable execution identifier being resumed.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task RegisterResumeRunExecutionIndexAsync(
            AiRuntimeQueuedPipelineRun queuedRun,
            string resumeExecutionId,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(queuedRun);
            ArgumentException.ThrowIfNullOrWhiteSpace(resumeExecutionId);

            await _runExecutionIndex
                .RegisterQueuedAsync(new AiRuntimeRunExecutionIndexEntry
                {
                    RunId = queuedRun.Handle.RunId,
                    ExecutionId = resumeExecutionId,
                    RuntimeInstanceId = _runtimeInstanceIdentity.RuntimeInstanceId,
                    Status = "queued",
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    ExecutionContextSnapshot = queuedRun.Request.ExecutionContextSnapshot,
                    Metadata = MergeRecoveryMetadata(
                        new Dictionary<string, string>
                        {
                            ["pipeline.name"] = queuedRun.Request.PipelineName,
                            ["runtime.instance.id"] = _runtimeInstanceIdentity.RuntimeInstanceId,
                            ["recovery.resume"] = "true",
                            ["recovery.execution.id"] = resumeExecutionId,
                            ["context.key"] = queuedRun.Request.ExecutionContextSnapshot?.ContextKey ?? string.Empty,
                            [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = queuedRun.Request.ExecutionContextSnapshot?.TenantId ?? string.Empty,
                            [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = queuedRun.Request.ExecutionContextSnapshot?.TenantGroupId ?? string.Empty
                        },
                        GetPipelineRunMetadata(queuedRun.Request))
                })
                .ConfigureAwait(false);

            _logger.Engine.LogInformation(
                $"[AI PIPELINE CONTROLLER] Resume run execution index registered. RunId='{queuedRun.Handle.RunId}', ExecutionId='{resumeExecutionId}', RuntimeInstanceId='{_runtimeInstanceIdentity.RuntimeInstanceId}', Pipeline='{queuedRun.Request.PipelineName}'.");
        }

        /// <inheritdoc />
        public async Task PauseQueueAsync(
            string? reason = null,
            string? requestedBy = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_stopped)
            {
                throw new InvalidOperationException(
                    "The runtime pipeline background controller has been stopped and cannot be paused.");
            }

            var ledgerTarget = ResolveQueueLedgerTarget();

            _queuePaused = true;
            _queuePauseReason = reason;
            _queuePauseRequestedBy = requestedBy;
            _queuePausedAtUtc = DateTime.UtcNow;

            _logger.Engine.LogInformation(
                $"[AI PIPELINE CONTROLLER] Queue paused. Reason='{reason}', RequestedBy='{requestedBy}'.");

            await RecordQueueLedgerAsync(
                    executionId: ledgerTarget.ExecutionId,
                    runId: ledgerTarget.RunId,
                    pipelineName: ledgerTarget.PipelineName,
                    eventType: AiDecisionLedgerEvents.Queue.Paused,
                    outcome: AiDecisionLedgerOutcome.Applied,
                    reason: reason ?? "Pipeline controller queue paused.",
                    metadata: new Dictionary<string, string>
                    {
                        ["requested.by"] = requestedBy ?? string.Empty,
                        ["paused.at.utc"] = _queuePausedAtUtc?.ToString("O") ?? string.Empty
                    },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task ResumeQueueAsync(
            string? requestedBy = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_stopped)
            {
                throw new InvalidOperationException(
                    "The runtime pipeline background controller has been stopped and cannot be resumed.");
            }

            var ledgerTarget = ResolveQueueLedgerTarget();
            var pausedSince = _queuePausedAtUtc;
            var previousRequestedBy = _queuePauseRequestedBy;

            _queuePaused = false;
            _queuePauseReason = null;
            _queuePauseRequestedBy = requestedBy;
            _queuePausedAtUtc = null;

            _logger.Engine.LogInformation(
                $"[AI PIPELINE CONTROLLER] Queue resumed. RequestedBy='{requestedBy}', PausedSinceUtc='{pausedSince:O}'.");

            await RecordQueueLedgerAsync(
                    executionId: ledgerTarget.ExecutionId,
                    runId: ledgerTarget.RunId,
                    pipelineName: ledgerTarget.PipelineName,
                    eventType: AiDecisionLedgerEvents.Queue.Resumed,
                    outcome: AiDecisionLedgerOutcome.Applied,
                    reason: "Pipeline controller queue resumed.",
                    metadata: new Dictionary<string, string>
                    {
                        ["requested.by"] = requestedBy ?? string.Empty,
                        ["previous.requested.by"] = previousRequestedBy ?? string.Empty,
                        ["paused.since.utc"] = pausedSince?.ToString("O") ?? string.Empty
                    },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<bool> CancelQueuedRunAsync(
            string runId,
            string? reason = null,
            string? requestedBy = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            cancellationToken.ThrowIfCancellationRequested();

            if (!_queuedRuns.TryRemove(runId, out var queuedRun))
            {
                return false;
            }

            if (queuedRun.Handle.Status != AiRuntimeWorkerRunStatus.Queued)
            {
                return false;
            }

            queuedRun.Handle.MarkCancelled();

            queuedRun.CompletionSource.TrySetResult(
                new AiExecutionRecord
                {
                    ExecutionId = queuedRun.Handle.ExecutionId ?? queuedRun.Handle.RunId,
                    Status = AiExecutionStatus.Cancelled,
                    CompletedAtUtc = DateTime.UtcNow
                });

            _logger.Engine.LogInformation(
                $"[AI PIPELINE CONTROLLER] Queued run cancelled. RunId='{runId}', Pipeline='{queuedRun.Request.PipelineName}', RequestedBy='{requestedBy}', Reason='{reason}'.");

            await RecordRunLedgerAsync(
                    runId,
                    queuedRun.Request.PipelineName,
                    AiDecisionLedgerEvents.Run.Cancelled,
                    AiDecisionLedgerOutcome.Cancelled,
                    reason: reason ?? "Queued pipeline run cancelled before execution creation.",
                    metadata: new Dictionary<string, string>
                    {
                        ["run.id"] = runId,
                        ["pipeline.name"] = queuedRun.Request.PipelineName,
                        ["requested.by"] = requestedBy ?? string.Empty,
                        ["run.status"] = AiRuntimeWorkerRunStatus.Cancelled.ToString()
                    },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return true;
        }

        /// <inheritdoc />
        public async Task<bool> CancelRunAsync(
            AiRuntimeWorkerRunHandle handle,
            string? reason = null,
            string? requestedBy = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(handle.RunId);
            cancellationToken.ThrowIfCancellationRequested();

            var queuedCancelled = await CancelQueuedRunAsync(
                    handle.RunId,
                    reason,
                    requestedBy,
                    cancellationToken)
                .ConfigureAwait(false);

            if (queuedCancelled)
            {
                return true;
            }

            if (!_runningRuns.TryGetValue(handle.RunId, out var runningRun))
            {
                return false;
            }

            var executionId = runningRun.Handle.ExecutionId;

            if (string.IsNullOrWhiteSpace(executionId))
            {
                return false;
            }

            await _executionControlService.CancelExecutionAsync(
                    executionId,
                    reason ?? "Running pipeline run cancellation requested.",
                    requestedBy,
                    cancellationToken)
                .ConfigureAwait(false);

            _logger.Engine.LogInformation(
                $"[AI PIPELINE CONTROLLER] Running run cancellation requested. RunId='{handle.RunId}', ExecutionId='{executionId}', Pipeline='{runningRun.Request.PipelineName}', RequestedBy='{requestedBy}', Reason='{reason}'.");

            return true;
        }

        /// <summary>
        /// Runs the main background controller loop.
        /// </summary>
        /// <param name="cancellationToken">The controller cancellation token.</param>
        private async Task RunControllerLoopAsync(
            CancellationToken cancellationToken)
        {
            _logger.Engine.LogInformation(
                "[AI PIPELINE CONTROLLER] Background loop started.");

            Console.WriteLine(
                $"[AI PIPELINE CONTROLLER] LOOP START RuntimeInstanceId='{_runtimeInstanceIdentity.RuntimeInstanceId}'");

            try
            {
                await foreach (var queuedRun in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    Console.WriteLine(
                        $"[AI PIPELINE CONTROLLER] DEQUEUED RuntimeInstanceId='{_runtimeInstanceIdentity.RuntimeInstanceId}' RunId='{queuedRun.Handle.RunId}'");

                    if (queuedRun.Handle.Status == AiRuntimeWorkerRunStatus.Cancelled)
                    {
                        _queuedRuns.TryRemove(
                            queuedRun.Handle.RunId,
                            out _);

                        continue;
                    }

                    await WaitWhileQueuePausedAsync(
                            queuedRun,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (queuedRun.Handle.Status == AiRuntimeWorkerRunStatus.Cancelled)
                    {
                        _queuedRuns.TryRemove(
                            queuedRun.Handle.RunId,
                            out _);

                        continue;
                    }

                    await _parallelismGate.WaitAsync(cancellationToken).ConfigureAwait(false);

                    _queuedRuns.TryRemove(
                        queuedRun.Handle.RunId,
                        out _);

                    _runningRuns[queuedRun.Handle.RunId] = queuedRun;

                    await RecordRunLedgerAsync(
                            queuedRun.Handle.RunId,
                            queuedRun.Request.PipelineName,
                            AiDecisionLedgerEvents.Run.Dequeued,
                            AiDecisionLedgerOutcome.Started,
                            reason: "Pipeline run dequeued for processing.",
                            metadata: new Dictionary<string, string>
                            {
                                ["run.id"] = queuedRun.Handle.RunId,
                                ["pipeline.name"] = queuedRun.Request.PipelineName,
                                ["max.concurrent.runs"] = _options.MaxConcurrentRuns.ToString()
                            },
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                    var task = ProcessQueuedRunAsync(
                        queuedRun,
                        cancellationToken);

                    _activeRuns.TryAdd(
                        queuedRun.Handle.RunId,
                        task);

                    _ = task.ContinueWith(
                        completed =>
                        {
                            _activeRuns.TryRemove(
                                queuedRun.Handle.RunId,
                                out _);

                            _runningRuns.TryRemove(
                                queuedRun.Handle.RunId,
                                out _);

                            _parallelismGate.Release();

                            if (completed.Exception is not null)
                            {
                                var exception =
                                    completed.Exception.GetBaseException();

                                _logger.Engine.LogError(
                                    CreateRunFailureLogMessage(
                                        "Run task faulted unexpectedly after background processing escaped the guarded execution path.",
                                        queuedRun,
                                        exception,
                                        executionId: queuedRun.Handle.ExecutionId));
                            }
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancelQueuedRuns();
            }
            finally
            {
                _logger.Engine.LogInformation(
                    "[AI PIPELINE CONTROLLER] Background loop stopped.");
            }
        }

        /// <summary>
        /// Processes one queued pipeline run.
        /// </summary>
        /// <param name="queuedRun">The queued pipeline run.</param>
        /// <param name="cancellationToken">The controller cancellation token.</param>
        private async Task ProcessQueuedRunAsync(
            AiRuntimeQueuedPipelineRun queuedRun,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(queuedRun);

            var handle = queuedRun.Handle;
            var request = queuedRun.Request;

            queuedRun.Correlation.RuntimeInstanceId ??= _runtimeInstanceIdentity.RuntimeInstanceId;
            queuedRun.Correlation.WorkerId ??= PipelineBackgroundControllerWorkerId;
            queuedRun.Correlation.RunId ??= handle.RunId;

            using var correlationScope = _observability.Correlation.Push(queuedRun.Correlation);

            try
            {
                await _observability.Tracer.TraceExecutionAsync(
                    new AiExecutionTraceContext
                    {
                        ExecutionId = handle.ExecutionId ?? handle.RunId,
                        ExecutionMode = "Dag",
                        Status = "PipelineRunQueued",
                        WorkerId = queuedRun.Correlation.WorkerId ?? PipelineBackgroundControllerWorkerId
                    },
                    async () =>
                    {
                        await ProcessQueuedRunCoreAsync(
                            queuedRun,
                            cancellationToken).ConfigureAwait(false);

                        return true;
                    }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                handle.MarkCancelled();

                await _runExecutionIndex
                    .MarkFailedAsync(
                        handle.RunId,
                        handle.ExecutionId,
                        "Pipeline run was cancelled by controller cancellation token.",
                        CancellationToken.None)
                    .ConfigureAwait(false);

                queuedRun.CompletionSource.TrySetCanceled(
                    cancellationToken);

                _logger.Engine.LogInformation(
                    $"[AI PIPELINE CONTROLLER] Run cancelled. RunId='{handle.RunId}', Pipeline='{request.PipelineName}'.");

                await RecordRunLedgerAsync(
                        handle.RunId,
                        request.PipelineName,
                        AiDecisionLedgerEvents.Run.Cancelled,
                        AiDecisionLedgerOutcome.Cancelled,
                        handle.ExecutionId,
                        "Pipeline run cancelled by controller cancellation token.",
                        new Dictionary<string, string>
                        {
                            ["run.id"] = handle.RunId,
                            ["pipeline.name"] = request.PipelineName,
                            ["run.status"] = AiRuntimeWorkerRunStatus.Cancelled.ToString()
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var phase =
                    ResolveExceptionPhase(
                        ex);

                var failureReason =
                    CreateRunFailureReason(
                        phase,
                        queuedRun,
                        ex,
                        executionId: handle.ExecutionId);

                handle.MarkFailed();

                await _runExecutionIndex
                    .MarkFailedAsync(
                        handle.RunId,
                        handle.ExecutionId,
                        failureReason,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                queuedRun.CompletionSource.TrySetException(ex);

                _logger.Engine.LogError(
                    CreateRunFailureLogMessage(
                        "Run failed during background execution.",
                        queuedRun,
                        ex,
                        executionId: handle.ExecutionId));

                await RecordRunLedgerAsync(
                        handle.RunId,
                        request.PipelineName,
                        AiDecisionLedgerEvents.Run.Failed,
                        AiDecisionLedgerOutcome.Failed,
                        handle.ExecutionId,
                        failureReason,
                        CreateRunFailureMetadata(
                            phase,
                            queuedRun,
                            ex,
                            executionId: handle.ExecutionId),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public Task<AiRuntimePipelineRunState?> GetRunStateAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(runId))
            {
                throw new ArgumentException(
                    "RunId is required.",
                    nameof(runId));
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (_runningRuns.TryGetValue(runId, out var runningRun))
            {
                return Task.FromResult<AiRuntimePipelineRunState?>(
                    CreateRunState(
                        runningRun.Handle,
                        runningRun.Request.PipelineName,
                        _runtimeInstanceIdentity.RuntimeInstanceId,
                        isQueued: false,
                        isRunning: true));
            }

            if (_queuedRuns.TryGetValue(runId, out var queuedRun))
            {
                return Task.FromResult<AiRuntimePipelineRunState?>(
                    CreateRunState(
                        queuedRun.Handle,
                        queuedRun.Request.PipelineName,
                        _runtimeInstanceIdentity.RuntimeInstanceId,
                        isQueued: true,
                        isRunning: false));
            }

            return Task.FromResult<AiRuntimePipelineRunState?>(null);
        }

        /// <inheritdoc />
        public Task<AiRuntimePipelineQueueState> GetQueueStateAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var queuedRunCount = _queuedRuns.Count;
            var runningRunCount = _runningRuns.Count;
            var activeRunCount = _activeRuns.Count;

            var availableRunSlots =
                Math.Max(
                    0,
                    _options.MaxConcurrentRuns - runningRunCount);

            var workerCount =
                _options.Distributed.Enabled
                    ? Math.Max(
                        1,
                        _options.Distributed.WorkerCount)
                    : 1;

            var activeWorkerCount =
                Math.Max(
                    0,
                    Volatile.Read(ref _activeWorkerCount));

            var availableWorkerCount =
                Math.Max(
                    0,
                    workerCount - activeWorkerCount);

            var canAcceptRun =
                !_queuePaused &&
                queuedRunCount < _options.QueueCapacity &&
                availableRunSlots > 0 &&
                availableWorkerCount > 0;

            return Task.FromResult(
                new AiRuntimePipelineQueueState
                {
                    RuntimeInstanceId = _runtimeInstanceIdentity.RuntimeInstanceId,
                    IsPaused = _queuePaused,
                    QueuedRunCount = queuedRunCount,
                    RunningRunCount = runningRunCount,
                    ActiveRunCount = activeRunCount,
                    QueueCapacity = _options.QueueCapacity,
                    MaxConcurrentRuns = _options.MaxConcurrentRuns,
                    AvailableRunSlots = availableRunSlots,

                    WorkerCount = workerCount,
                    ActiveWorkerCount = activeWorkerCount,
                    AvailableWorkerCount = availableWorkerCount,
                    MaxLocalWorkersPerExecution = _options.MaxLocalWorkersPerExecution,

                    CanAcceptRun = canAcceptRun,

                    SnapshotAtUtc = DateTimeOffset.UtcNow
                });
        }

        /// <summary>
        /// Marks all queued, not-yet-started runs as cancelled.
        /// </summary>
        private void CancelQueuedRuns()
        {
            foreach (var item in _queuedRuns.ToArray())
            {
                if (!_queuedRuns.TryRemove(item.Key, out var queuedRun))
                {
                    continue;
                }

                queuedRun.Handle.MarkCancelled();

                queuedRun.CompletionSource.TrySetCanceled();

                _logger.Engine.LogInformation(
                    $"[AI PIPELINE CONTROLLER] Queued run cancelled before execution. RunId='{queuedRun.Handle.RunId}', Pipeline='{queuedRun.Request.PipelineName}'.");
            }

            while (_queue.Reader.TryRead(out var queuedRun))
            {
                if (queuedRun.Handle.Status == AiRuntimeWorkerRunStatus.Cancelled)
                {
                    continue;
                }

                queuedRun.Handle.MarkCancelled();

                queuedRun.CompletionSource.TrySetCanceled();

                _logger.Engine.LogInformation(
                    $"[AI PIPELINE CONTROLLER] Queued run cancelled before execution. RunId='{queuedRun.Handle.RunId}', Pipeline='{queuedRun.Request.PipelineName}'.");
            }
        }

        /// <summary>
        /// Executes the core pipeline run flow: resolve definition, publish definition,
        /// create execution, and advance execution through the runtime worker.
        /// </summary>
        /// <param name="queuedRun">The queued pipeline run.</param>
        /// <param name="cancellationToken">The controller cancellation token.</param>
        private async Task ProcessQueuedRunCoreAsync(
            AiRuntimeQueuedPipelineRun queuedRun,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(queuedRun);

            var handle =
                queuedRun.Handle;

            var request =
                queuedRun.Request;

            var diagnosticPhase =
                "initializing";

            AiExecutionRecord? created =
                null;

            AiPipelineDefinition? definition =
                null;

            var previousExecutionContext =
                _executionContextAccessor.Current;

            var executionContextRestored =
                false;

            try
            {
                diagnosticPhase =
                    "restore-execution-context";

                var restoredExecutionContext =
                    RestoreExecutionContextFromSnapshot(
                        queuedRun);

                executionContextRestored =
                    true;

                if (queuedRun.IsResume)
                {
                    diagnosticPhase =
                        "seed-resume-execution-context";

                    await SeedResumeExecutionContextAsync(
                            queuedRun,
                            restoredExecutionContext,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                diagnosticPhase =
                    "mark-creating-execution";

                handle.MarkCreatingExecution();

                _logger.Engine.LogInformation(
                    queuedRun.IsResume
                        ? $"[AI PIPELINE CONTROLLER] Preparing existing execution resume. RunId='{handle.RunId}', ExecutionId='{queuedRun.ResumeExecutionId}', Pipeline='{request.PipelineName}', RuntimeInstanceId='{_runtimeInstanceIdentity.RuntimeInstanceId}', TenantId='{request.ExecutionContextSnapshot?.TenantId ?? string.Empty}', ContextKey='{request.ExecutionContextSnapshot?.ContextKey ?? string.Empty}', InputType='{ResolveInputTypeName(request.Input)}'."
                        : $"[AI PIPELINE CONTROLLER] Creating execution. RunId='{handle.RunId}', Pipeline='{request.PipelineName}', RuntimeInstanceId='{_runtimeInstanceIdentity.RuntimeInstanceId}', TenantId='{request.ExecutionContextSnapshot?.TenantId ?? string.Empty}', ContextKey='{request.ExecutionContextSnapshot?.ContextKey ?? string.Empty}', InputType='{ResolveInputTypeName(request.Input)}'.");

                diagnosticPhase =
                    "resolve-definition";

                definition = await _definitionResolver.ResolveAsync(
                    request,
                    cancellationToken).ConfigureAwait(false);

                _logger.Engine.LogInformation(
                    $"[AI PIPELINE CONTROLLER] Definition resolved. RunId='{handle.RunId}', Pipeline='{request.PipelineName}', StepCount='{definition.Steps.Count}'.");

                diagnosticPhase =
                    "publish-definition";

                await _definitionPublisher.PublishAsync(
                    definition,
                    cancellationToken).ConfigureAwait(false);

                _logger.Engine.LogInformation(
                    $"[AI PIPELINE CONTROLLER] Definition published. RunId='{handle.RunId}', Pipeline='{request.PipelineName}'.");

                diagnosticPhase =
                    queuedRun.IsResume
                        ? "resume-existing-execution"
                        : "create-execution";

                if (queuedRun.IsResume)
                {
                    created = new AiExecutionRecord
                    {
                        ExecutionId = queuedRun.ResumeExecutionId!,
                        PipelineName = request.PipelineName,
                        Status = AiExecutionStatus.Running
                    };

                    _logger.Engine.LogInformation(
                        $"[AI PIPELINE CONTROLLER] Existing execution resume selected. RunId='{handle.RunId}', ExecutionId='{created.ExecutionId}', Pipeline='{request.PipelineName}', RuntimeInstanceId='{_runtimeInstanceIdentity.RuntimeInstanceId}'.");

                    await RecordRecoveryForensicsEventAsync(
                            queuedRun,
                            AiRuntimeRecoveryForensicsEventType.DagResumeStarted,
                            "started",
                            "dag-resume-started-on-replacement-runtime",
                            created.ExecutionId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    created = await CreateExecutionAsync(
                        request,
                        cancellationToken).ConfigureAwait(false);

                    _logger.Engine.LogInformation(
                        $"[AI PIPELINE CONTROLLER] Execution created. RunId='{handle.RunId}', ExecutionId='{created.ExecutionId}', Pipeline='{created.PipelineName}'.");
                }

                queuedRun.Correlation.ExecutionId =
                    created.ExecutionId;

                queuedRun.Correlation.PipelineKey =
                    created.PipelineName;

                queuedRun.Correlation.RunId =
                    handle.RunId;

                diagnosticPhase =
                    "mark-running";

                handle.MarkRunning(
                    created.ExecutionId);

                diagnosticPhase =
                    "mark-run-execution-index-started";

                await _runExecutionIndex
                    .MarkStartedAsync(
                        handle.RunId,
                        created.ExecutionId,
                        cancellationToken)
                    .ConfigureAwait(false);

                diagnosticPhase =
                    "register-execution-assistance-candidate";

                await RegisterExecutionAssistanceCandidateAsync(
                        handle.RunId,
                        request,
                        created,
                        definition,
                        cancellationToken)
                    .ConfigureAwait(false);

                diagnosticPhase =
                    "record-run-started-ledger";

                await RecordRunLedgerAsync(
                        handle.RunId,
                        request.PipelineName,
                        AiDecisionLedgerEvents.Run.Started,
                        AiDecisionLedgerOutcome.Started,
                        created.ExecutionId,
                        "Pipeline run started execution processing.",
                        new Dictionary<string, string>
                        {
                            ["run.id"] = handle.RunId,
                            ["execution.id"] = created.ExecutionId,
                            ["pipeline.name"] = request.PipelineName,
                            [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = request.ExecutionContextSnapshot?.TenantId ?? string.Empty,
                            [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = request.ExecutionContextSnapshot?.TenantGroupId ?? string.Empty,
                            ["context.key"] = request.ExecutionContextSnapshot?.ContextKey ?? string.Empty,
                            ["distributed.enabled"] = _options.Distributed.Enabled.ToString(),
                            ["distributed.worker.count"] = _options.Distributed.WorkerCount.ToString(),
                            ["max.local.workers.per.execution"] = _options.MaxLocalWorkersPerExecution.ToString(),
                            ["effective.worker.count.per.execution"] = ResolveMaxWorkerCountForExecution().ToString(),
                            ["recovery.resume"] = queuedRun.IsResume.ToString(),
                            ["recovery.execution.id"] = queuedRun.ResumeExecutionId ?? string.Empty
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                AiExecutionRecord? final =
                    null;

                try
                {
                    diagnosticPhase =
                        "run-created-execution";

                    final = await RunCreatedExecutionAsync(
                        created.ExecutionId,
                        cancellationToken).ConfigureAwait(false);

                    if (queuedRun.IsResume)
                    {
                        if (final.Status == AiExecutionStatus.Completed)
                        {
                            await RecordRecoveryForensicsEventAsync(
                                    queuedRun,
                                    AiRuntimeRecoveryForensicsEventType.DagResumeCompleted,
                                    "completed",
                                    "dag-resume-completed-on-replacement-runtime",
                                    created.ExecutionId,
                                    cancellationToken)
                                .ConfigureAwait(false);

                            await RecordRecoveryForensicsEventAsync(
                                    queuedRun,
                                    AiRuntimeRecoveryForensicsEventType.ExecutionRecoveryCompleted,
                                    "completed",
                                    "execution-recovery-completed-after-dag-resume",
                                    created.ExecutionId,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await RecordRecoveryForensicsEventAsync(
                                    queuedRun,
                                    AiRuntimeRecoveryForensicsEventType.ExecutionRecoveryFailed,
                                    "failed",
                                    $"execution-recovery-failed-after-dag-resume-status-{final.Status}",
                                    created.ExecutionId,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }

                    diagnosticPhase =
                        "apply-terminal-status";

                    if (final.Status == AiExecutionStatus.Completed)
                    {
                        handle.MarkCompleted();

                        await _runExecutionIndex
                            .MarkCompletedAsync(
                                handle.RunId,
                                created.ExecutionId,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else if (final.Status == AiExecutionStatus.Cancelled)
                    {
                        handle.MarkCancelled();

                        await _runExecutionIndex
                            .MarkFailedAsync(
                                handle.RunId,
                                created.ExecutionId,
                                "Pipeline run was cancelled.",
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        handle.MarkFailed();

                        await _runExecutionIndex
                            .MarkFailedAsync(
                                handle.RunId,
                                created.ExecutionId,
                                $"Pipeline run reached terminal status '{final.Status}'.",
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    diagnosticPhase =
                        "record-terminal-ledger";

                    await RecordRunTerminalLedgerAsync(
                            handle.RunId,
                            request.PipelineName,
                            created.ExecutionId,
                            final,
                            cancellationToken)
                        .ConfigureAwait(false);

                    diagnosticPhase =
                        "invoke-run-finalized-hook";

                    await InvokeRunFinalizedAsync(
                        queuedRun,
                        final,
                        cancellationToken).ConfigureAwait(false);

                    queuedRun.CompletionSource.TrySetResult(final);

                    _logger.Engine.LogInformation(
                        $"[AI PIPELINE CONTROLLER] Run terminal. RunId='{handle.RunId}', ExecutionId='{created.ExecutionId}', Status='{final.Status}'.");
                }
                catch (Exception ex)
                {
                    if (queuedRun.IsResume)
                    {
                        await RecordRecoveryForensicsEventAsync(
                                queuedRun,
                                AiRuntimeRecoveryForensicsEventType.ExecutionRecoveryFailed,
                                "failed",
                                ex.Message,
                                created.ExecutionId,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }

                    AttachRunExceptionData(
                        ex,
                        diagnosticPhase,
                        queuedRun,
                        executionId: created.ExecutionId);

                    throw;
                }
                finally
                {
                    diagnosticPhase =
                        "mark-execution-assistance-candidate-completed";

                    await MarkExecutionAssistanceCandidateCompletedAsync(
                            created.ExecutionId,
                            final?.Status.ToString() ?? "unknown",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                AttachRunExceptionData(
                    ex,
                    diagnosticPhase,
                    queuedRun,
                    executionId: created?.ExecutionId,
                    definitionStepCount: definition?.Steps.Count);

                throw;
            }
            finally
            {
                RestorePreviousExecutionContext(
                    previousExecutionContext,
                    executionContextRestored);
            }
        }

        /// <summary>
        /// Records a recovery forensics event for a controlled DAG resume.
        /// </summary>
        /// <param name="queuedRun">The queued runtime pipeline run.</param>
        /// <param name="eventType">The recovery forensics event type.</param>
        /// <param name="outcome">The event outcome.</param>
        /// <param name="reason">The event reason.</param>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task RecordRecoveryForensicsEventAsync(
            AiRuntimeQueuedPipelineRun queuedRun,
            string eventType,
            string outcome,
            string reason,
            string executionId,
            CancellationToken cancellationToken)
        {
            if (!queuedRun.IsResume)
            {
                return;
            }

            var metadata =
                GetPipelineRunMetadata(
                    queuedRun.Request);

            if (!TryResolveRecoveryForensicsId(
                    queuedRun,
                    metadata,
                    executionId,
                    out var forensicsId,
                    out var sharedRunId,
                    out var failedRuntimeInstanceId,
                    out var failedLocalRunId))
            {
                return;
            }

            await _forensicsRecorder
                .RecordEventAsync(
                    new AiRuntimeRecoveryForensicsEvent
                    {
                        EventId = string.Join(
                            ":",
                            forensicsId,
                            eventType,
                            _runtimeInstanceIdentity.RuntimeInstanceId,
                            queuedRun.Handle.RunId),
                        ForensicsId = forensicsId,
                        TimestampUtc = DateTimeOffset.UtcNow,
                        EventType = eventType,
                        Outcome = outcome,
                        Reason = reason,
                        ExecutionId = executionId,
                        SharedRunId = sharedRunId,
                        LocalRunId = queuedRun.Handle.RunId,
                        RuntimeInstanceId = _runtimeInstanceIdentity.RuntimeInstanceId,
                        Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["pipeline.name"] = queuedRun.Request.PipelineName,
                            ["tenant.id"] = queuedRun.Request.ExecutionContextSnapshot?.TenantId ?? string.Empty,
                            ["tenant.group.id"] = queuedRun.Request.ExecutionContextSnapshot?.TenantGroupId ?? string.Empty,
                            ["replacement.runtimeInstanceId"] = _runtimeInstanceIdentity.RuntimeInstanceId,
                            ["replacement.localRunId"] = queuedRun.Handle.RunId,
                            ["replacement.executionId"] = executionId,
                            ["failed.runtimeInstanceId"] = failedRuntimeInstanceId,
                            ["failed.localRunId"] = failedLocalRunId,
                            ["resume.contextKey"] = queuedRun.Request.ExecutionContextSnapshot?.ContextKey ?? string.Empty,
                            ["recovery.resume"] = "true"
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Tries to resolve the recovery forensics identity from propagated runtime pipeline run metadata.
        /// </summary>
        /// <param name="queuedRun">The queued runtime pipeline run.</param>
        /// <param name="metadata">The propagated runtime pipeline run metadata.</param>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="forensicsId">The resolved forensics identifier.</param>
        /// <param name="sharedRunId">The resolved shared run identifier.</param>
        /// <param name="failedRuntimeInstanceId">The failed runtime instance identifier.</param>
        /// <param name="failedLocalRunId">The failed local run identifier.</param>
        /// <returns><c>true</c> when the forensics identity can be resolved; otherwise, <c>false</c>.</returns>
        private static bool TryResolveRecoveryForensicsId(
            AiRuntimeQueuedPipelineRun queuedRun,
            IReadOnlyDictionary<string, string> metadata,
            string executionId,
            out string forensicsId,
            out string? sharedRunId,
            out string failedRuntimeInstanceId,
            out string failedLocalRunId)
        {
            sharedRunId =
                ResolveMetadataValue(metadata, "shared.run.id");

            failedRuntimeInstanceId =
                ResolveMetadataValue(metadata, "recovery.failedRuntimeInstanceId");

            failedLocalRunId =
                ResolveMetadataValue(metadata, "recovery.failedLocalRunId");

            if (TryGetMetadataValue(
                    metadata,
                    "recovery.forensicsId",
                    out var explicitForensicsId))
            {
                forensicsId = explicitForensicsId;
                return true;
            }

            if (string.IsNullOrWhiteSpace(sharedRunId) ||
                string.IsNullOrWhiteSpace(failedLocalRunId))
            {
                forensicsId = string.Empty;
                return false;
            }

            forensicsId = string.Join(
                ":",
                "runtime-recovery",
                executionId,
                sharedRunId,
                failedLocalRunId);

            return !string.IsNullOrWhiteSpace(queuedRun.Handle.RunId);
        }

        /// <summary>
        /// Gets optional runtime pipeline run metadata without requiring older request contracts to define the property at compile time.
        /// </summary>
        /// <param name="request">The pipeline run request.</param>
        /// <returns>The metadata dictionary when available; otherwise, an empty dictionary.</returns>
        private static IReadOnlyDictionary<string, string> GetPipelineRunMetadata(
            AiRuntimePipelineRunRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            var metadataProperty =
                request
                    .GetType()
                    .GetProperty(
                        "Metadata",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (metadataProperty?.GetValue(request) is IReadOnlyDictionary<string, string> metadata)
            {
                return metadata;
            }

            if (metadataProperty?.GetValue(request) is IDictionary<string, string> dictionary)
            {
                return new Dictionary<string, string>(
                    dictionary,
                    StringComparer.OrdinalIgnoreCase);
            }

            return new Dictionary<string, string>();
        }

        /// <summary>
        /// Merges recovery metadata dictionaries.
        /// </summary>
        /// <param name="baseMetadata">The base metadata.</param>
        /// <param name="overrideMetadata">The override metadata.</param>
        /// <returns>The merged metadata dictionary.</returns>
        private static IReadOnlyDictionary<string, string> MergeRecoveryMetadata(
            IReadOnlyDictionary<string, string> baseMetadata,
            IReadOnlyDictionary<string, string> overrideMetadata)
        {
            var merged =
                new Dictionary<string, string>(
                    baseMetadata,
                    StringComparer.OrdinalIgnoreCase);

            foreach (var item in overrideMetadata)
            {
                if (!string.IsNullOrWhiteSpace(item.Key))
                {
                    merged[item.Key] = item.Value;
                }
            }

            return merged;
        }

        /// <summary>
        /// Resolves a metadata value or an empty string.
        /// </summary>
        /// <param name="metadata">The metadata dictionary.</param>
        /// <param name="key">The metadata key.</param>
        /// <returns>The metadata value when present; otherwise, an empty string.</returns>
        private static string ResolveMetadataValue(
            IReadOnlyDictionary<string, string> metadata,
            string key)
        {
            return TryGetMetadataValue(
                metadata,
                key,
                out var value)
                ? value
                : string.Empty;
        }

        /// <summary>
        /// Attempts to read a metadata value by key using ordinal ignore-case matching.
        /// </summary>
        /// <param name="metadata">The metadata dictionary.</param>
        /// <param name="key">The metadata key.</param>
        /// <param name="value">The resolved value.</param>
        /// <returns><c>true</c> when a non-empty value is found; otherwise, <c>false</c>.</returns>
        private static bool TryGetMetadataValue(
            IReadOnlyDictionary<string, string> metadata,
            string key,
            out string value)
        {
            if (metadata.TryGetValue(
                    key,
                    out var directValue) &&
                !string.IsNullOrWhiteSpace(directValue))
            {
                value = directValue;
                return true;
            }

            foreach (var pair in metadata)
            {
                if (string.Equals(
                        pair.Key,
                        key,
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(pair.Value))
                {
                    value = pair.Value;
                    return true;
                }
            }

            value = string.Empty;
            return false;
        }

        /// <summary>
        /// Restores the active RBAC execution context from the durable snapshot carried by the runtime run request.
        /// </summary>
        /// <param name="queuedRun">The queued runtime pipeline run.</param>
        private ExecutionContext RestoreExecutionContextFromSnapshot(
            AiRuntimeQueuedPipelineRun queuedRun)
        {
            ArgumentNullException.ThrowIfNull(queuedRun);

            var snapshot =
                queuedRun.Request.ExecutionContextSnapshot;

            if (snapshot is null)
            {
                throw new InvalidOperationException(
                    $"No execution context snapshot is available for runtime run '{queuedRun.Handle.RunId}' and pipeline '{queuedRun.Request.PipelineName}'. The shared run must persist ExecutionContextSnapshot in Redis and propagate it to the local runtime queue.");
            }

            if (string.IsNullOrWhiteSpace(snapshot.TenantId))
            {
                throw new InvalidOperationException(
                    $"Execution context snapshot for runtime run '{queuedRun.Handle.RunId}' has no TenantId.");
            }

            var context =
                MapSnapshotToExecutionContext(
                    snapshot);

            _executionContextAccessor.Set(
                context);

            return context;
        }

        /// <summary>
        /// Seeds the restored RBAC execution context into the local context store before resuming
        /// an existing durable execution on a replacement runtime instance.
        /// </summary>
        /// <param name="queuedRun">The queued runtime pipeline run.</param>
        /// <param name="context">The restored RBAC execution context.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task SeedResumeExecutionContextAsync(
            AiRuntimeQueuedPipelineRun queuedRun,
            ExecutionContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(queuedRun);
            ArgumentNullException.ThrowIfNull(context);

            cancellationToken.ThrowIfCancellationRequested();

            if (!queuedRun.IsResume)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(context.ContextKey))
            {
                throw new InvalidOperationException(
                    $"Cannot resume execution '{queuedRun.ResumeExecutionId}' for runtime run '{queuedRun.Handle.RunId}' because the restored execution context has no ContextKey.");
            }

            await _engine
                .SeedRestoredExecutionContextAsync(
                    queuedRun.ResumeExecutionId,
                    context,
                    cancellationToken)
                .ConfigureAwait(false);

            _logger.Engine.LogInformation(
                $"[AI PIPELINE CONTROLLER] Resume execution context seeded. RunId='{queuedRun.Handle.RunId}', ExecutionId='{queuedRun.ResumeExecutionId}', Pipeline='{queuedRun.Request.PipelineName}', RuntimeInstanceId='{_runtimeInstanceIdentity.RuntimeInstanceId}', TenantId='{context.TenantId}', ContextKey='{context.ContextKey}'.");
        }

        /// <summary>
        /// Restores the previous RBAC execution context after a background run.
        /// </summary>
        /// <param name="previousExecutionContext">The context that was active before the run, if any.</param>
        /// <param name="executionContextRestored">Whether this controller restored a context for the run.</param>
        private void RestorePreviousExecutionContext(
            ExecutionContext? previousExecutionContext,
            bool executionContextRestored)
        {
            if (!executionContextRestored)
            {
                return;
            }

            if (previousExecutionContext is not null)
            {
                _executionContextAccessor.Set(
                    previousExecutionContext);

                return;
            }

            TryClearExecutionContextAccessor();
        }

        /// <summary>
        /// Clears the execution context accessor when the concrete accessor supports a Clear method.
        /// </summary>
        /// <remarks>
        /// The public abstraction only requires Set and Current. Some runtime accessors expose
        /// Clear to prevent AsyncLocal leakage after background execution. This reflection-based
        /// call keeps the controller compatible with both accessor shapes.
        /// </remarks>
        private void TryClearExecutionContextAccessor()
        {
            var clearMethod =
                _executionContextAccessor
                    .GetType()
                    .GetMethod(
                        "Clear",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        binder: null,
                        types: Type.EmptyTypes,
                        modifiers: null);

            clearMethod?.Invoke(
                _executionContextAccessor,
                parameters: null);
        }

        /// <summary>
        /// Maps a durable execution context snapshot back to the RBAC execution context model.
        /// </summary>
        /// <param name="snapshot">The durable execution context snapshot.</param>
        /// <returns>The runtime RBAC execution context.</returns>
        private static ExecutionContext MapSnapshotToExecutionContext(
            ExecutionContextSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            return new ExecutionContext
            {
                ContextKey = snapshot.ContextKey,
                Project = snapshot.Project,
                UserId = snapshot.UserId,
                TenantId = snapshot.TenantId,
                TenantGroupId = snapshot.TenantGroupId,
                CurrentNamespace = snapshot.CurrentNamespace,
                Namespaces = snapshot.Namespaces
                    .Select(namespaceEntry => new NamespaceEntry
                    {
                        Name = namespaceEntry.Name,
                        Trns = new HashSet<string>(
                            namespaceEntry.Trns,
                            StringComparer.Ordinal)
                    })
                    .ToList(),
                InFlightCount = snapshot.InFlightCount,
                TtlSeconds = snapshot.TtlSeconds
            };
        }

        /// <summary>
        /// Registers a running execution as an assistance candidate.
        /// </summary>
        /// <param name="runId">The local runtime run identifier.</param>
        /// <param name="request">The pipeline run request.</param>
        /// <param name="created">The created execution record.</param>
        /// <param name="definition">The resolved pipeline definition.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task RegisterExecutionAssistanceCandidateAsync(
            string runId,
            AiRuntimePipelineRunRequest request,
            AiExecutionRecord created,
            AiPipelineDefinition definition,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(created);
            ArgumentNullException.ThrowIfNull(definition);

            var estimatedReadyStepCount = CountRootSteps(definition);
            var estimatedRemainingStepCount = definition.Steps.Count;
            var estimatedActiveWorkerCount = ResolveMaxWorkerCountForExecution();

            await _assistanceCandidateStore.UpsertAsync(
                    new AiExecutionAssistanceCandidate
                    {
                        ExecutionId = created.ExecutionId,
                        PrimaryRuntimeInstanceId = _runtimeInstanceIdentity.RuntimeInstanceId,
                        LocalRunId = runId,
                        PipelineName = created.PipelineName,
                        PipelineVersion = definition.Version,
                        EstimatedReadyStepCount = estimatedReadyStepCount,
                        EstimatedRemainingStepCount = estimatedRemainingStepCount,
                        EstimatedActiveWorkerCount = estimatedActiveWorkerCount,
                        IsActive = true,
                        RegisteredAtUtc = DateTimeOffset.UtcNow,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        Reason = "Pipeline execution started and is eligible for bounded execution assistance.",
                        Metadata = new Dictionary<string, string>
                        {
                            ["run.id"] = runId,
                            ["execution.id"] = created.ExecutionId,
                            ["pipeline.name"] = request.PipelineName,
                            ["runtime.instance.id"] = _runtimeInstanceIdentity.RuntimeInstanceId,
                            ["distributed.enabled"] = _options.Distributed.Enabled.ToString(),
                            ["distributed.worker.count"] = _options.Distributed.WorkerCount.ToString(),
                            ["estimated.ready.step.count"] = estimatedReadyStepCount.ToString(),
                            ["estimated.remaining.step.count"] = estimatedRemainingStepCount.ToString(),
                            ["estimated.active.worker.count"] = estimatedActiveWorkerCount.ToString()
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Marks an assistance candidate as completed after the owning execution reaches terminal state.
        /// </summary>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="status">The final execution status.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task MarkExecutionAssistanceCandidateCompletedAsync(
            string executionId,
            string status,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            try
            {
                await _assistanceCandidateStore.MarkCompletedAsync(
                        executionId,
                        $"Execution reached terminal status '{status}'.",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Candidate registration is best-effort. If the candidate was never registered
                // or was already completed, the pipeline run lifecycle must not fail.
            }
        }

        /// <summary>
        /// Resolves the maximum number of local workers that may be assigned to one execution.
        /// </summary>
        /// <returns>The maximum worker count for one execution.</returns>
        private int ResolveMaxWorkerCountForExecution()
        {
            if (!_options.Distributed.Enabled)
            {
                return 1;
            }

            var configuredWorkerCount =
                Math.Max(
                    1,
                    _options.Distributed.WorkerCount);

            var maxLocalWorkersPerExecution =
                Math.Max(
                    1,
                    _options.MaxLocalWorkersPerExecution);

            return Math.Min(
                configuredWorkerCount,
                maxLocalWorkersPerExecution);
        }

        /// <summary>
        /// Counts root steps in a pipeline definition.
        /// </summary>
        /// <param name="definition">The pipeline definition.</param>
        /// <returns>The number of steps with no dependencies.</returns>
        private static int CountRootSteps(
            AiPipelineDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(definition);

            return definition.Steps.Count(step => step.DependsOn.Count == 0);
        }

        /// <summary>
        /// Invokes the optional run lifecycle hook after a queued run has reached
        /// its terminal runtime result.
        /// </summary>
        /// <param name="queuedRun">The queued pipeline run.</param>
        /// <param name="final">The final execution record.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task InvokeRunFinalizedAsync(
            AiRuntimeQueuedPipelineRun queuedRun,
            AiExecutionRecord final,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(queuedRun);
            ArgumentNullException.ThrowIfNull(final);

            if (string.IsNullOrWhiteSpace(final.ExecutionId))
            {
                return;
            }

            await _runLifecycleHook.OnFinalizedAsync(
                new AiRuntimePipelineRunFinalizedContext
                {
                    RunId = queuedRun.Handle.RunId,
                    ExecutionId = final.ExecutionId,
                    Record = final
                },
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Advances the created runtime execution using either the default single
        /// runtime instance worker or the distributed runtime instance worker group.
        /// </summary>
        /// <param name="executionId">The runtime execution identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The terminal execution record.</returns>
        private async Task<AiExecutionRecord> RunCreatedExecutionAsync(
            string executionId,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            var workerCount =
                await ReserveWorkersForExecutionAsync(
                        cancellationToken)
                    .ConfigureAwait(false);

            try
            {
                if (!_options.Distributed.Enabled || workerCount == 1)
                {
                    return await _worker.RunExecutionAsync(
                        executionId,
                        cancellationToken).ConfigureAwait(false);
                }

                var workers = _workerFactory.CreateWorkers(
                    workerCount);

                return await _workerGroup.RunExecutionAsync(
                    executionId,
                    workers,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Add(
                    ref _activeWorkerCount,
                    -workerCount);
            }
        }

        private async Task<int> ReserveWorkersForExecutionAsync(
            CancellationToken cancellationToken)
        {
            var totalWorkerCount =
                _options.Distributed.Enabled
                    ? Math.Max(1, _options.Distributed.WorkerCount)
                    : 1;

            var maxWorkersForExecution =
                _options.Distributed.Enabled
                    ? Math.Max(1, _options.MaxLocalWorkersPerExecution)
                    : 1;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var activeWorkerCount =
                    Math.Max(
                        0,
                        Volatile.Read(ref _activeWorkerCount));

                var availableWorkerCount =
                    Math.Max(
                        0,
                        totalWorkerCount - activeWorkerCount);

                if (availableWorkerCount <= 0)
                {
                    await Task.Delay(
                            TimeSpan.FromMilliseconds(25),
                            cancellationToken)
                        .ConfigureAwait(false);

                    continue;
                }

                var workerCount =
                    Math.Min(
                        maxWorkersForExecution,
                        availableWorkerCount);

                var updatedWorkerCount =
                    activeWorkerCount + workerCount;

                if (Interlocked.CompareExchange(
                        ref _activeWorkerCount,
                        updatedWorkerCount,
                        activeWorkerCount) == activeWorkerCount)
                {
                    return workerCount;
                }
            }
        }

        /// <summary>
        /// Creates a runtime execution for the specified pipeline run request.
        /// </summary>
        /// <param name="request">The pipeline run request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The created execution record.</returns>
        private async Task<AiExecutionRecord> CreateExecutionAsync(
            AiRuntimePipelineRunRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.PipelineName);

            if (request.Input is null)
            {
                return await _engine.CreateAsync(
                    request.PipelineName,
                    new Dictionary<string, object?>(),
                    cancellationToken).ConfigureAwait(false);
            }

            if (request.Input is string textInput)
            {
                return await _engine.CreateAsync(
                    request.PipelineName,
                    textInput,
                    cancellationToken).ConfigureAwait(false);
            }

            if (request.Input is IDictionary<string, object?> stateInput)
            {
                return await _engine.CreateAsync(
                    request.PipelineName,
                    stateInput,
                    cancellationToken).ConfigureAwait(false);
            }

            if (request.Input is IReadOnlyDictionary<string, object?> readonlyStateInput)
            {
                return await _engine.CreateAsync(
                    request.PipelineName,
                    new Dictionary<string, object?>(readonlyStateInput, StringComparer.Ordinal),
                    cancellationToken).ConfigureAwait(false);
            }

            return await _engine.CreateAsync(
                request.PipelineName,
                ConvertObjectToStateInput(request.Input),
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Records a controller run lifecycle event in the decision ledger.
        /// </summary>
        /// <param name="runId">The controller run identifier.</param>
        /// <param name="pipelineName">The pipeline name.</param>
        /// <param name="eventType">The ledger event type.</param>
        /// <param name="outcome">The ledger outcome.</param>
        /// <param name="executionId">The optional runtime execution identifier.</param>
        /// <param name="reason">The optional event reason.</param>
        /// <param name="metadata">The optional metadata.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task RecordRunLedgerAsync(
            string runId,
            string pipelineName,
            string eventType,
            AiDecisionLedgerOutcome outcome,
            string? executionId = null,
            string? reason = null,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

            var resolvedExecutionId = string.IsNullOrWhiteSpace(executionId)
                ? runId
                : executionId;

            var context = AiRuntimeCorrelationContextHelper.Create(
                executionId: resolvedExecutionId,
                pipelineKey: pipelineName,
                stepName: "pipeline-run",
                workerId: PipelineBackgroundControllerWorkerId,
                claimToken: null,
                concurrencyContext: null,
                runId: runId,
                correlationId: runId);

            context.CorrelationId = _observability.Correlation.Current?.CorrelationId ?? runId;

            await _observability.Ledger.RecordAsync(
                    context,
                    AiDecisionLedgerCategory.Run,
                    eventType,
                    outcome,
                    reason,
                    metadata,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Records a controller queue lifecycle event in the decision ledger.
        /// </summary>
        /// <param name="eventType">The ledger event type.</param>
        /// <param name="outcome">The ledger outcome.</param>
        /// <param name="reason">The optional event reason.</param>
        /// <param name="metadata">The optional metadata.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task RecordQueueLedgerAsync(
            string executionId,
            string runId,
            string pipelineName,
            string eventType,
            AiDecisionLedgerOutcome outcome,
            string? reason = null,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

            var context = AiRuntimeCorrelationContextHelper.Create(
                executionId: executionId,
                pipelineKey: pipelineName,
                stepName: "pipeline-queue",
                workerId: PipelineBackgroundControllerWorkerId,
                claimToken: null,
                concurrencyContext: null,
                runId: runId,
                correlationId: runId);

            context.CorrelationId = _observability.Correlation.Current?.CorrelationId ?? runId;

            await _observability.Ledger.RecordAsync(
                    context,
                    AiDecisionLedgerCategory.Queue,
                    eventType,
                    outcome,
                    reason,
                    metadata,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Records the terminal controller run lifecycle event in the decision ledger.
        /// </summary>
        /// <param name="runId">The controller run identifier.</param>
        /// <param name="pipelineName">The pipeline name.</param>
        /// <param name="executionId">The runtime execution identifier.</param>
        /// <param name="final">The final execution record.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task RecordRunTerminalLedgerAsync(
            string runId,
            string pipelineName,
            string executionId,
            AiExecutionRecord final,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentNullException.ThrowIfNull(final);

            var eventType = final.Status == AiExecutionStatus.Completed
                ? AiDecisionLedgerEvents.Run.Completed
                : final.Status == AiExecutionStatus.Cancelled
                    ? AiDecisionLedgerEvents.Run.Cancelled
                    : AiDecisionLedgerEvents.Run.Failed;

            var outcome = final.Status == AiExecutionStatus.Completed
                ? AiDecisionLedgerOutcome.Completed
                : final.Status == AiExecutionStatus.Cancelled
                    ? AiDecisionLedgerOutcome.Cancelled
                    : AiDecisionLedgerOutcome.Failed;

            await RecordRunLedgerAsync(
                    runId,
                    pipelineName,
                    eventType,
                    outcome,
                    executionId,
                    $"Pipeline run reached terminal status '{final.Status}'.",
                    new Dictionary<string, string>
                    {
                        ["run.id"] = runId,
                        ["execution.id"] = executionId,
                        ["pipeline.name"] = pipelineName,
                        ["execution.status"] = final.Status.ToString(),
                        ["completed.at.utc"] = final.CompletedAtUtc.ToString("O") ?? string.Empty
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Converts an arbitrary object into structured execution state input.
        /// </summary>
        /// <param name="input">The input object.</param>
        /// <returns>The structured state input dictionary.</returns>
        private static Dictionary<string, object?> ConvertObjectToStateInput(
            object input)
        {
            ArgumentNullException.ThrowIfNull(input);

            var properties = input
                .GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetIndexParameters().Length == 0)
                .ToArray();

            if (properties.Length == 0)
            {
                return new Dictionary<string, object?>
                {
                    [AiExecutionKeys.Input] = input
                };
            }

            var state = new Dictionary<string, object?>(
                StringComparer.Ordinal);

            foreach (var property in properties)
            {
                state[property.Name] = property.GetValue(input);
            }

            return state;
        }

        /// <inheritdoc />
        public async Task<bool> CancelRunAsync(
            string runId,
            string? reason = null,
            string? requestedBy = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            cancellationToken.ThrowIfCancellationRequested();

            var queuedCancelled = await CancelQueuedRunAsync(
                    runId,
                    reason,
                    requestedBy,
                    cancellationToken)
                .ConfigureAwait(false);

            if (queuedCancelled)
            {
                return true;
            }

            if (!_runningRuns.TryGetValue(runId, out var runningRun))
            {
                return false;
            }

            var executionId = runningRun.Handle.ExecutionId;

            if (string.IsNullOrWhiteSpace(executionId))
            {
                return false;
            }

            await _executionControlService.CancelExecutionAsync(
                    executionId,
                    reason ?? "Running pipeline run cancellation requested.",
                    requestedBy,
                    cancellationToken)
                .ConfigureAwait(false);

            _logger.Engine.LogInformation(
                $"[AI PIPELINE CONTROLLER] Running run cancellation requested. RunId='{runId}', ExecutionId='{executionId}', Pipeline='{runningRun.Request.PipelineName}', RequestedBy='{requestedBy}', Reason='{reason}'.");

            await RecordRunLedgerAsync(
                    runId,
                    runningRun.Request.PipelineName,
                    AiDecisionLedgerEvents.Run.Cancelled,
                    AiDecisionLedgerOutcome.Applied,
                    executionId,
                    reason ?? "Running pipeline run cancellation delegated to execution control.",
                    new Dictionary<string, string>
                    {
                        ["run.id"] = runId,
                        ["execution.id"] = executionId,
                        ["pipeline.name"] = runningRun.Request.PipelineName,
                        ["requested.by"] = requestedBy ?? string.Empty,
                        ["run.status"] = runningRun.Handle.Status.ToString()
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return true;
        }

        /// <summary>
        /// Waits while the controller queue is paused before allowing a queued run to start.
        /// </summary>
        /// <param name="queuedRun">
        /// The queued pipeline run waiting to start.
        /// </param>
        /// <param name="cancellationToken">
        /// The controller cancellation token.
        /// </param>
        /// <returns>
        /// A task representing the asynchronous wait operation.
        /// </returns>
        /// <remarks>
        /// The queued run has already been read from the channel, but it has not acquired a
        /// parallelism slot and has not started execution creation. Its public handle therefore
        /// remains queued until the controller queue resumes.
        /// </remarks>
        private async Task WaitWhileQueuePausedAsync(
            AiRuntimeQueuedPipelineRun queuedRun,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(queuedRun);

            var logged = false;

            while (_queuePaused)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!logged)
                {
                    _logger.Engine.LogInformation(
                        $"[AI PIPELINE CONTROLLER] Queued run is waiting because the controller queue is paused. RunId='{queuedRun.Handle.RunId}', Pipeline='{queuedRun.Request.PipelineName}', Reason='{_queuePauseReason}', RequestedBy='{_queuePauseRequestedBy}', PausedAtUtc='{_queuePausedAtUtc:O}'.");

                    logged = true;
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(25),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Determines whether the specified run is currently present in the local pending queue.
        /// </summary>
        /// <param name="runId">The runtime run identifier.</param>
        /// <returns>
        /// <c>true</c> when the run is currently queued locally; otherwise, <c>false</c>.
        /// </returns>
        private bool IsQueued(
            string runId)
        {
            return _queuedRuns.ContainsKey(runId);
        }

        /// <summary>
        /// Creates an immutable visibility snapshot for a runtime pipeline run.
        /// </summary>
        /// <param name="handle">The runtime worker run handle.</param>
        /// <param name="pipelineName">The pipeline name associated with the run.</param>
        /// <param name="runtimeInstanceId">The runtime instance id that owns the local run.</param>
        /// <param name="isQueued">Indicates whether the run is currently queued locally.</param>
        /// <param name="isRunning">Indicates whether the run is currently running locally.</param>
        /// <returns>An immutable runtime pipeline run state snapshot.</returns>
        private static AiRuntimePipelineRunState CreateRunState(
            AiRuntimeWorkerRunHandle handle,
            string? pipelineName,
            string? runtimeInstanceId,
            bool isQueued,
            bool isRunning)
        {
            return new AiRuntimePipelineRunState
            {
                RunId = handle.RunId,
                ExecutionId = handle.ExecutionId,
                PipelineKey = pipelineName,
                PipelineName = pipelineName,
                RuntimeInstanceId = runtimeInstanceId,
                Status = ToStableRunStatus(handle.Status),
                IsQueued = isQueued,
                IsRunning = isRunning,
                CancellationRequested = handle.Status == AiRuntimeWorkerRunStatus.Cancelled,
                QueuedAtUtc = null,
                StartedAtUtc = null,
                CompletedAtUtc = handle.Completion.IsCompleted
                    ? DateTimeOffset.UtcNow
                    : null,
                FailureReason = handle.Completion.IsFaulted
                    ? CreateCompletionFailureReason(
                        handle.Completion.Exception)
                    : null
            };
        }

        /// <summary>
        /// Converts the runtime worker run status to a stable lowercase status value.
        /// </summary>
        /// <param name="status">The runtime worker run status.</param>
        /// <returns>
        /// A stable lowercase status value suitable for control-plane visibility,
        /// diagnostics, logs, dashboards, and future Kubernetes observability.
        /// </returns>
        private static string ToStableRunStatus(
            AiRuntimeWorkerRunStatus status)
        {
            return status switch
            {
                AiRuntimeWorkerRunStatus.Queued => "queued",
                AiRuntimeWorkerRunStatus.CreatingExecution => "creating-execution",
                AiRuntimeWorkerRunStatus.Running => "running",
                AiRuntimeWorkerRunStatus.Completed => "completed",
                AiRuntimeWorkerRunStatus.Failed => "failed",
                AiRuntimeWorkerRunStatus.Cancelled => "cancelled",

                _ => "unknown"
            };
        }

        /// <summary>
        /// Attaches runtime run diagnostics to an exception without changing the original exception type.
        /// </summary>
        private static void AttachRunExceptionData(
            Exception exception,
            string phase,
            AiRuntimeQueuedPipelineRun queuedRun,
            string? executionId = null,
            int? definitionStepCount = null)
        {
            ArgumentNullException.ThrowIfNull(exception);
            ArgumentNullException.ThrowIfNull(queuedRun);

            TrySetExceptionData(
                exception,
                "ai.runtime.phase",
                phase);

            TrySetExceptionData(
                exception,
                "ai.runtime.run.id",
                queuedRun.Handle.RunId);

            TrySetExceptionData(
                exception,
                "ai.runtime.execution.id",
                executionId ?? queuedRun.Handle.ExecutionId ?? string.Empty);

            TrySetExceptionData(
                exception,
                "ai.runtime.pipeline.name",
                queuedRun.Request.PipelineName);

            TrySetExceptionData(
                exception,
                "ai.runtime.input.type",
                ResolveInputTypeName(queuedRun.Request.Input));

            if (definitionStepCount.HasValue)
            {
                TrySetExceptionData(
                    exception,
                    "ai.runtime.definition.step.count",
                    definitionStepCount.Value.ToString());
            }
        }

        /// <summary>
        /// Builds a stable failure reason that can be stored in runtime queue status and run indexes.
        /// </summary>
        private static string CreateRunFailureReason(
            string phase,
            AiRuntimeQueuedPipelineRun queuedRun,
            Exception exception,
            string? executionId = null)
        {
            ArgumentNullException.ThrowIfNull(queuedRun);
            ArgumentNullException.ThrowIfNull(exception);

            var root =
                exception.GetBaseException();

            return Truncate(
                $"Runtime pipeline run failed. " +
                $"Phase='{phase}', " +
                $"RunId='{queuedRun.Handle.RunId}', " +
                $"ExecutionId='{executionId ?? queuedRun.Handle.ExecutionId ?? string.Empty}', " +
                $"Pipeline='{queuedRun.Request.PipelineName}', " +
                $"RuntimeStatus='{queuedRun.Handle.Status}', " +
                $"ExceptionType='{exception.GetType().FullName}', " +
                $"RootExceptionType='{root.GetType().FullName}', " +
                $"Message='{exception.Message}', " +
                $"RootMessage='{root.Message}'. " +
                $"StackTrace='{exception.StackTrace ?? string.Empty}'.",
                12000);
        }

        /// <summary>
        /// Builds a verbose log message for failed background runs.
        /// </summary>
        private string CreateRunFailureLogMessage(
            string title,
            AiRuntimeQueuedPipelineRun queuedRun,
            Exception exception,
            string? executionId = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentNullException.ThrowIfNull(queuedRun);
            ArgumentNullException.ThrowIfNull(exception);

            var phase =
                ResolveExceptionPhase(
                    exception);

            var root =
                exception.GetBaseException();

            return
                $"[AI PIPELINE CONTROLLER] {title} " +
                $"Phase='{phase}', " +
                $"RunId='{queuedRun.Handle.RunId}', " +
                $"ExecutionId='{executionId ?? queuedRun.Handle.ExecutionId ?? string.Empty}', " +
                $"Pipeline='{queuedRun.Request.PipelineName}', " +
                $"RuntimeInstanceId='{_runtimeInstanceIdentity.RuntimeInstanceId}', " +
                $"InputType='{ResolveInputTypeName(queuedRun.Request.Input)}', " +
                $"RunStatus='{queuedRun.Handle.Status}', " +
                $"ExceptionType='{exception.GetType().FullName}', " +
                $"RootExceptionType='{root.GetType().FullName}', " +
                $"Message='{exception.Message}', " +
                $"RootMessage='{root.Message}', " +
                $"Exception='{exception}'.";
        }

        /// <summary>
        /// Builds failure metadata for the decision ledger.
        /// </summary>
        private static IReadOnlyDictionary<string, string> CreateRunFailureMetadata(
            string phase,
            AiRuntimeQueuedPipelineRun queuedRun,
            Exception exception,
            string? executionId = null)
        {
            ArgumentNullException.ThrowIfNull(queuedRun);
            ArgumentNullException.ThrowIfNull(exception);

            var root =
                exception.GetBaseException();

            return new Dictionary<string, string>
            {
                ["run.id"] = queuedRun.Handle.RunId,
                ["execution.id"] = executionId ?? queuedRun.Handle.ExecutionId ?? string.Empty,
                ["pipeline.name"] = queuedRun.Request.PipelineName,
                ["runtime.status"] = queuedRun.Handle.Status.ToString(),
                ["failure.phase"] = phase,
                ["input.type"] = ResolveInputTypeName(queuedRun.Request.Input),
                ["exception.type"] = exception.GetType().FullName ?? exception.GetType().Name,
                ["exception.message"] = Truncate(exception.Message, 2000),
                ["exception.stack"] = Truncate(exception.StackTrace, 6000),
                ["root.exception.type"] = root.GetType().FullName ?? root.GetType().Name,
                ["root.exception.message"] = Truncate(root.Message, 2000),
                ["root.exception.stack"] = Truncate(root.StackTrace, 6000)
            };
        }

        /// <summary>
        /// Resolves the best known runtime phase from exception diagnostic data.
        /// </summary>
        private static string ResolveExceptionPhase(
            Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            if (exception.Data.Contains("ai.runtime.phase"))
            {
                return exception.Data["ai.runtime.phase"]?.ToString()
                    ?? "unknown";
            }

            return "unknown";
        }

        /// <summary>
        /// Builds a completion failure reason for public run state visibility.
        /// </summary>
        private static string? CreateCompletionFailureReason(
            AggregateException? exception)
        {
            if (exception is null)
            {
                return null;
            }

            var baseException =
                exception.GetBaseException();

            return Truncate(
                baseException.ToString(),
                12000);
        }

        /// <summary>
        /// Resolves a safe input type name for diagnostics.
        /// </summary>
        private static string ResolveInputTypeName(
            object? input)
        {
            return input?.GetType().FullName
                ?? "(null)";
        }

        /// <summary>
        /// Adds exception diagnostic data without overwriting an existing value.
        /// </summary>
        private static void TrySetExceptionData(
            Exception exception,
            string key,
            string value)
        {
            if (exception.Data.Contains(key))
            {
                return;
            }

            exception.Data[key] =
                value;
        }

        /// <summary>
        /// Truncates long diagnostic strings so they can be safely stored in status records and metadata.
        /// </summary>
        private static string Truncate(
            string? value,
            int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            if (value.Length <= maxLength)
            {
                return value;
            }

            return value[..maxLength] + "...[truncated]";
        }

        /// <summary>
        /// Resolves the best available ledger correlation target for a queue-level operation.
        /// </summary>
        /// <returns>
        /// A queue ledger target using an active execution when possible, otherwise a queued run,
        /// otherwise a controller-level fallback.
        /// </returns>
        /// <remarks>
        /// Queue pause and resume are queue-level operations, but when a run is already active
        /// they should remain execution-correlated for diagnostics, replay visibility,
        /// and integration-test visibility.
        /// </remarks>
        private QueueLedgerTarget ResolveQueueLedgerTarget()
        {
            var runningRun = _runningRuns.Values.FirstOrDefault(
                run => !string.IsNullOrWhiteSpace(run.Handle.ExecutionId));

            if (runningRun is not null)
            {
                return new QueueLedgerTarget
                {
                    ExecutionId = runningRun.Handle.ExecutionId!,
                    RunId = runningRun.Handle.RunId,
                    PipelineName = runningRun.Request.PipelineName
                };
            }

            var queuedRun = _queuedRuns.Values.FirstOrDefault();

            if (queuedRun is not null)
            {
                return new QueueLedgerTarget
                {
                    ExecutionId = queuedRun.Handle.ExecutionId ?? queuedRun.Handle.RunId,
                    RunId = queuedRun.Handle.RunId,
                    PipelineName = queuedRun.Request.PipelineName
                };
            }

            return new QueueLedgerTarget
            {
                ExecutionId = "pipeline-controller",
                RunId = "pipeline-controller",
                PipelineName = "pipeline-controller"
            };
        }

        /// <summary>
        /// Represents the ledger correlation target for a queue-level controller operation.
        /// </summary>
        private sealed class QueueLedgerTarget
        {
            /// <summary>
            /// Gets the execution identifier used to index the ledger event.
            /// </summary>
            public required string ExecutionId { get; init; }

            /// <summary>
            /// Gets the controller run identifier associated with the queue event.
            /// </summary>
            public required string RunId { get; init; }

            /// <summary>
            /// Gets the pipeline name associated with the queue event.
            /// </summary>
            public required string PipelineName { get; init; }
        }
    }
}