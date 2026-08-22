using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.ExecutionAssistance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
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
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery.Observability;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Observability;
using Multiplexed.AI.Runtime.Observability.Helpers;
using Multiplexed.AI.Runtime.Observability.Logging;
using Multiplexed.Rbac.Core.ExecutionContext;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using ExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;
using Multiplexed.Abstractions.AI.Execution.Context;
using Multiplexed.Abstractions.AI.Observability.Events;

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
    /// Two narrow existing-execution paths intentionally bypass creation: controlled crash-recovery resume and
    /// normal external-wait continuation. Recovery keeps its ownership and forensic semantics, while an external
    /// continuation targets one durable <see cref="AiStepExecutionStatus.WaitingForExternal"/> step without using
    /// recovery metadata. Neither path creates the execution again.
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
        private readonly IAiControlPlaneObserver _observer;

        private readonly AiRuntimePipelineBackgroundControllerOptions _options;
        private readonly Channel<AiRuntimeQueuedPipelineRun> _queue;
        private readonly SemaphoreSlim _parallelismGate;
        private readonly ConcurrentDictionary<string, Task> _activeRuns = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, AiRuntimeQueuedPipelineRun> _queuedRuns = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, AiRuntimeQueuedPipelineRun> _runningRuns = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _externallyWaitingCapacityReleasedRuns = new(StringComparer.Ordinal);
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
        /// <param name="forensicsRecorder">The runtime recovery forensics recorder retained for direct-construction compatibility.</param>
        /// <param name="observer">The centralized control-plane Event Manager when provided by dependency injection.</param>
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
            IAiRuntimeRecoveryForensicsRecorder forensicsRecorder,
            IAiControlPlaneObserver? observer = null)
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
            ArgumentNullException.ThrowIfNull(forensicsRecorder);
            _observer = observer is null
                ? AiRecoveryObservabilityCompatibility.Create(forensicsRecorder)
                : AiRecoveryObservabilityCompatibility.Compose(observer, forensicsRecorder);

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

            cancellationToken.ThrowIfCancellationRequested();

            Console.WriteLine(
                $"[AI PIPELINE CONTROLLER] ENQUEUE CALLED ControllerHash='{GetHashCode()}' RuntimeInstanceId='{_runtimeInstanceIdentity.RuntimeInstanceId}' ResumeExecutionId='{resumeExecutionId ?? string.Empty}'");

            if (_options.RejectEnqueueWhenStopped &&
                !_started)
            {
                throw new InvalidOperationException(
                    "The runtime pipeline background controller has not been started.");
            }

            if (_stopped)
            {
                throw new InvalidOperationException(
                    "The runtime pipeline background controller has been stopped and cannot accept new work.");
            }

            var externalWaitContinuation =
                ResolveExternalWaitContinuation(request, resumeExecutionId);

            var recoveryResume =
                string.IsNullOrWhiteSpace(resumeExecutionId)
                    ? null
                    : ResolveRecoveryResume(
                        request,
                        resumeExecutionId);

            var runId = recoveryResume is null
                ? Guid.NewGuid().ToString("N")
                : CreateDeterministicRecoveryRunId(
                    recoveryResume.RecoveryOwnerId);

            var correlation =
                new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = runId,
                    RunId = runId,
                    ExecutionId = resumeExecutionId ?? externalWaitContinuation?.ExecutionId,
                    PipelineName = request.PipelineName,
                    RuntimeInstanceId =
                        _runtimeInstanceIdentity.RuntimeInstanceId,
                    WorkerId =
                        PipelineBackgroundControllerWorkerId
                };

            var completionSource =
                new TaskCompletionSource<AiExecutionRecord>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            var existingExecutionId =
                resumeExecutionId;

            var handle =
                string.IsNullOrWhiteSpace(existingExecutionId)
                    ? new AiRuntimeWorkerRunHandle(
                        runId,
                        completionSource.Task)
                    : new AiRuntimeWorkerRunHandle(
                        runId,
                        completionSource.Task,
                        existingExecutionId);

            var queuedRun =
                new AiRuntimeQueuedPipelineRun(
                    request,
                    handle,
                    completionSource,
                    correlation,
                    resumeExecutionId);

            if (externalWaitContinuation is not null)
            {
                await RegisterExternalWaitQueuedRunAsync(
                        queuedRun,
                        externalWaitContinuation,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (recoveryResume is not null)
            {
                var registeredByThisCaller =
                    await TryRegisterResumeRunExecutionIndexAsync(
                            queuedRun,
                            recoveryResume,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (!registeredByThisCaller)
                {
                    var canonicalEntry = await _runExecutionIndex
                        .GetAsync(
                            runId,
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    ValidateIdempotentRecoveryAcceptance(
                        canonicalEntry,
                        recoveryResume,
                        runId);

                    var canonicalHandle =
                        CreateIdempotentRecoveryAcceptanceHandle(
                            canonicalEntry!,
                            request.PipelineName,
                            recoveryResume.ExecutionId);

                    _logger.Engine.LogInformation(
                        $"[AI PIPELINE CONTROLLER] Recovery resume enqueue resolved to existing durable acceptance. RunId='{runId}', ExecutionId='{recoveryResume.ExecutionId}', RecoveryOwnerId='{recoveryResume.RecoveryOwnerId}', CanonicalRuntimeInstanceId='{canonicalEntry!.RuntimeInstanceId ?? string.Empty}', RequestedRuntimeInstanceId='{_runtimeInstanceIdentity.RuntimeInstanceId}'.");

                    return canonicalHandle;
                }
            }

            _queuedRuns[runId] =
                queuedRun;

            try
            {
                await _queue.Writer
                    .WriteAsync(
                        queuedRun,
                        recoveryResume is null && externalWaitContinuation is null
                            ? cancellationToken
                            : CancellationToken.None)
                    .ConfigureAwait(false);

                Console.WriteLine(
                    $"[AI PIPELINE CONTROLLER] ENQUEUED RuntimeInstanceId='{_runtimeInstanceIdentity.RuntimeInstanceId}' RunId='{runId}' ResumeExecutionId='{resumeExecutionId ?? string.Empty}'");
            }
            catch (Exception exception)
            {
                _queuedRuns.TryRemove(
                    runId,
                    out _);

                if (externalWaitContinuation is not null)
                {
                    try
                    {
                        await _runExecutionIndex
                            .MarkFailedAsync(
                                runId,
                                null,
                                $"External-wait continuation local acceptance failed before channel enqueue. ExceptionType='{exception.GetType().FullName}', Message='{exception.Message}'.",
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception indexException)
                    {
                        _logger.Engine.LogWarning(
                            $"[AI PIPELINE CONTROLLER] External-wait queued acceptance failure could not be persisted. RunId='{runId}', ParentExecutionId='{externalWaitContinuation.ExecutionId}', ExceptionType='{indexException.GetType().FullName}', Message='{indexException.Message}'.");
                    }
                }

                if (recoveryResume is not null)
                {
                    try
                    {
                        await _runExecutionIndex
                            .MarkFailedAsync(
                                runId,
                                recoveryResume.ExecutionId,
                                $"Recovery resume local acceptance failed before channel enqueue. ExceptionType='{exception.GetType().FullName}', Message='{exception.Message}'.",
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception indexException)
                    {
                        _logger.Engine.LogWarning(
                            $"[AI PIPELINE CONTROLLER] Recovery resume acceptance failure could not be persisted. RunId='{runId}', ExecutionId='{recoveryResume.ExecutionId}', ExceptionType='{indexException.GetType().FullName}', Message='{indexException.Message}'.");
                    }
                }

                throw;
            }

            if (recoveryResume is not null)
            {
                try
                {
                    await RecordRecoveryForensicsEventAsync(
                            queuedRun,
                            AiEngineEvents.Recovery.ReplacementLocalRunRegistered,
                            "registered",
                            "replacement-local-run-registered-on-runtime-instance",
                            recoveryResume.ExecutionId,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.Engine.LogWarning(
                        $"[AI PIPELINE CONTROLLER] Accepted recovery resume forensics recording failed without reverting local acceptance. RunId='{runId}', ExecutionId='{recoveryResume.ExecutionId}', RecoveryOwnerId='{recoveryResume.RecoveryOwnerId}', ExceptionType='{exception.GetType().FullName}', Message='{exception.Message}'.");
                }
            }

            _logger.Engine.LogInformation(
                $"[AI PIPELINE CONTROLLER] Run queued. RunId='{runId}', Pipeline='{request.PipelineName}', ResumeExecutionId='{resumeExecutionId ?? string.Empty}', ExternalWaitExecutionId='{externalWaitContinuation?.ExecutionId ?? string.Empty}', ExternalWaitStep='{externalWaitContinuation?.StepName ?? string.Empty}', RecoveryOwnerId='{recoveryResume?.RecoveryOwnerId ?? string.Empty}'.");

            /*
             * The Channel write above is the local runtime acceptance boundary.
             * From this point onward, transport cancellation or an auxiliary ledger
             * failure must not convert an accepted run into a failed dispatch result.
             */
            try
            {
                await RecordRunLedgerAsync(
                        runId,
                        request.PipelineName,
                        AiEngineEvents.Run.Queued,
                        AiDecisionLedgerOutcome.Persisted,
                        reason: recoveryResume is not null
                            ? "Pipeline run queued for existing execution recovery resume."
                            : externalWaitContinuation is not null
                                ? "Pipeline run queued for normal external-wait continuation."
                                : "Pipeline run queued.",
                        metadata: new Dictionary<string, string>
                        {
                            [AiRunMetadataKeys.RunId] =
                                runId,

                            [AiPipelineMetadataKeys.Name] =
                                request.PipelineName,

                            [AiRuntimeRecoveryMetadataKeys.Resume] =
                                (recoveryResume is not null).ToString(),

                            [AiRuntimeRecoveryMetadataKeys.ExecutionId] =
                                recoveryResume?.ExecutionId ??
                                string.Empty,

                            [AiRuntimeRecoveryMetadataKeys.OwnerId] =
                                recoveryResume?.RecoveryOwnerId ??
                                string.Empty,

                            [AiRuntimeExternalWaitMetadataKeys.Continuation] =
                                (externalWaitContinuation is not null).ToString(),

                            [AiRuntimeExternalWaitMetadataKeys.ExecutionId] =
                                externalWaitContinuation?.ExecutionId ??
                                string.Empty,

                            [AiRuntimeExternalWaitMetadataKeys.Step] =
                                externalWaitContinuation?.StepName ??
                                string.Empty,

                            [AiRuntimeExternalWaitMetadataKeys.ContinuationId] =
                                externalWaitContinuation?.ContinuationId ??
                                string.Empty
                        },
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Engine.LogWarning(
                    $"[AI PIPELINE CONTROLLER] Accepted run ledger recording failed without reverting enqueue acknowledgement. RunId='{runId}', Pipeline='{request.PipelineName}', ResumeExecutionId='{resumeExecutionId ?? string.Empty}', ExceptionType='{exception.GetType().FullName}', Message='{exception.Message}'.");
            }

            return handle;
        }

        /// <summary>
        /// Resolves and validates one normal external-wait continuation request.
        /// </summary>
        /// <param name="request">The submitted runtime pipeline request.</param>
        /// <param name="resumeExecutionId">The optional crash-recovery execution identifier.</param>
        /// <returns>The validated continuation, or <see langword="null"/> when this is not a continuation request.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a normal continuation attempts to reuse execution-creation or crash-recovery semantics.
        /// </exception>
        private static AiRuntimeExternalWaitContinuation? ResolveExternalWaitContinuation(
            AiRuntimePipelineRunRequest request,
            string? resumeExecutionId)
        {
            ArgumentNullException.ThrowIfNull(request);

            var continuation = request.ExternalWaitContinuation;
            if (continuation is null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(resumeExecutionId))
            {
                throw new InvalidOperationException(
                    "Normal external-wait continuation cannot be combined with crash-recovery resume.");
            }

            if (!string.IsNullOrWhiteSpace(request.RequestedExecutionId))
            {
                throw new InvalidOperationException(
                    "Normal external-wait continuation cannot request execution creation.");
            }

            if (request.PipelineDefinitionSnapshot is not null ||
                request.PipelineDefinition is not null ||
                !string.IsNullOrWhiteSpace(request.PipelineJson) ||
                !string.IsNullOrWhiteSpace(request.PipelineJsonFilePath) ||
                request.Input is not null)
            {
                throw new InvalidOperationException(
                    "Normal external-wait continuation cannot provide a pipeline definition or execution input.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(continuation.ExecutionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(continuation.StepName);
            ArgumentException.ThrowIfNullOrWhiteSpace(continuation.ContinuationId);

            if (request.ExecutionContextSnapshot is null)
            {
                throw new InvalidOperationException(
                    "Normal external-wait continuation requires the durable parent execution context snapshot.");
            }

            var metadata = GetPipelineRunMetadata(request);
            if (TryGetMetadataValue(metadata, AiRuntimeRecoveryMetadataKeys.Mode, out _))
            {
                throw new InvalidOperationException(
                    "Normal external-wait continuation cannot carry crash-recovery mode metadata.");
            }

            return continuation;
        }

        /// <summary>
        /// Registers a normal external-wait continuation as local queued work before transient channel acceptance.
        /// </summary>
        /// <remarks>
        /// The queued index entry intentionally has no execution identifier until the continuation actually starts.
        /// If the runtime disappears before local channel delivery, the existing local-queued recovery path requeues
        /// the shared run instead of misclassifying the parent as an in-flight crash-recovery resume.
        /// </remarks>
        /// <param name="queuedRun">The queued runtime pipeline run.</param>
        /// <param name="continuation">The validated continuation identity.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task RegisterExternalWaitQueuedRunAsync(
            AiRuntimeQueuedPipelineRun queuedRun,
            AiRuntimeExternalWaitContinuation continuation,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(queuedRun);
            ArgumentNullException.ThrowIfNull(continuation);

            var metadata = new Dictionary<string, string>(
                GetPipelineRunMetadata(queuedRun.Request),
                StringComparer.OrdinalIgnoreCase)
            {
                [AiPipelineMetadataKeys.Name] = queuedRun.Request.PipelineName,
                [AiRuntimeInstanceMetadataKeys.RuntimeInstanceId] = _runtimeInstanceIdentity.RuntimeInstanceId,
                [AiRuntimeExternalWaitMetadataKeys.Continuation] = "true",
                [AiRuntimeExternalWaitMetadataKeys.ExecutionId] = continuation.ExecutionId,
                [AiRuntimeExternalWaitMetadataKeys.Step] = continuation.StepName,
                [AiRuntimeExternalWaitMetadataKeys.ContinuationId] = continuation.ContinuationId,
                [AiExecutionContextMetadataKeys.ContextKey] = queuedRun.Request.ExecutionContextSnapshot?.ContextKey ?? string.Empty,
                [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = queuedRun.Request.ExecutionContextSnapshot?.TenantId ?? string.Empty,
                [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = queuedRun.Request.ExecutionContextSnapshot?.TenantGroupId ?? string.Empty
            };

            var registered = await _runExecutionIndex
                .TryRegisterQueuedAsync(
                    new AiRuntimeRunExecutionIndexEntry
                    {
                        RunId = queuedRun.Handle.RunId,
                        ExecutionId = null,
                        RuntimeInstanceId = _runtimeInstanceIdentity.RuntimeInstanceId,
                        Status = "queued",
                        CreatedAtUtc = DateTimeOffset.UtcNow,
                        ExecutionContextSnapshot = queuedRun.Request.ExecutionContextSnapshot!,
                        Metadata = metadata
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!registered)
            {
                throw new InvalidOperationException(
                    $"External-wait continuation local run id collision. RunId='{queuedRun.Handle.RunId}', ExecutionId='{continuation.ExecutionId}', Step='{continuation.StepName}'.");
            }
        }

        /// <summary>
        /// Atomically registers the durable acceptance of a recovery resume run.
        /// </summary>
        /// <param name="queuedRun">The queued runtime pipeline run.</param>
        /// <param name="recoveryResume">The validated recovery resume identity.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// <c>true</c> when this runtime instance owns the first durable acceptance;
        /// otherwise, <c>false</c>.
        /// </returns>
        private async Task<bool> TryRegisterResumeRunExecutionIndexAsync(
            AiRuntimeQueuedPipelineRun queuedRun,
            RecoveryResumeContext recoveryResume,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(queuedRun);
            ArgumentNullException.ThrowIfNull(recoveryResume);

            var registered = await _runExecutionIndex
                .TryRegisterQueuedAsync(new AiRuntimeRunExecutionIndexEntry
                {
                    RunId = queuedRun.Handle.RunId,
                    ExecutionId = recoveryResume.ExecutionId,
                    RuntimeInstanceId = _runtimeInstanceIdentity.RuntimeInstanceId,
                    Status = "queued",
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    ExecutionContextSnapshot = queuedRun.Request.ExecutionContextSnapshot,
                    Metadata = MergeRecoveryMetadata(
                        new Dictionary<string, string>
                        {
                            [AiPipelineMetadataKeys.Name] = queuedRun.Request.PipelineName,
                            [AiRuntimeInstanceMetadataKeys.RuntimeInstanceId] = _runtimeInstanceIdentity.RuntimeInstanceId,
                            [AiRuntimeRecoveryMetadataKeys.Resume] = "true",
                            [AiRuntimeRecoveryMetadataKeys.ExecutionId] = recoveryResume.ExecutionId,
                            [AiRuntimeRecoveryMetadataKeys.OwnerId] = recoveryResume.RecoveryOwnerId,
                            [AiExecutionContextMetadataKeys.ContextKey] = queuedRun.Request.ExecutionContextSnapshot?.ContextKey ?? string.Empty,
                            [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = queuedRun.Request.ExecutionContextSnapshot?.TenantId ?? string.Empty,
                            [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = queuedRun.Request.ExecutionContextSnapshot?.TenantGroupId ?? string.Empty
                        },
                        GetPipelineRunMetadata(queuedRun.Request))
                },
                cancellationToken)
                .ConfigureAwait(false);

            _logger.Engine.LogInformation(
                $"[AI PIPELINE CONTROLLER] Recovery resume acceptance registration completed. RunId='{queuedRun.Handle.RunId}', ExecutionId='{recoveryResume.ExecutionId}', RecoveryOwnerId='{recoveryResume.RecoveryOwnerId}', RuntimeInstanceId='{_runtimeInstanceIdentity.RuntimeInstanceId}', RegisteredByThisCaller='{registered}', Pipeline='{queuedRun.Request.PipelineName}'.");

            return registered;
        }

        /// <summary>
        /// Validates that an existing deterministic recovery run belongs to the same recovery operation.
        /// </summary>
        private static void ValidateIdempotentRecoveryAcceptance(
            AiRuntimeRunExecutionIndexEntry? entry,
            RecoveryResumeContext recoveryResume,
            string expectedRunId)
        {
            if (entry is null)
            {
                throw new InvalidOperationException(
                    $"Recovery resume acceptance '{expectedRunId}' already exists but its durable runtime run entry could not be resolved.");
            }

            var hasRecoveryOwner =
                TryGetRecoveryMetadataValue(
                    entry.Metadata,
                    AiRuntimeRecoveryMetadataKeys.OwnerId,
                    out var existingRecoveryOwnerId);

            if (!string.Equals(
                    entry.RunId,
                    expectedRunId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    entry.ExecutionId,
                    recoveryResume.ExecutionId,
                    StringComparison.Ordinal) ||
                !hasRecoveryOwner ||
                !string.Equals(
                    existingRecoveryOwnerId,
                    recoveryResume.RecoveryOwnerId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Recovery resume acceptance collision. RunId='{expectedRunId}', ExpectedExecutionId='{recoveryResume.ExecutionId}', ExistingExecutionId='{entry.ExecutionId ?? string.Empty}', ExpectedRecoveryOwnerId='{recoveryResume.RecoveryOwnerId}', ExistingRecoveryOwnerId='{existingRecoveryOwnerId ?? string.Empty}', ExistingRuntimeInstanceId='{entry.RuntimeInstanceId ?? string.Empty}'.");
            }
        }

        /// <summary>
        /// Creates a non-owning handle that reflects an already accepted canonical recovery run.
        /// </summary>
        private static AiRuntimeWorkerRunHandle CreateIdempotentRecoveryAcceptanceHandle(
            AiRuntimeRunExecutionIndexEntry entry,
            string pipelineName,
            string executionId)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            var status = entry.Status?.Trim().ToLowerInvariant() ?? "queued";

            if (status is AiRuntimeRunExecutionIndexStatuses.Failed or AiRuntimeRunExecutionIndexStatuses.Cancelled or AiRuntimeRunExecutionIndexStatuses.RequeuedForRecovery)
            {
                throw new InvalidOperationException(
                    $"Recovery resume acceptance '{entry.RunId}' is already terminal and cannot acknowledge a duplicate dispatch. Status='{entry.Status ?? string.Empty}', ExecutionId='{entry.ExecutionId ?? string.Empty}', RuntimeInstanceId='{entry.RuntimeInstanceId ?? string.Empty}', FailureReason='{entry.FailureReason ?? string.Empty}'.");
            }

            var executionStatus = status switch
            {
                "completed" => AiExecutionStatus.Completed,
                "running" => AiExecutionStatus.Running,
                "queued" => AiExecutionStatus.Pending,
                _ => throw new InvalidOperationException(
                    $"Recovery resume acceptance '{entry.RunId}' has unsupported status '{entry.Status ?? string.Empty}'.")
            };

            Task<AiExecutionRecord> completion;

            if (executionStatus == AiExecutionStatus.Completed)
            {
                completion = Task.FromResult(new AiExecutionRecord
                {
                    ExecutionId = executionId,
                    PipelineName = pipelineName,
                    Status = AiExecutionStatus.Completed,
                    CompletedAtUtc = entry.CompletedAtUtc?.UtcDateTime ?? DateTime.UtcNow
                });
            }
            else
            {
                var completionSource =
                    new TaskCompletionSource<AiExecutionRecord>(
                        TaskCreationOptions.RunContinuationsAsynchronously);

                completion = completionSource.Task;
            }

            var handle = new AiRuntimeWorkerRunHandle(
                entry.RunId,
                completion,
                executionId);

            if (executionStatus == AiExecutionStatus.Completed)
            {
                handle.MarkCompleted();
            }
            else if (executionStatus == AiExecutionStatus.Running)
            {
                handle.MarkRunning(executionId);
            }

            return handle;
        }

        /// <summary>
        /// Creates one stable local run identifier for a deterministic recovery owner.
        /// </summary>
        private static string CreateDeterministicRecoveryRunId(
            string recoveryOwnerId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(recoveryOwnerId);

            var hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(recoveryOwnerId));

            return Convert.ToHexString(hash)
                .ToLowerInvariant()[..32];
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
                    eventType: AiEngineEvents.Queue.Paused,
                    outcome: AiDecisionLedgerOutcome.Applied,
                    reason: reason ?? "Pipeline controller queue paused.",
                    metadata: new Dictionary<string, string>
                    {
                        [AiExecutionControlMetadataKeys.RequestedBy] = requestedBy ?? string.Empty,
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
                    eventType: AiEngineEvents.Queue.Resumed,
                    outcome: AiDecisionLedgerOutcome.Applied,
                    reason: "Pipeline controller queue resumed.",
                    metadata: new Dictionary<string, string>
                    {
                        [AiExecutionControlMetadataKeys.RequestedBy] = requestedBy ?? string.Empty,
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
                    AiEngineEvents.Run.Cancelled,
                    AiDecisionLedgerOutcome.Cancelled,
                    reason: reason ?? "Queued pipeline run cancelled before execution creation.",
                    metadata: new Dictionary<string, string>
                    {
                        [AiRunMetadataKeys.RunId] = runId,
                        [AiPipelineMetadataKeys.Name] = queuedRun.Request.PipelineName,
                        [AiExecutionControlMetadataKeys.RequestedBy] = requestedBy ?? string.Empty,
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
                            AiEngineEvents.Run.Dequeued,
                            AiDecisionLedgerOutcome.Started,
                            reason: "Pipeline run dequeued for processing.",
                            metadata: new Dictionary<string, string>
                            {
                                [AiRunMetadataKeys.RunId] = queuedRun.Handle.RunId,
                                [AiPipelineMetadataKeys.Name] = queuedRun.Request.PipelineName,
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

                            var capacityWasReleasedForExternalWait =
                                _externallyWaitingCapacityReleasedRuns.TryRemove(
                                    queuedRun.Handle.RunId,
                                    out _);

                            _runningRuns.TryRemove(
                                queuedRun.Handle.RunId,
                                out _);

                            if (!capacityWasReleasedForExternalWait)
                            {
                                _parallelismGate.Release();
                            }

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
                        AiEngineEvents.Run.Cancelled,
                        AiDecisionLedgerOutcome.Cancelled,
                        handle.ExecutionId,
                        "Pipeline run cancelled by controller cancellation token.",
                        new Dictionary<string, string>
                        {
                            [AiRunMetadataKeys.RunId] = handle.RunId,
                            [AiPipelineMetadataKeys.Name] = request.PipelineName,
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
                        AiEngineEvents.Run.Failed,
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

        /// <summary>
        /// Releases one runtime execution slot immediately after the durable run index proves that the
        /// execution has entered external waiting.
        /// </summary>
        /// <remarks>
        /// <para>
        /// External waiting is a capacity-release boundary. Once <c>MarkWaitingAsync</c> has committed,
        /// continuation scheduling must not depend on ancillary observability work completing first.
        /// </para>
        /// <para>
        /// The background task remains tracked in <see cref="_activeRuns"/> until all post-transition work
        /// finishes so shutdown still observes it. The release marker makes the normal task continuation
        /// idempotent and prevents a second semaphore release.
        /// </para>
        /// </remarks>
        /// <param name="queuedRun">The runtime run that has durably entered external waiting.</param>
        private void ReleaseExternallyWaitingRunCapacity(
            AiRuntimeQueuedPipelineRun queuedRun)
        {
            ArgumentNullException.ThrowIfNull(queuedRun);

            var runId = queuedRun.Handle.RunId;

            if (!_runningRuns.TryRemove(runId, out _))
            {
                return;
            }

            if (!_externallyWaitingCapacityReleasedRuns.TryAdd(runId, 0))
            {
                throw new InvalidOperationException(
                    $"Runtime run '{runId}' attempted to release external-wait capacity more than once.");
            }

            _parallelismGate.Release();

            _logger.Engine.LogInformation(
                $"[AI PIPELINE CONTROLLER] External-wait capacity released. RunId='{runId}', ExecutionId='{queuedRun.Handle.ExecutionId ?? string.Empty}', RuntimeInstanceId='{_runtimeInstanceIdentity.RuntimeInstanceId}'.");
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
            var activeRunCount = Math.Max(
                0,
                _activeRuns.Count - _externallyWaitingCapacityReleasedRuns.Count);
            var queueCapacity =
                Math.Max(
                    0,
                    _options.QueueCapacity);

            /*
             * A zero-capacity local queue is a valid direct-execution policy.
             * The internal channel still has one transit slot, but a run waiting
             * in that transit slot already consumes immediate execution capacity.
             */
            var queuedRunsOccupyingImmediateCapacity =
                queueCapacity == 0
                    ? queuedRunCount
                    : 0;

            var availableRunSlots =
                Math.Max(
                    0,
                    _options.MaxConcurrentRuns -
                    runningRunCount -
                    queuedRunsOccupyingImmediateCapacity);

            var availableQueueSlots =
                Math.Max(
                    0,
                    queueCapacity - queuedRunCount);

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
                (availableRunSlots > 0 ||
                 availableQueueSlots > 0);

            return Task.FromResult(
                new AiRuntimePipelineQueueState
                {
                    RuntimeInstanceId = _runtimeInstanceIdentity.RuntimeInstanceId,
                    IsPaused = _queuePaused,
                    QueuedRunCount = queuedRunCount,
                    RunningRunCount = runningRunCount,
                    ActiveRunCount = activeRunCount,
                    QueueCapacity = queueCapacity,
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
        /// create or resume an execution, and advance it through the runtime worker.
        /// </summary>
        /// <param name="queuedRun">The queued pipeline run.</param>
        /// <param name="cancellationToken">The controller cancellation token.</param>
        private async Task ProcessQueuedRunCoreAsync(
            AiRuntimeQueuedPipelineRun queuedRun,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(queuedRun);

            var handle = queuedRun.Handle;
            var request = queuedRun.Request;
            var diagnosticPhase = "initializing";

            AiExecutionRecord? created = null;
            AiPipelineDefinition? definition = null;
            RecoveryResumeContext? recoveryResume = null;
            AiRuntimeExternalWaitContinuation? externalWaitContinuation = null;
            var assistanceCandidateRegistered = false;

            var previousExecutionContext = _executionContextAccessor.Current;
            var executionContextRestored = false;

            try
            {
                diagnosticPhase = "restore-execution-context";

                var restoredExecutionContext = RestoreExecutionContextFromSnapshot(queuedRun);
                executionContextRestored = true;

                if (queuedRun.IsResume)
                {
                    diagnosticPhase = "seed-resume-execution-context";

                    await SeedResumeExecutionContextAsync(
                            queuedRun,
                            restoredExecutionContext,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (queuedRun.IsExternalWaitContinuation)
                {
                    externalWaitContinuation = ResolveExternalWaitContinuation(request, resumeExecutionId: null)
                        ?? throw new InvalidOperationException(
                            "External-wait continuation metadata disappeared after queue acceptance.");

                    diagnosticPhase = "seed-external-wait-execution-context";

                    await SeedExternalWaitExecutionContextAsync(
                            queuedRun,
                            externalWaitContinuation,
                            restoredExecutionContext,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                diagnosticPhase = queuedRun.IsExternalWaitContinuation
                    ? "mark-continuing-execution"
                    : "mark-creating-execution";
                handle.MarkCreatingExecution();

                _logger.Engine.LogInformation(
                    queuedRun.IsResume
                        ? $"[AI PIPELINE CONTROLLER] Preparing existing execution recovery resume. RunId='{handle.RunId}', ExecutionId='{queuedRun.ResumeExecutionId}', Pipeline='{request.PipelineName}', RuntimeInstanceId='{_runtimeInstanceIdentity.RuntimeInstanceId}', TenantId='{request.ExecutionContextSnapshot?.TenantId ?? string.Empty}', ContextKey='{request.ExecutionContextSnapshot?.ContextKey ?? string.Empty}'."
                        : externalWaitContinuation is not null
                            ? $"[AI PIPELINE CONTROLLER] Preparing normal external-wait continuation. RunId='{handle.RunId}', ExecutionId='{externalWaitContinuation.ExecutionId}', Step='{externalWaitContinuation.StepName}', ContinuationId='{externalWaitContinuation.ContinuationId}', Pipeline='{request.PipelineName}', RuntimeInstanceId='{_runtimeInstanceIdentity.RuntimeInstanceId}', TenantId='{request.ExecutionContextSnapshot?.TenantId ?? string.Empty}', ContextKey='{request.ExecutionContextSnapshot?.ContextKey ?? string.Empty}'."
                            : $"[AI PIPELINE CONTROLLER] Creating execution. RunId='{handle.RunId}', Pipeline='{request.PipelineName}', RuntimeInstanceId='{_runtimeInstanceIdentity.RuntimeInstanceId}', TenantId='{request.ExecutionContextSnapshot?.TenantId ?? string.Empty}', ContextKey='{request.ExecutionContextSnapshot?.ContextKey ?? string.Empty}', InputType='{ResolveInputTypeName(request.Input)}'.");

                if (externalWaitContinuation is null)
                {
                    diagnosticPhase = "resolve-definition";

                    definition = await _definitionResolver
                        .ResolveAsync(request, cancellationToken)
                        .ConfigureAwait(false);

                    _logger.Engine.LogInformation(
                        $"[AI PIPELINE CONTROLLER] Definition resolved. RunId='{handle.RunId}', Pipeline='{request.PipelineName}', StepCount='{definition.Steps.Count}'.");

                    if (request.PipelineDefinitionSnapshot is null)
                    {
                        diagnosticPhase = "publish-definition";

                        await _definitionPublisher
                            .PublishAsync(definition, cancellationToken)
                            .ConfigureAwait(false);

                        _logger.Engine.LogInformation(
                            $"[AI PIPELINE CONTROLLER] Definition published. RunId='{handle.RunId}', Pipeline='{request.PipelineName}'.");
                    }
                    else
                    {
                        diagnosticPhase = "use-pinned-definition";

                        _logger.Engine.LogInformation(
                            $"[AI PIPELINE CONTROLLER] Immutable execution-bound definition retained without publishing as latest. RunId='{handle.RunId}', Pipeline='{request.PipelineName}', DefinitionHash='{request.PipelineDefinitionSnapshot.ContentHash ?? string.Empty}'.");
                    }
                }

                diagnosticPhase = queuedRun.IsResume
                    ? "validate-recovery-resume"
                    : externalWaitContinuation is not null
                        ? "continue-external-wait"
                        : "create-execution";

                if (queuedRun.IsResume)
                {
                    recoveryResume = ResolveRecoveryResume(queuedRun);

                    diagnosticPhase = "resume-execution-from-recovery";

                    var resumingState = await _executionControlService
                        .ResumeExecutionFromRecoveryAsync(
                            recoveryResume.ExecutionId,
                            recoveryResume.RecoveryOwnerId,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (!IsRecoveryResumeStateOwnedBy(
                            resumingState,
                            recoveryResume.RecoveryOwnerId,
                            AiExecutionControlStatus.Resuming,
                            AiExecutionControlAction.Resume))
                    {
                        throw new InvalidOperationException(
                            $"Execution '{recoveryResume.ExecutionId}' could not enter the recovery-owned resuming state for owner '{recoveryResume.RecoveryOwnerId}'. CurrentStatus='{resumingState.Status}', PendingAction='{resumingState.PendingAction}', RequestedBy='{resumingState.RequestedBy ?? string.Empty}'.");
                    }

                    created = new AiExecutionRecord
                    {
                        ExecutionId = recoveryResume.ExecutionId,
                        PipelineName = request.PipelineName,
                        Status = AiExecutionStatus.Running
                    };

                    _logger.Engine.LogInformation(
                        $"[AI PIPELINE CONTROLLER] Existing execution recovery resume accepted. RunId='{handle.RunId}', ExecutionId='{created.ExecutionId}', RecoveryOwnerId='{recoveryResume.RecoveryOwnerId}', Pipeline='{request.PipelineName}', RuntimeInstanceId='{_runtimeInstanceIdentity.RuntimeInstanceId}'.");

                    diagnosticPhase = "record-recovery-resume-started";

                    await RecordRecoveryForensicsEventAsync(
                            queuedRun,
                            AiEngineEvents.Recovery.DagResumeStarted,
                            "started",
                            "dag-resume-started-on-replacement-runtime",
                            created.ExecutionId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (externalWaitContinuation is not null)
                {
                    created = await _engine
                        .ResumeExternalWaitingStepAsync(
                            externalWaitContinuation.ExecutionId,
                            externalWaitContinuation.StepName,
                            cancellationToken)
                        .ConfigureAwait(false);

                    _logger.Engine.LogInformation(
                        $"[AI PIPELINE CONTROLLER] Normal external-wait continuation accepted. RunId='{handle.RunId}', ExecutionId='{created.ExecutionId}', Step='{externalWaitContinuation.StepName}', ContinuationId='{externalWaitContinuation.ContinuationId}', Pipeline='{created.PipelineName ?? request.PipelineName}', RuntimeInstanceId='{_runtimeInstanceIdentity.RuntimeInstanceId}'.");
                }
                else
                {
                    created = await CreateExecutionAsync(
                            request,
                            definition ?? throw new InvalidOperationException(
                                "Pipeline definition was not resolved before execution creation."),
                            cancellationToken)
                        .ConfigureAwait(false);

                    _logger.Engine.LogInformation(
                        $"[AI PIPELINE CONTROLLER] Execution created. RunId='{handle.RunId}', ExecutionId='{created.ExecutionId}', Pipeline='{created.PipelineName}'.");
                }

                if (!queuedRun.IsResume && externalWaitContinuation is null)
                {
                    var childCreatedEvent = AiChildDagEngineEventFactory.TryCreateExecutionLifecycle(
                        request,
                        created.ExecutionId,
                        _runtimeInstanceIdentity.RuntimeInstanceId,
                        AiEngineEvents.ChildDag.ExecutionCreated);

                    if (childCreatedEvent is not null)
                    {
                        await _observer.RecordAsync(childCreatedEvent, cancellationToken).ConfigureAwait(false);
                    }
                }

                queuedRun.Correlation.ExecutionId = created.ExecutionId;
                queuedRun.Correlation.PipelineKey = created.PipelineName;
                queuedRun.Correlation.RunId = handle.RunId;

                diagnosticPhase = "mark-running";
                handle.MarkRunning(created.ExecutionId);

                diagnosticPhase = "mark-run-execution-index-started";

                await _runExecutionIndex
                    .MarkStartedAsync(
                        handle.RunId,
                        created.ExecutionId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!queuedRun.IsResume && externalWaitContinuation is null)
                {
                    var childStartedEvent = AiChildDagEngineEventFactory.TryCreateExecutionLifecycle(
                        request,
                        created.ExecutionId,
                        _runtimeInstanceIdentity.RuntimeInstanceId,
                        AiEngineEvents.ChildDag.ExecutionStarted);

                    if (childStartedEvent is not null)
                    {
                        await _observer.RecordAsync(childStartedEvent, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (externalWaitContinuation is null)
                {
                    diagnosticPhase = "register-execution-assistance-candidate";

                    await RegisterExecutionAssistanceCandidateAsync(
                            handle.RunId,
                            request,
                            created,
                            definition ?? throw new InvalidOperationException(
                                "Pipeline definition was not resolved before execution assistance registration."),
                            cancellationToken)
                        .ConfigureAwait(false);

                    assistanceCandidateRegistered = true;
                }

                diagnosticPhase = "record-run-started-ledger";

                await RecordRunLedgerAsync(
                        handle.RunId,
                        request.PipelineName,
                        AiEngineEvents.Run.Started,
                        AiDecisionLedgerOutcome.Started,
                        created.ExecutionId,
                        "Pipeline run started execution processing.",
                        new Dictionary<string, string>
                        {
                            [AiRunMetadataKeys.RunId] = handle.RunId,
                            [AiExecutionMetadataKeys.ExecutionId] = created.ExecutionId,
                            [AiPipelineMetadataKeys.Name] = request.PipelineName,
                            [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = request.ExecutionContextSnapshot?.TenantId ?? string.Empty,
                            [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = request.ExecutionContextSnapshot?.TenantGroupId ?? string.Empty,
                            [AiExecutionContextMetadataKeys.ContextKey] = request.ExecutionContextSnapshot?.ContextKey ?? string.Empty,
                            ["distributed.enabled"] = _options.Distributed.Enabled.ToString(),
                            ["distributed.worker.count"] = _options.Distributed.WorkerCount.ToString(),
                            ["max.local.workers.per.execution"] = _options.MaxLocalWorkersPerExecution.ToString(),
                            ["effective.worker.count.per.execution"] = ResolveMaxWorkerCountForExecution().ToString(),
                            [AiRuntimeRecoveryMetadataKeys.Resume] = queuedRun.IsResume.ToString(),
                            [AiRuntimeRecoveryMetadataKeys.ExecutionId] = queuedRun.ResumeExecutionId ?? string.Empty,
                            [AiRuntimeRecoveryMetadataKeys.OwnerId] = recoveryResume?.RecoveryOwnerId ?? string.Empty,
                            [AiRuntimeExternalWaitMetadataKeys.Continuation] = (externalWaitContinuation is not null).ToString(),
                            [AiRuntimeExternalWaitMetadataKeys.ExecutionId] = externalWaitContinuation?.ExecutionId ?? string.Empty,
                            [AiRuntimeExternalWaitMetadataKeys.Step] = externalWaitContinuation?.StepName ?? string.Empty,
                            [AiRuntimeExternalWaitMetadataKeys.ContinuationId] = externalWaitContinuation?.ContinuationId ?? string.Empty
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                AiExecutionRecord? final = null;

                try
                {
                    if (recoveryResume is not null)
                    {
                        diagnosticPhase = "mark-execution-running-from-recovery";

                        var runningState = await _executionControlService
                            .MarkRunningAsync(
                                recoveryResume.ExecutionId,
                                recoveryResume.RecoveryOwnerId,
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (!IsRecoveryResumeStateOwnedBy(
                                runningState,
                                recoveryResume.RecoveryOwnerId,
                                AiExecutionControlStatus.Running,
                                AiExecutionControlAction.None))
                        {
                            throw new InvalidOperationException(
                                $"Execution '{recoveryResume.ExecutionId}' could not enter the recovery-owned running state for owner '{recoveryResume.RecoveryOwnerId}'. CurrentStatus='{runningState.Status}', PendingAction='{runningState.PendingAction}', RequestedBy='{runningState.RequestedBy ?? string.Empty}'.");
                        }
                    }

                    diagnosticPhase = "run-created-execution";

                    final = await RunCreatedExecutionAsync(
                            created.ExecutionId,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (queuedRun.IsResume)
                    {
                        if (final.Status == AiExecutionStatus.Completed)
                        {
                            await RecordRecoveryForensicsEventAsync(
                                    queuedRun,
                                    AiEngineEvents.Recovery.DagResumeCompleted,
                                    "completed",
                                    "dag-resume-completed-on-replacement-runtime",
                                    created.ExecutionId,
                                    cancellationToken)
                                .ConfigureAwait(false);

                            await RecordRecoveryForensicsEventAsync(
                                    queuedRun,
                                    AiEngineEvents.Recovery.ExecutionRecoveryCompleted,
                                    "completed",
                                    "execution-recovery-completed-after-dag-resume",
                                    created.ExecutionId,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else if (final.Status == AiExecutionStatus.Waiting)
                        {
                            await RecordRecoveryForensicsEventAsync(
                                    queuedRun,
                                    AiEngineEvents.Recovery.ExecutionRecoveryCompleted,
                                    "waiting",
                                    "execution-recovery-converged-to-durable-waiting-state",
                                    created.ExecutionId,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await RecordRecoveryForensicsEventAsync(
                                    queuedRun,
                                    AiEngineEvents.Recovery.ExecutionRecoveryFailed,
                                    "failed",
                                    $"execution-recovery-failed-after-dag-resume-status-{final.Status}",
                                    created.ExecutionId,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }

                    diagnosticPhase = "apply-run-status";

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
                    else if (final.Status == AiExecutionStatus.Waiting)
                    {
                        handle.MarkPaused();

                        await _runExecutionIndex
                            .MarkWaitingAsync(
                                handle.RunId,
                                created.ExecutionId,
                                cancellationToken)
                            .ConfigureAwait(false);

                        ReleaseExternallyWaitingRunCapacity(queuedRun);
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

                    if (final.IsTerminal)
                    {
                        diagnosticPhase = "record-terminal-ledger";

                        await RecordRunTerminalLedgerAsync(
                                handle.RunId,
                                request.PipelineName,
                                created.ExecutionId,
                                final,
                                cancellationToken)
                            .ConfigureAwait(false);

                        diagnosticPhase = "invoke-run-finalized-hook";

                        await InvokeRunFinalizedAsync(
                                queuedRun,
                                final,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else if (final.Status == AiExecutionStatus.Waiting)
                    {
                        diagnosticPhase = "record-suspension-ledger";

                        await RecordRunLedgerAsync(
                                handle.RunId,
                                request.PipelineName,
                                AiEngineEvents.Run.Suspended,
                                AiDecisionLedgerOutcome.Applied,
                                created.ExecutionId,
                                "Pipeline run released runtime capacity while the execution waits for an external durable condition.",
                                new Dictionary<string, string>
                                {
                                    [AiRunMetadataKeys.RunId] = handle.RunId,
                                    [AiExecutionMetadataKeys.ExecutionId] = created.ExecutionId,
                                    [AiPipelineMetadataKeys.Name] = request.PipelineName,
                                    [AiExecutionMetadataKeys.ExecutionStatus] = final.Status.ToString()
                                },
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    queuedRun.CompletionSource.TrySetResult(final);

                    _logger.Engine.LogInformation(
                        $"[AI PIPELINE CONTROLLER] Run processing attempt returned. RunId='{handle.RunId}', ExecutionId='{created.ExecutionId}', Status='{final.Status}', ControllerStatus='{handle.Status}'.");
                }
                catch (Exception ex)
                {
                    if (queuedRun.IsResume)
                    {
                        await RecordRecoveryForensicsEventAsync(
                                queuedRun,
                                AiEngineEvents.Recovery.ExecutionRecoveryFailed,
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
                    if (assistanceCandidateRegistered)
                    {
                        diagnosticPhase = "mark-execution-assistance-candidate-completed";

                        await MarkExecutionAssistanceCandidateCompletedAsync(
                                created.ExecutionId,
                                final?.Status.ToString() ?? "unknown",
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
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
        /// Resolves and validates controlled recovery metadata for an existing execution resume.
        /// </summary>
        /// <param name="queuedRun">The queued recovery run.</param>
        /// <returns>The validated recovery resume context.</returns>
        private static RecoveryResumeContext ResolveRecoveryResume(
            AiRuntimeQueuedPipelineRun queuedRun)
        {
            ArgumentNullException.ThrowIfNull(queuedRun);

            if (!queuedRun.IsResume ||
                string.IsNullOrWhiteSpace(queuedRun.ResumeExecutionId))
            {
                throw new InvalidOperationException(
                    "A recovery resume requires an existing execution identifier.");
            }

            return ResolveRecoveryResume(
                queuedRun.Request,
                queuedRun.ResumeExecutionId);
        }

        /// <summary>
        /// Resolves and validates controlled recovery metadata before one local runtime run is accepted.
        /// </summary>
        /// <param name="request">The runtime pipeline request.</param>
        /// <param name="resumeExecutionId">The durable execution identifier to resume.</param>
        /// <returns>The validated recovery resume context.</returns>
        private static RecoveryResumeContext ResolveRecoveryResume(
            AiRuntimePipelineRunRequest request,
            string resumeExecutionId)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(resumeExecutionId);

            var metadata =
                request.Metadata;

            if (metadata is null ||
                metadata.Count == 0)
            {
                throw new InvalidOperationException(
                    "Recovery resume metadata is required.");
            }

            if (!TryGetRecoveryMetadataValue(
                    metadata,
                    AiRuntimeRecoveryMetadataKeys.Mode,
                    out var recoveryMode) ||
                !string.Equals(
                    recoveryMode,
                    AiRuntimeRecoveryModes.ResumeExistingExecution,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Recovery metadata must declare mode '{AiRuntimeRecoveryModes.ResumeExistingExecution}'.");
            }

            if (!TryGetRecoveryMetadataValue(
                    metadata,
                    AiRuntimeRecoveryMetadataKeys.FailedExecutionId,
                    out var metadataExecutionId) ||
                string.IsNullOrWhiteSpace(metadataExecutionId))
            {
                throw new InvalidOperationException(
                    $"Recovery metadata '{AiRuntimeRecoveryMetadataKeys.FailedExecutionId}' is required.");
            }

            if (!string.Equals(
                    resumeExecutionId,
                    metadataExecutionId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Recovery execution id mismatch. QueuedExecutionId='{resumeExecutionId}', MetadataExecutionId='{metadataExecutionId}'.");
            }

            if (!TryGetRecoveryMetadataValue(
                    metadata,
                    AiRuntimeRecoveryMetadataKeys.ForensicsId,
                    out var recoveryOwnerId) ||
                string.IsNullOrWhiteSpace(recoveryOwnerId))
            {
                throw new InvalidOperationException(
                    $"Recovery metadata '{AiRuntimeRecoveryMetadataKeys.ForensicsId}' is required.");
            }

            var expectedOwnerPrefix =
                $"runtime-recovery:{metadataExecutionId}:";

            if (!recoveryOwnerId.StartsWith(
                    expectedOwnerPrefix,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Recovery owner id '{recoveryOwnerId}' does not belong to execution '{metadataExecutionId}'.");
            }

            return new RecoveryResumeContext(
                metadataExecutionId,
                recoveryOwnerId);
        }

        /// <summary>
        /// Determines whether execution control reached the expected recovery-owned state.
        /// </summary>
        /// <param name="state">The resulting execution control state.</param>
        /// <param name="recoveryOwnerId">The expected recovery owner identifier.</param>
        /// <param name="expectedStatus">The expected control status.</param>
        /// <param name="expectedPendingAction">The expected pending action.</param>
        /// <returns><c>true</c> when ownership and state match; otherwise, <c>false</c>.</returns>
        private static bool IsRecoveryResumeStateOwnedBy(
            AiExecutionControlState state,
            string recoveryOwnerId,
            AiExecutionControlStatus expectedStatus,
            AiExecutionControlAction expectedPendingAction)
        {
            ArgumentNullException.ThrowIfNull(state);

            return string.Equals(
                       state.RequestedBy,
                       recoveryOwnerId,
                       StringComparison.Ordinal) &&
                   state.Status == expectedStatus &&
                   state.PendingAction == expectedPendingAction;
        }

        /// <summary>
        /// Resolves one recovery metadata value using case-insensitive key matching.
        /// </summary>
        /// <param name="metadata">The recovery metadata.</param>
        /// <param name="key">The metadata key.</param>
        /// <param name="value">The resolved value.</param>
        /// <returns><c>true</c> when the key exists; otherwise, <c>false</c>.</returns>
        private static bool TryGetRecoveryMetadataValue(
            IReadOnlyDictionary<string, string> metadata,
            string key,
            out string? value)
        {
            foreach (var pair in metadata)
            {
                if (string.Equals(
                        pair.Key,
                        key,
                        StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }

            value = null;
            return false;
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

            await _observer
                .RecordAsync(
                    AiRecoveryEngineEventFactory.Create(
                        semanticEventType: eventType,
                        eventId: string.Join(
                            ":",
                            forensicsId,
                            eventType,
                            _runtimeInstanceIdentity.RuntimeInstanceId,
                            queuedRun.Handle.RunId),
                        forensicsId: forensicsId,
                        timestampUtc: DateTimeOffset.UtcNow,
                        outcome: outcome,
                        reason: reason,
                        executionId: executionId,
                        sharedRunId: sharedRunId,
                        localRunId: queuedRun.Handle.RunId,
                        runtimeInstanceId: _runtimeInstanceIdentity.RuntimeInstanceId,
                        metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            [AiPipelineMetadataKeys.Name] = queuedRun.Request.PipelineName,
                            [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = queuedRun.Request.ExecutionContextSnapshot?.TenantId ?? string.Empty,
                            [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = queuedRun.Request.ExecutionContextSnapshot?.TenantGroupId ?? string.Empty,
                            [AiRuntimeRecoveryMetadataKeys.ReplacementRuntimeInstanceId] = _runtimeInstanceIdentity.RuntimeInstanceId,
                            [AiRuntimeRecoveryMetadataKeys.ReplacementLocalRunId] = queuedRun.Handle.RunId,
                            [AiRuntimeRecoveryMetadataKeys.ReplacementExecutionId] = executionId,
                            [AiRuntimeRecoveryMetadataKeys.TransitionFailedRuntimeInstanceId] = failedRuntimeInstanceId,
                            [AiRuntimeRecoveryMetadataKeys.TransitionFailedLocalRunId] = failedLocalRunId,
                            [AiRuntimeRecoveryMetadataKeys.ResumeContextKey] = queuedRun.Request.ExecutionContextSnapshot?.ContextKey ?? string.Empty,
                            [AiRuntimeRecoveryMetadataKeys.Resume] = "true"
                        }),
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
                ResolveMetadataValue(metadata, AiRunMetadataKeys.SharedRunId);

            failedRuntimeInstanceId =
                ResolveMetadataValue(metadata, AiRuntimeRecoveryMetadataKeys.FailedRuntimeInstanceId);

            failedLocalRunId =
                ResolveMetadataValue(metadata, AiRuntimeRecoveryMetadataKeys.FailedLocalRunId);

            if (TryGetMetadataValue(
                    metadata,
                    AiRuntimeRecoveryMetadataKeys.ForensicsId,
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
                ExecutionContextSnapshotMapper.ToExecutionContext(
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

            await RecordRecoveryForensicsEventAsync(
                    queuedRun,
                    AiEngineEvents.Recovery.ResumeContextSeeded,
                    "seeded",
                    "resume-context-seeded-on-replacement-runtime",
                    queuedRun.ResumeExecutionId!,
                    cancellationToken)
                .ConfigureAwait(false);

            _logger.Engine.LogInformation(
                $"[AI PIPELINE CONTROLLER] Resume execution context seeded. RunId='{queuedRun.Handle.RunId}', ExecutionId='{queuedRun.ResumeExecutionId}', Pipeline='{queuedRun.Request.PipelineName}', RuntimeInstanceId='{_runtimeInstanceIdentity.RuntimeInstanceId}', TenantId='{context.TenantId}', ContextKey='{context.ContextKey}'.");
        }

        /// <summary>
        /// Seeds the restored RBAC execution context before a normal external-wait continuation re-drives an existing execution.
        /// </summary>
        /// <param name="queuedRun">The queued runtime pipeline run.</param>
        /// <param name="continuation">The validated normal continuation identity.</param>
        /// <param name="context">The restored RBAC execution context.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task SeedExternalWaitExecutionContextAsync(
            AiRuntimeQueuedPipelineRun queuedRun,
            AiRuntimeExternalWaitContinuation continuation,
            ExecutionContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(queuedRun);
            ArgumentNullException.ThrowIfNull(continuation);
            ArgumentNullException.ThrowIfNull(context);

            cancellationToken.ThrowIfCancellationRequested();

            if (!queuedRun.IsExternalWaitContinuation)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(context.ContextKey))
            {
                throw new InvalidOperationException(
                    $"Cannot continue external-wait execution '{continuation.ExecutionId}' for runtime run '{queuedRun.Handle.RunId}' because the restored execution context has no ContextKey.");
            }

            await _engine
                .SeedRestoredExecutionContextAsync(
                    continuation.ExecutionId,
                    context,
                    cancellationToken)
                .ConfigureAwait(false);

            _logger.Engine.LogInformation(
                $"[AI PIPELINE CONTROLLER] External-wait execution context seeded. RunId='{queuedRun.Handle.RunId}', ExecutionId='{continuation.ExecutionId}', Step='{continuation.StepName}', ContinuationId='{continuation.ContinuationId}', RuntimeInstanceId='{_runtimeInstanceIdentity.RuntimeInstanceId}', TenantId='{context.TenantId}', ContextKey='{context.ContextKey}'.");
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
                            [AiRunMetadataKeys.RunId] = runId,
                            [AiExecutionMetadataKeys.ExecutionId] = created.ExecutionId,
                            [AiPipelineMetadataKeys.Name] = request.PipelineName,
                            [AiRuntimeInstanceMetadataKeys.RuntimeInstanceId] = _runtimeInstanceIdentity.RuntimeInstanceId,
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
        /// <returns>The terminal or durably waiting execution record.</returns>
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
        /// <param name="definition">The declarative pipeline definition already resolved for this run request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The created execution record.</returns>
        private async Task<AiExecutionRecord> CreateExecutionAsync(
            AiRuntimePipelineRunRequest request,
            AiPipelineDefinition definition,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.PipelineName);

            if (!string.Equals(request.PipelineName, definition.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Resolved pipeline definition '{definition.Name}' does not match requested pipeline '{request.PipelineName}'.");
            }

            var hasRequestedExecutionId = !string.IsNullOrWhiteSpace(request.RequestedExecutionId);
            if (hasRequestedExecutionId && request.PipelineDefinitionSnapshot is null)
            {
                throw new InvalidOperationException(
                    "Preallocated execution creation requires an immutable pipeline definition snapshot.");
            }

            if (!hasRequestedExecutionId && request.PipelineDefinitionSnapshot is not null)
            {
                throw new InvalidOperationException(
                    "An immutable pipeline definition snapshot may only be used with a preallocated execution identifier.");
            }

            if (request.Input is null)
            {
                return await CreateDagExecutionAsync(
                    request,
                    definition,
                    new Dictionary<string, object?>(),
                    cancellationToken).ConfigureAwait(false);
            }

            if (request.Input is string textInput)
            {
                return await CreateDagExecutionAsync(
                    request,
                    definition,
                    textInput,
                    cancellationToken).ConfigureAwait(false);
            }

            if (request.Input is IDictionary<string, object?> stateInput)
            {
                return await CreateDagExecutionAsync(
                    request,
                    definition,
                    stateInput,
                    cancellationToken).ConfigureAwait(false);
            }

            if (request.Input is IReadOnlyDictionary<string, object?> readonlyStateInput)
            {
                return await CreateDagExecutionAsync(
                    request,
                    definition,
                    new Dictionary<string, object?>(readonlyStateInput, StringComparer.Ordinal),
                    cancellationToken).ConfigureAwait(false);
            }

            return await CreateDagExecutionAsync(
                request,
                definition,
                ConvertObjectToStateInput(request.Input),
                cancellationToken).ConfigureAwait(false);
        }


        /// <summary>
        /// Creates a string-input DAG execution using either historical new-id creation or exact create-if-absent.
        /// </summary>
        /// <param name="request">The runtime run request.</param>
        /// <param name="definition">The exact declarative definition.</param>
        /// <param name="input">The string input.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The created or existing execution record.</returns>
        private Task<AiExecutionRecord> CreateDagExecutionAsync(
            AiRuntimePipelineRunRequest request,
            AiPipelineDefinition definition,
            string input,
            CancellationToken cancellationToken)
        {
            return string.IsNullOrWhiteSpace(request.RequestedExecutionId)
                ? _engine.CreateAsync(request.PipelineName, input, cancellationToken)
                : _engine.CreateIfAbsentAsync(
                    request.RequestedExecutionId,
                    definition,
                    request.PipelineDefinitionSnapshot!,
                    input,
                    cancellationToken);
        }

        /// <summary>
        /// Creates a structured-input DAG execution using either historical new-id creation or exact create-if-absent.
        /// </summary>
        /// <param name="request">The runtime run request.</param>
        /// <param name="definition">The exact declarative definition.</param>
        /// <param name="input">The structured input.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The created or existing execution record.</returns>
        private Task<AiExecutionRecord> CreateDagExecutionAsync(
            AiRuntimePipelineRunRequest request,
            AiPipelineDefinition definition,
            IDictionary<string, object?> input,
            CancellationToken cancellationToken)
        {
            return string.IsNullOrWhiteSpace(request.RequestedExecutionId)
                ? _engine.CreateAsync(request.PipelineName, input, cancellationToken)
                : _engine.CreateIfAbsentAsync(
                    request.RequestedExecutionId,
                    definition,
                    request.PipelineDefinitionSnapshot!,
                    input,
                    cancellationToken);
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
                ? AiEngineEvents.Run.Completed
                : final.Status == AiExecutionStatus.Cancelled
                    ? AiEngineEvents.Run.Cancelled
                    : AiEngineEvents.Run.Failed;

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
                        [AiRunMetadataKeys.RunId] = runId,
                        [AiExecutionMetadataKeys.ExecutionId] = executionId,
                        [AiPipelineMetadataKeys.Name] = pipelineName,
                        [AiExecutionMetadataKeys.ExecutionStatus] = final.Status.ToString(),
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
                    AiEngineEvents.Run.Cancelled,
                    AiDecisionLedgerOutcome.Applied,
                    executionId,
                    reason ?? "Running pipeline run cancellation delegated to execution control.",
                    new Dictionary<string, string>
                    {
                        [AiRunMetadataKeys.RunId] = runId,
                        [AiExecutionMetadataKeys.ExecutionId] = executionId,
                        [AiPipelineMetadataKeys.Name] = runningRun.Request.PipelineName,
                        [AiExecutionControlMetadataKeys.RequestedBy] = requestedBy ?? string.Empty,
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
                AiRuntimeWorkerRunStatus.Paused => "waiting",
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
                [AiRunMetadataKeys.RunId] = queuedRun.Handle.RunId,
                [AiExecutionMetadataKeys.ExecutionId] = executionId ?? queuedRun.Handle.ExecutionId ?? string.Empty,
                [AiPipelineMetadataKeys.Name] = queuedRun.Request.PipelineName,
                [AiRuntimeInstanceMetadataKeys.Status] = queuedRun.Handle.Status.ToString(),
                ["failure.phase"] = phase,
                ["input.type"] = ResolveInputTypeName(queuedRun.Request.Input),
                [AiExceptionMetadataKeys.ExceptionType] = exception.GetType().FullName ?? exception.GetType().Name,
                [AiExceptionMetadataKeys.ExceptionMessage] = Truncate(exception.Message, 2000),
                [AiExceptionMetadataKeys.ExceptionStackTrace] = Truncate(exception.StackTrace, 6000),
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
        /// Represents validated metadata for a recovery-owned existing execution resume.
        /// </summary>
        /// <param name="ExecutionId">The durable execution identifier.</param>
        /// <param name="RecoveryOwnerId">The deterministic recovery owner identifier.</param>
        private sealed record RecoveryResumeContext(
            string ExecutionId,
            string RecoveryOwnerId);


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