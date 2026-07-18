using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue
{
    /// <summary>
    /// Runtime implementation of the local runtime queue control-plane facade.
    /// </summary>
    /// <remarks>
    /// This class wraps the existing local runtime pipeline background controller.
    ///
    /// IMPORTANT:
    /// - This class does not replace local queues, workers, runtime instances,
    ///   or DAG execution logic.
    /// - It only exposes adapter-neutral control-plane operations over the local
    ///   runtime queue owned by one runtime instance.
    /// - It also maintains a local runtime run execution index so a local RunId
    ///   can remain observable even after the transient queue item has been consumed.
    /// </remarks>
    public sealed class AiRuntimeQueueControlPlane : IAiRuntimeQueueControlPlane
    {
        private const string RecoveryForensicsIdMetadataKey = "recovery.forensicsId";
        private const string RecoveryModeMetadataKey = "recovery.mode";
        private const string RecoveryModeResumeExistingExecution = "resume-existing-execution";
        private const string RecoveryModeRequeueLocalQueuedRun = "requeue-local-queued-run";
        private const string RecoveryFailedExecutionIdMetadataKey = "recovery.failedExecutionId";

        private readonly IAiRuntimePipelineBackgroundController _controller;
        private readonly IAiRuntimeRunExecutionIndex _runExecutionIndex;
        private readonly AiRuntimeQueueControlPlaneOptions _options;
        private readonly IAiControlPlaneObserver _observer;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeQueueControlPlane"/> class.
        /// </summary>
        /// <param name="controller">The local runtime pipeline background controller.</param>
        /// <param name="runExecutionIndex">
        /// The local runtime run execution index used to keep the relationship between
        /// a local runtime queue RunId and its DAG ExecutionId.
        /// </param>
        /// <param name="options">The runtime queue control-plane options.</param>
        /// <param name="observer">The control-plane observer.</param>
        public AiRuntimeQueueControlPlane(
            IAiRuntimePipelineBackgroundController controller,
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            IOptions<AiRuntimeQueueControlPlaneOptions> options,
            IAiControlPlaneObserver observer)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _runExecutionIndex = runExecutionIndex ?? throw new ArgumentNullException(nameof(runExecutionIndex));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        }

        /// <inheritdoc />
        public Task<AiRuntimeQueueControlPlaneResult> ExecuteAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            return request.Operation switch
            {
                AiRuntimeQueueControlPlaneOperation.EnqueueRun => EnqueueRunAsync(request, cancellationToken),
                AiRuntimeQueueControlPlaneOperation.CancelRun => CancelRunAsync(request, cancellationToken),
                AiRuntimeQueueControlPlaneOperation.CancelQueuedRun => CancelQueuedRunAsync(request, cancellationToken),
                AiRuntimeQueueControlPlaneOperation.PauseQueue => PauseQueueAsync(request, cancellationToken),
                AiRuntimeQueueControlPlaneOperation.ResumeQueue => ResumeQueueAsync(request, cancellationToken),
                AiRuntimeQueueControlPlaneOperation.GetRunStatus => GetRunStatusAsync(request, cancellationToken),
                AiRuntimeQueueControlPlaneOperation.GetQueueStatus => GetQueueStatusAsync(request, cancellationToken),

                _ => throw new NotSupportedException(
                    $"Runtime queue control-plane operation '{request.Operation}' is not supported.")
            };
        }

        /// <inheritdoc />
        public Task<AiRuntimeQueueControlPlaneResult> EnqueueRunAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteQueueOperationAsync(
                request,
                AiRuntimeQueueControlPlaneOperation.EnqueueRun,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiRuntimeQueueControlPlaneResult> CancelRunAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteQueueOperationAsync(
                request,
                AiRuntimeQueueControlPlaneOperation.CancelRun,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiRuntimeQueueControlPlaneResult> CancelQueuedRunAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteQueueOperationAsync(
                request,
                AiRuntimeQueueControlPlaneOperation.CancelQueuedRun,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiRuntimeQueueControlPlaneResult> PauseQueueAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteQueueOperationAsync(
                request,
                AiRuntimeQueueControlPlaneOperation.PauseQueue,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiRuntimeQueueControlPlaneResult> ResumeQueueAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteQueueOperationAsync(
                request,
                AiRuntimeQueueControlPlaneOperation.ResumeQueue,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiRuntimeQueueControlPlaneResult> GetRunStatusAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteQueueOperationAsync(
                request,
                AiRuntimeQueueControlPlaneOperation.GetRunStatus,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiRuntimeQueueControlPlaneResult> GetQueueStatusAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteQueueOperationAsync(
                request,
                AiRuntimeQueueControlPlaneOperation.GetQueueStatus,
                cancellationToken);
        }

        /// <summary>
        /// Executes a runtime queue control-plane operation with validation,
        /// observability, failure handling, and result normalization.
        /// </summary>
        /// <param name="request">The control-plane request.</param>
        /// <param name="operation">The expected operation.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The normalized control-plane result.</returns>
        private async Task<AiRuntimeQueueControlPlaneResult> ExecuteQueueOperationAsync(
            AiRuntimeQueueControlPlaneRequest request,
            AiRuntimeQueueControlPlaneOperation operation,
            CancellationToken cancellationToken)
        {
            var startedAtUtc = DateTimeOffset.UtcNow;
            var correlation = CreateCorrelation(request);

            try
            {
                ValidateRequest(request, operation);
                EnsureEnabled(operation);

                await RecordStartedAsync(
                    request,
                    operation,
                    correlation,
                    cancellationToken).ConfigureAwait(false);

                var operationResult = await ExecuteInnerAsync(
                    request,
                    operation,
                    cancellationToken).ConfigureAwait(false);

                var completedAtUtc = DateTimeOffset.UtcNow;
                var durationMs = CalculateDurationMs(startedAtUtc, completedAtUtc);

                var enqueueAccepted =
                    operation == AiRuntimeQueueControlPlaneOperation.EnqueueRun &&
                    operationResult.RunHandle is not null;

                if (enqueueAccepted)
                {
                    try
                    {
                        await RecordCompletedAsync(
                            request,
                            operation,
                            correlation,
                            operationResult,
                            durationMs,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        Console.WriteLine(
                            $"[RUNTIME QUEUE ENQUEUE] ACCEPTED COMPLETION OBSERVABILITY FAILED RunId='{operationResult.RunHandle?.RunId}', ExecutionId='{operationResult.RunHandle?.ExecutionId}', ExceptionType='{exception.GetType().FullName}', Message='{exception.Message}'.");
                    }
                }
                else
                {
                    await RecordCompletedAsync(
                        request,
                        operation,
                        correlation,
                        operationResult,
                        durationMs,
                        cancellationToken).ConfigureAwait(false);
                }

                return new AiRuntimeQueueControlPlaneResult
                {
                    Operation = operation,
                    Success = true,
                    Message = $"Runtime queue control-plane operation '{operation}' completed successfully.",
                    RunId = operationResult.RunHandle?.RunId ?? operationResult.RunState?.RunId ?? request.RunId,
                    ExecutionId = operationResult.RunHandle?.ExecutionId ?? operationResult.RunState?.ExecutionId,
                    RunHandle = operationResult.RunHandle,
                    RunState = request.IncludeRunState ? operationResult.RunState : null,
                    QueueState = request.IncludeQueueState ? operationResult.QueueState : null,
                    CorrelationId = correlation.CorrelationId,
                    RuntimeInstanceId =
                        operationResult.QueueState?.RuntimeInstanceId ??
                        operationResult.RunState?.RuntimeInstanceId ??
                        request.RuntimeInstanceId,
                    RequestedBy = request.RequestedBy,
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = completedAtUtc,
                    DurationMs = durationMs
                };
            }
            catch (Exception exception) when (_options.ReturnFailureResultInsteadOfThrowing)
            {
                var completedAtUtc = DateTimeOffset.UtcNow;
                var durationMs = CalculateDurationMs(startedAtUtc, completedAtUtc);

                await RecordFailedAsync(
                    request,
                    operation,
                    correlation,
                    exception,
                    durationMs,
                    cancellationToken).ConfigureAwait(false);

                return new AiRuntimeQueueControlPlaneResult
                {
                    Operation = operation,
                    Success = false,
                    Message = $"Runtime queue control-plane operation '{operation}' failed.",
                    RunId = request?.RunId,
                    Diagnostics = request?.IncludeDiagnostics == true
                        ? new[] { exception.Message }
                        : Array.Empty<string>(),
                    CorrelationId = correlation.CorrelationId,
                    RuntimeInstanceId = request?.RuntimeInstanceId,
                    RequestedBy = request?.RequestedBy,
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = completedAtUtc,
                    DurationMs = durationMs,
                    FailureReason = exception.Message
                };
            }
        }

        /// <summary>
        /// Executes the inner runtime queue operation.
        /// </summary>
        /// <param name="request">The control-plane request.</param>
        /// <param name="operation">The operation to execute.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The raw runtime queue operation result.</returns>
        private async Task<RuntimeQueueOperationResult> ExecuteInnerAsync(
            AiRuntimeQueueControlPlaneRequest request,
            AiRuntimeQueueControlPlaneOperation operation,
            CancellationToken cancellationToken)
        {
            return operation switch
            {
                AiRuntimeQueueControlPlaneOperation.EnqueueRun =>
                    await EnqueueInnerAsync(request, cancellationToken).ConfigureAwait(false),

                AiRuntimeQueueControlPlaneOperation.CancelRun =>
                    await CancelRunInnerAsync(request, cancellationToken).ConfigureAwait(false),

                AiRuntimeQueueControlPlaneOperation.CancelQueuedRun =>
                    await CancelQueuedRunInnerAsync(request, cancellationToken).ConfigureAwait(false),

                AiRuntimeQueueControlPlaneOperation.PauseQueue =>
                    await PauseQueueInnerAsync(request, cancellationToken).ConfigureAwait(false),

                AiRuntimeQueueControlPlaneOperation.ResumeQueue =>
                    await ResumeQueueInnerAsync(request, cancellationToken).ConfigureAwait(false),

                AiRuntimeQueueControlPlaneOperation.GetRunStatus =>
                    await GetRunStatusInnerAsync(request, cancellationToken).ConfigureAwait(false),

                AiRuntimeQueueControlPlaneOperation.GetQueueStatus =>
                    await GetQueueStatusInnerAsync(cancellationToken).ConfigureAwait(false),

                _ => throw new NotSupportedException(
                    $"Runtime queue control-plane operation '{operation}' is not supported.")
            };
        }

        /// <summary>
        /// Enqueues a run into the local runtime queue and records it in the local
        /// runtime run execution index.
        /// </summary>
        /// <param name="request">The enqueue request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The enqueue operation result.</returns>
        /// <remarks>
        /// The local runtime queue can be transient. Once the background controller
        /// consumes a queued run, the queue itself may no longer expose the run state.
        /// The runtime run execution index keeps a stable RunId entry so the control
        /// plane can still resolve the run later.
        /// </remarks>
        private async Task<RuntimeQueueOperationResult> EnqueueInnerAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken)
        {
            var recoveryResume =
                ResolveRecoveryResume(
                    request.Metadata);

            var resumeExecutionId =
                recoveryResume?.ExecutionId;

            Console.WriteLine(
                $"[RUNTIME QUEUE RECOVERY RESUME] ResumeExecutionId='{resumeExecutionId ?? string.Empty}', RecoveryOwnerId='{recoveryResume?.RecoveryOwnerId ?? string.Empty}', HasMetadata='{request.Metadata is not null}', Metadata='{string.Join(";", request.Metadata?.Select(pair => $"{pair.Key}={pair.Value}") ?? Array.Empty<string>())}'");

            var enrichedRunRequest =
                CreateMetadataEnrichedRunRequest(
                    request.RunRequest!,
                    request.Metadata);

            Console.WriteLine(
                $"[RUNTIME QUEUE ENQUEUE] BEFORE CONTROLLER ENQUEUE Pipeline='{request.RunRequest?.PipelineName}', RuntimeInstanceId='{request.RuntimeInstanceId}'.");

            var handle = string.IsNullOrWhiteSpace(resumeExecutionId)
                ? await _controller
                    .EnqueueAsync(
                        enrichedRunRequest,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await _controller
                    .EnqueueResumeAsync(
                        enrichedRunRequest,
                        resumeExecutionId,
                        cancellationToken)
                    .ConfigureAwait(false);

            /*
             * A returned controller handle means the run was accepted into the
             * controller Channel. All remaining reads, index enrichment, and
             * observability are post-acceptance work and must not be controlled by
             * the HTTP/gRPC request cancellation token.
             */
            Console.WriteLine(
                $"[RUNTIME QUEUE ENQUEUE] ACCEPTED RunId='{handle.RunId}', ExecutionId='{handle.ExecutionId}', Resume='{!string.IsNullOrWhiteSpace(resumeExecutionId)}'.");

            AiRuntimePipelineRunState? runState =
                null;

            try
            {
                runState =
                    await _controller
                        .GetRunStateAsync(
                            handle.RunId,
                            CancellationToken.None)
                        .ConfigureAwait(false);

                Console.WriteLine(
                    $"[RUNTIME QUEUE ENQUEUE] POST-ACCEPT RUN STATE RunId='{handle.RunId}', StateFound='{runState is not null}', StateExecutionId='{runState?.ExecutionId}', Status='{runState?.Status}'.");
            }
            catch (Exception exception)
            {
                Console.WriteLine(
                    $"[RUNTIME QUEUE ENQUEUE] POST-ACCEPT RUN STATE FAILED RunId='{handle.RunId}', ExecutionId='{handle.ExecutionId}', ExceptionType='{exception.GetType().FullName}', Message='{exception.Message}'.");
            }

            AiRuntimePipelineQueueState? queueState =
                null;

            try
            {
                queueState =
                    await _controller
                        .GetQueueStateAsync(
                            CancellationToken.None)
                        .ConfigureAwait(false);

                Console.WriteLine(
                    $"[RUNTIME QUEUE ENQUEUE] POST-ACCEPT QUEUE STATE RuntimeInstanceId='{queueState?.RuntimeInstanceId}', Queued='{queueState?.QueuedRunCount}', Running='{queueState?.RunningRunCount}'.");
            }
            catch (Exception exception)
            {
                Console.WriteLine(
                    $"[RUNTIME QUEUE ENQUEUE] POST-ACCEPT QUEUE STATE FAILED RunId='{handle.RunId}', ExecutionId='{handle.ExecutionId}', ExceptionType='{exception.GetType().FullName}', Message='{exception.Message}'.");
            }

            var executionContextSnapshot =
                enrichedRunRequest.ExecutionContextSnapshot;

            if (string.IsNullOrWhiteSpace(resumeExecutionId))
            {
                try
                {
                    await _runExecutionIndex.RegisterQueuedAsync(
                            new AiRuntimeRunExecutionIndexEntry
                            {
                                RunId = handle.RunId,
                                ExecutionId = handle.ExecutionId ?? runState?.ExecutionId,
                                RuntimeInstanceId =
                                    runState?.RuntimeInstanceId ??
                                    queueState?.RuntimeInstanceId ??
                                    request.RuntimeInstanceId,
                                Status = runState?.Status ?? "queued",
                                CreatedAtUtc = DateTimeOffset.UtcNow,
                                ExecutionContextSnapshot = executionContextSnapshot,
                                Metadata = MergeMetadata(
                                    enrichedRunRequest.Metadata,
                                    new Dictionary<string, string>
                                    {
                                        ["source"] = request.Source ?? string.Empty,
                                        ["requestedBy"] = request.RequestedBy ?? string.Empty,
                                        ["reason"] = request.Reason ?? string.Empty,
                                        ["correlationId"] = request.CorrelationId ?? string.Empty,
                                        ["recovery.resume"] = "false",
                                        ["recovery.execution.id"] = string.Empty,
                                        [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = executionContextSnapshot?.TenantId ?? string.Empty,
                                        [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = executionContextSnapshot?.TenantGroupId ?? string.Empty
                                    })
                            },
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    Console.WriteLine(
                        $"[RUNTIME QUEUE ENQUEUE] POST-ACCEPT INDEX REGISTERED RunId='{handle.RunId}', ExecutionId='{handle.ExecutionId}'.");
                }
                catch (Exception exception)
                {
                    Console.WriteLine(
                        $"[RUNTIME QUEUE ENQUEUE] POST-ACCEPT INDEX REGISTRATION FAILED RunId='{handle.RunId}', ExecutionId='{handle.ExecutionId}', ExceptionType='{exception.GetType().FullName}', Message='{exception.Message}'.");
                }
            }
            else
            {
                /*
                 * Resume index ownership belongs to
                 * AiRuntimePipelineBackgroundController.EnqueueResumeAsync.
                 * Re-registering here can race with MarkStartedAsync and regress a
                 * started/running entry back to queued.
                 */
                Console.WriteLine(
                    $"[RUNTIME QUEUE ENQUEUE] RESUME INDEX REGISTRATION SKIPPED RunId='{handle.RunId}', ExecutionId='{resumeExecutionId}', Owner='AiRuntimePipelineBackgroundController'.");
            }

            return new RuntimeQueueOperationResult
            {
                RunHandle = handle,
                RunState = runState,
                QueueState = queueState
            };
        }

        /// <summary>
        /// Cancels a runtime run and updates the runtime run execution index when a run state is available.
        /// </summary>
        /// <param name="request">The cancel request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The cancel operation result.</returns>
        private async Task<RuntimeQueueOperationResult> CancelRunInnerAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken)
        {
            var indexedRun = await _runExecutionIndex
                .GetAsync(
                    request.RunId!,
                    cancellationToken)
                .ConfigureAwait(false);

            if (indexedRun is null)
            {
                return new RuntimeQueueOperationResult();
            }

            await _controller
                .CancelRunAsync(
                    request.RunId!,
                    request.Reason,
                    request.RequestedBy,
                    cancellationToken)
                .ConfigureAwait(false);

            var runState = await _controller
                .GetRunStateAsync(request.RunId!, cancellationToken)
                .ConfigureAwait(false);

            await _runExecutionIndex.MarkCancelledAsync(
                    request.RunId!,
                    runState?.ExecutionId ?? indexedRun.ExecutionId,
                    request.Reason ?? "Runtime run was cancelled.",
                    cancellationToken)
                .ConfigureAwait(false);

            var cancelledIndexedRun = await _runExecutionIndex
                .GetAsync(
                    request.RunId!,
                    cancellationToken)
                .ConfigureAwait(false);

            var queueState = await _controller
                .GetQueueStateAsync(cancellationToken)
                .ConfigureAwait(false);

            return new RuntimeQueueOperationResult
            {
                RunState = cancelledIndexedRun is not null
                    ? CreateRunStateFromIndex(cancelledIndexedRun)
                    : CreateRunStateFromIndex(indexedRun),
                QueueState = queueState
            };
        }

        /// <summary>
        /// Cancels a queued runtime run and updates the runtime run execution index when a run state is available.
        /// </summary>
        /// <param name="request">The cancel queued run request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The cancel queued operation result.</returns>
        private async Task<RuntimeQueueOperationResult> CancelQueuedRunInnerAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken)
        {
            var indexedRun = await _runExecutionIndex
                .GetAsync(
                    request.RunId!,
                    cancellationToken)
                .ConfigureAwait(false);

            if (indexedRun is null)
            {
                return new RuntimeQueueOperationResult();
            }

            await _controller
                .CancelQueuedRunAsync(
                    request.RunId!,
                    request.Reason,
                    request.RequestedBy,
                    cancellationToken)
                .ConfigureAwait(false);

            var runState = await _controller
                .GetRunStateAsync(request.RunId!, cancellationToken)
                .ConfigureAwait(false);

            await _runExecutionIndex.MarkCancelledAsync(
                    request.RunId!,
                    runState?.ExecutionId ?? indexedRun.ExecutionId,
                    request.Reason ?? "Queued runtime run was cancelled.",
                    cancellationToken)
                .ConfigureAwait(false);

            var cancelledIndexedRun = await _runExecutionIndex
                .GetAsync(
                    request.RunId!,
                    cancellationToken)
                .ConfigureAwait(false);

            var queueState = await _controller
                .GetQueueStateAsync(cancellationToken)
                .ConfigureAwait(false);

            return new RuntimeQueueOperationResult
            {
                RunState = cancelledIndexedRun is not null
                    ? CreateRunStateFromIndex(cancelledIndexedRun)
                    : CreateRunStateFromIndex(indexedRun),
                QueueState = queueState
            };
        }

        /// <summary>
        /// Pauses the local runtime queue.
        /// </summary>
        /// <param name="request">The pause request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The pause operation result.</returns>
        private async Task<RuntimeQueueOperationResult> PauseQueueInnerAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken)
        {
            await _controller
                .PauseQueueAsync(
                    request.Reason,
                    request.RequestedBy,
                    cancellationToken)
                .ConfigureAwait(false);

            var queueState = await _controller
                .GetQueueStateAsync(cancellationToken)
                .ConfigureAwait(false);

            return new RuntimeQueueOperationResult
            {
                QueueState = queueState
            };
        }

        /// <summary>
        /// Resumes the local runtime queue.
        /// </summary>
        /// <param name="request">The resume request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resume operation result.</returns>
        private async Task<RuntimeQueueOperationResult> ResumeQueueInnerAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken)
        {
            await _controller
                .ResumeQueueAsync(
                    request.RequestedBy,
                    cancellationToken)
                .ConfigureAwait(false);

            var queueState = await _controller
                .GetQueueStateAsync(cancellationToken)
                .ConfigureAwait(false);

            return new RuntimeQueueOperationResult
            {
                QueueState = queueState
            };
        }

        /// <summary>
        /// Gets the status of a local runtime run.
        /// </summary>
        /// <param name="request">The get run status request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The runtime run status operation result.</returns>
        /// <remarks>
        /// The method first checks the runtime run execution index because the index owns
        /// tenant visibility for local RunId to ExecutionId mappings.
        /// This prevents a tenant from bypassing isolation by calling the local runtime
        /// controller directly with another tenant's RunId.
        /// </remarks>
        private async Task<RuntimeQueueOperationResult> GetRunStatusInnerAsync(
            AiRuntimeQueueControlPlaneRequest request,
            CancellationToken cancellationToken)
        {
            var indexedRun = await _runExecutionIndex
                .GetAsync(
                    request.RunId!,
                    cancellationToken)
                .ConfigureAwait(false);

            if (indexedRun is null)
            {
                return new RuntimeQueueOperationResult();
            }

            if (IsTerminalStatus(indexedRun.Status))
            {
                return new RuntimeQueueOperationResult
                {
                    RunState = CreateRunStateFromIndex(indexedRun)
                };
            }

            var runState = await _controller
                .GetRunStateAsync(request.RunId!, cancellationToken)
                .ConfigureAwait(false);

            if (runState is not null)
            {
                return new RuntimeQueueOperationResult
                {
                    RunState = runState
                };
            }

            return new RuntimeQueueOperationResult
            {
                RunState = CreateRunStateFromIndex(indexedRun)
            };
        }

        private static bool IsTerminalStatus(
            string? status)
        {
            return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets the local runtime queue status.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The runtime queue status operation result.</returns>
        private async Task<RuntimeQueueOperationResult> GetQueueStatusInnerAsync(
            CancellationToken cancellationToken)
        {
            var queueState = await _controller
                .GetQueueStateAsync(cancellationToken)
                .ConfigureAwait(false);

            return new RuntimeQueueOperationResult
            {
                QueueState = queueState
            };
        }

        /// <summary>
        /// Creates a runtime execution correlation context for the control-plane operation.
        /// </summary>
        /// <param name="request">The control-plane request.</param>
        /// <returns>The correlation context.</returns>
        private static AiRuntimeExecutionCorrelationContext CreateCorrelation(
            AiRuntimeQueueControlPlaneRequest request)
        {
            return new AiRuntimeExecutionCorrelationContext
            {
                CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId)
                    ? Guid.NewGuid().ToString("N")
                    : request.CorrelationId,

                RunId = request.RunId,
                RuntimeInstanceId = request.RuntimeInstanceId
            };
        }

        /// <summary>
        /// Records that a runtime queue control-plane operation started.
        /// </summary>
        /// <param name="request">The control-plane request.</param>
        /// <param name="operation">The operation.</param>
        /// <param name="correlation">The correlation context.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task RecordStartedAsync(
            AiRuntimeQueueControlPlaneRequest request,
            AiRuntimeQueueControlPlaneOperation operation,
            AiRuntimeExecutionCorrelationContext correlation,
            CancellationToken cancellationToken)
        {
            await _observer.RecordAsync(
                new AiControlPlaneEvent
                {
                    EventType = AiControlPlaneEventType.OperationStarted,
                    Area = AiControlPlaneArea.RunControl,
                    Operation = operation.ToString(),
                    Correlation = correlation,
                    Message = $"Runtime queue control-plane operation '{operation}' started.",
                    Properties = new Dictionary<string, object?>
                    {
                        ["source"] = request.Source,
                        ["requestedBy"] = request.RequestedBy,
                        ["reason"] = request.Reason,
                        ["runId"] = request.RunId,
                        ["hasRunRequest"] = request.RunRequest is not null,
                        ["includeRunState"] = request.IncludeRunState,
                        ["includeQueueState"] = request.IncludeQueueState,
                        ["includeDiagnostics"] = request.IncludeDiagnostics
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Records that a runtime queue control-plane operation completed.
        /// </summary>
        /// <param name="request">The control-plane request.</param>
        /// <param name="operation">The operation.</param>
        /// <param name="correlation">The correlation context.</param>
        /// <param name="operationResult">The raw operation result.</param>
        /// <param name="durationMs">The operation duration in milliseconds.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task RecordCompletedAsync(
            AiRuntimeQueueControlPlaneRequest request,
            AiRuntimeQueueControlPlaneOperation operation,
            AiRuntimeExecutionCorrelationContext correlation,
            RuntimeQueueOperationResult operationResult,
            long durationMs,
            CancellationToken cancellationToken)
        {
            await _observer.RecordAsync(
                new AiControlPlaneEvent
                {
                    EventType = AiControlPlaneEventType.OperationCompleted,
                    Area = AiControlPlaneArea.RunControl,
                    Operation = operation.ToString(),
                    Outcome = AiControlPlaneOperationOutcome.Succeeded,
                    Correlation = correlation,
                    DurationMs = durationMs,
                    Message = $"Runtime queue control-plane operation '{operation}' completed successfully.",
                    Properties = new Dictionary<string, object?>
                    {
                        ["source"] = request.Source,
                        ["requestedBy"] = request.RequestedBy,
                        ["runId"] = operationResult.RunHandle?.RunId ?? operationResult.RunState?.RunId ?? request.RunId,
                        ["executionId"] = operationResult.RunHandle?.ExecutionId ?? operationResult.RunState?.ExecutionId,
                        ["queuePaused"] = operationResult.QueueState?.IsPaused,
                        ["queuedRunCount"] = operationResult.QueueState?.QueuedRunCount,
                        ["runningRunCount"] = operationResult.QueueState?.RunningRunCount,
                        ["availableRunSlots"] = operationResult.QueueState?.AvailableRunSlots
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Records that a runtime queue control-plane operation failed.
        /// </summary>
        /// <param name="request">The control-plane request, when available.</param>
        /// <param name="operation">The operation.</param>
        /// <param name="correlation">The correlation context.</param>
        /// <param name="exception">The exception that caused the failure.</param>
        /// <param name="durationMs">The operation duration in milliseconds.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task RecordFailedAsync(
            AiRuntimeQueueControlPlaneRequest? request,
            AiRuntimeQueueControlPlaneOperation operation,
            AiRuntimeExecutionCorrelationContext correlation,
            Exception exception,
            long durationMs,
            CancellationToken cancellationToken)
        {
            await _observer.RecordAsync(
                new AiControlPlaneEvent
                {
                    EventType = AiControlPlaneEventType.OperationFailed,
                    Area = AiControlPlaneArea.RunControl,
                    Operation = operation.ToString(),
                    Outcome = AiControlPlaneOperationOutcome.Failed,
                    Correlation = correlation,
                    DurationMs = durationMs,
                    Message = $"Runtime queue control-plane operation '{operation}' failed.",
                    FailureReason = exception.Message,
                    Properties = new Dictionary<string, object?>
                    {
                        ["source"] = request?.Source,
                        ["requestedBy"] = request?.RequestedBy,
                        ["runId"] = request?.RunId,
                        ["exceptionType"] = exception.GetType().Name
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Validates the request for the requested runtime queue operation.
        /// </summary>
        /// <param name="request">The control-plane request.</param>
        /// <param name="operation">The requested operation.</param>
        private static void ValidateRequest(
            AiRuntimeQueueControlPlaneRequest request,
            AiRuntimeQueueControlPlaneOperation operation)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (operation == AiRuntimeQueueControlPlaneOperation.EnqueueRun &&
                request.RunRequest is null)
            {
                throw new ArgumentException(
                    "RunRequest is required for EnqueueRun operations.",
                    nameof(request));
            }

            if (RequiresRunId(operation) &&
                string.IsNullOrWhiteSpace(request.RunId))
            {
                throw new ArgumentException(
                    "RunId is required for this runtime queue control-plane operation.",
                    nameof(request));
            }
        }

        /// <summary>
        /// Determines whether the operation requires a runtime run identifier.
        /// </summary>
        /// <param name="operation">The operation.</param>
        /// <returns><see langword="true"/> if the operation requires a run id; otherwise, <see langword="false"/>.</returns>
        private static bool RequiresRunId(
            AiRuntimeQueueControlPlaneOperation operation)
        {
            return operation is
                AiRuntimeQueueControlPlaneOperation.CancelRun or
                AiRuntimeQueueControlPlaneOperation.CancelQueuedRun or
                AiRuntimeQueueControlPlaneOperation.GetRunStatus;
        }

        /// <summary>
        /// Ensures that the requested operation is enabled by options.
        /// </summary>
        /// <param name="operation">The operation.</param>
        private void EnsureEnabled(
            AiRuntimeQueueControlPlaneOperation operation)
        {
            var enabled = operation switch
            {
                AiRuntimeQueueControlPlaneOperation.EnqueueRun => _options.EnableEnqueueRun,
                AiRuntimeQueueControlPlaneOperation.CancelRun => _options.EnableCancelRun,
                AiRuntimeQueueControlPlaneOperation.CancelQueuedRun => _options.EnableCancelQueuedRun,
                AiRuntimeQueueControlPlaneOperation.PauseQueue => _options.EnablePauseQueue,
                AiRuntimeQueueControlPlaneOperation.ResumeQueue => _options.EnableResumeQueue,
                AiRuntimeQueueControlPlaneOperation.GetRunStatus => _options.EnableGetRunStatus,
                AiRuntimeQueueControlPlaneOperation.GetQueueStatus => _options.EnableGetQueueStatus,
                _ => false
            };

            if (!enabled)
            {
                throw new InvalidOperationException(
                    $"Runtime queue control-plane operation '{operation}' is disabled.");
            }
        }

        /// <summary>
        /// Calculates an operation duration in milliseconds.
        /// </summary>
        /// <param name="startedAtUtc">The operation start time.</param>
        /// <param name="completedAtUtc">The operation completion time.</param>
        /// <returns>The operation duration in milliseconds, or 0 when duration measurement is disabled.</returns>
        private long CalculateDurationMs(
            DateTimeOffset startedAtUtc,
            DateTimeOffset completedAtUtc)
        {
            if (!_options.MeasureDuration)
            {
                return 0;
            }

            return (long)(completedAtUtc - startedAtUtc).TotalMilliseconds;
        }

        /// <summary>
        /// Creates a pipeline run request enriched with runtime queue metadata.
        /// </summary>
        /// <param name="runRequest">The original pipeline run request.</param>
        /// <param name="metadata">The runtime queue metadata to propagate to the background controller.</param>
        /// <returns>A pipeline run request carrying merged metadata.</returns>
        /// <remarks>
        /// Recovery metadata originates at the control-plane queue boundary. The local
        /// background controller needs the same metadata later to record DAG resume
        /// forensics after the transient queue item has been consumed.
        /// </remarks>
        private static AiRuntimePipelineRunRequest CreateMetadataEnrichedRunRequest(
            AiRuntimePipelineRunRequest runRequest,
            IReadOnlyDictionary<string, string>? metadata)
        {
            ArgumentNullException.ThrowIfNull(runRequest);

            return new AiRuntimePipelineRunRequest
            {
                PipelineName = runRequest.PipelineName,
                PipelineJson = runRequest.PipelineJson,
                PipelineJsonFilePath = runRequest.PipelineJsonFilePath,
                PipelineDefinition = runRequest.PipelineDefinition,
                Input = runRequest.Input,
                ExecutionContextSnapshot = runRequest.ExecutionContextSnapshot,
                Metadata = MergeMetadata(
                    runRequest.Metadata,
                    metadata)
            };
        }

        /// <summary>
        /// Resolves the existing execution identifier when the enqueue request carries
        /// controlled recovery resume metadata.
        /// </summary>
        /// <param name="metadata">The enqueue metadata.</param>
        /// <returns>The execution identifier to resume, or <c>null</c>.</returns>
        private static RecoveryResumeRequest? ResolveRecoveryResume(
            IReadOnlyDictionary<string, string>? metadata)
        {
            if (metadata is null ||
                metadata.Count == 0)
            {
                return null;
            }

            if (!TryGetMetadataValue(
                    metadata,
                    RecoveryModeMetadataKey,
                    out var mode) ||
                string.IsNullOrWhiteSpace(mode))
            {
                return null;
            }

            if (string.Equals(
                    mode,
                    RecoveryModeRequeueLocalQueuedRun,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!string.Equals(
                    mode,
                    RecoveryModeResumeExistingExecution,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Unsupported runtime recovery mode '{mode}'.");
            }

            if (!TryGetMetadataValue(
                    metadata,
                    RecoveryFailedExecutionIdMetadataKey,
                    out var executionId) ||
                string.IsNullOrWhiteSpace(executionId))
            {
                throw new InvalidOperationException(
                    $"Recovery metadata '{RecoveryFailedExecutionIdMetadataKey}' is required when '{RecoveryModeMetadataKey}' is '{RecoveryModeResumeExistingExecution}'.");
            }

            if (!TryGetMetadataValue(
                    metadata,
                    RecoveryForensicsIdMetadataKey,
                    out var recoveryOwnerId) ||
                string.IsNullOrWhiteSpace(recoveryOwnerId))
            {
                throw new InvalidOperationException(
                    $"Recovery metadata '{RecoveryForensicsIdMetadataKey}' is required when '{RecoveryModeMetadataKey}' is '{RecoveryModeResumeExistingExecution}'.");
            }

            if (!recoveryOwnerId.StartsWith(
                    "runtime-recovery:",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Recovery owner id '{recoveryOwnerId}' is not a valid runtime recovery owner.");
            }

            return new RecoveryResumeRequest(
                executionId,
                recoveryOwnerId);
        }

        /// <summary>
        /// Gets a metadata value using case-insensitive key comparison.
        /// </summary>
        /// <param name="metadata">The metadata dictionary.</param>
        /// <param name="key">The metadata key.</param>
        /// <param name="value">The resolved metadata value.</param>
        /// <returns><c>true</c> when found; otherwise, <c>false</c>.</returns>
        private static bool TryGetMetadataValue(
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
        /// Merges two metadata dictionaries. Values from the second dictionary override values from the first.
        /// </summary>
        /// <param name="first">The first metadata dictionary.</param>
        /// <param name="second">The second metadata dictionary.</param>
        /// <returns>The merged metadata dictionary.</returns>
        private static IReadOnlyDictionary<string, string> MergeMetadata(
            IReadOnlyDictionary<string, string>? first,
            IReadOnlyDictionary<string, string>? second)
        {
            var metadata = new Dictionary<string, string>(
                StringComparer.Ordinal);

            if (first is not null)
            {
                foreach (var item in first)
                {
                    metadata[item.Key] = item.Value;
                }
            }

            if (second is not null)
            {
                foreach (var item in second)
                {
                    metadata[item.Key] = item.Value;
                }
            }

            return metadata;
        }

        /// <summary>
        /// Creates a visible runtime run state from an indexed local runtime run entry.
        /// </summary>
        /// <param name="entry">The indexed runtime run entry.</param>
        /// <returns>A runtime pipeline run state built from the index entry.</returns>
        private static AiRuntimePipelineRunState CreateRunStateFromIndex(
            AiRuntimeRunExecutionIndexEntry entry)
        {
            return new AiRuntimePipelineRunState
            {
                RunId = entry.RunId,
                ExecutionId = entry.ExecutionId,
                RuntimeInstanceId = entry.RuntimeInstanceId,
                Status = string.IsNullOrWhiteSpace(entry.Status)
                    ? "unknown"
                    : entry.Status,
                FailureReason = entry.FailureReason,
                StartedAtUtc = entry.StartedAtUtc,
                CompletedAtUtc = entry.CompletedAtUtc
            };
        }

        /// <summary>
        /// Represents a validated existing-execution recovery resume request.
        /// </summary>
        /// <param name="ExecutionId">The durable execution identifier to resume.</param>
        /// <param name="RecoveryOwnerId">The deterministic recovery owner identifier.</param>
        private sealed record RecoveryResumeRequest(
            string ExecutionId,
            string RecoveryOwnerId);

        /// <summary>
        /// Represents the raw result of an inner runtime queue operation before it is converted
        /// into a public <see cref="AiRuntimeQueueControlPlaneResult"/>.
        /// </summary>
        private sealed class RuntimeQueueOperationResult
        {
            /// <summary>
            /// Gets the runtime worker run handle returned by enqueue operations.
            /// </summary>
            public AiRuntimeWorkerRunHandle? RunHandle { get; init; }

            /// <summary>
            /// Gets the visible runtime pipeline run state when available.
            /// </summary>
            public AiRuntimePipelineRunState? RunState { get; init; }

            /// <summary>
            /// Gets the visible runtime pipeline queue state when available.
            /// </summary>
            public AiRuntimePipelineQueueState? QueueState { get; init; }
        }
    }
}