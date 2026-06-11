using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Observability.Context;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController
{
    /// <summary>
    /// V1 implementation of the shared runtime controller.
    /// </summary>
    /// <remarks>
    /// The shared runtime controller sits above run admission, runtime instances,
    /// local runtime queue control, the shared queue, the shared run dispatcher,
    /// the scale-out request publisher, and the future Kubernetes scale-out adapter.
    ///
    /// V1 records the admission decision and shared run status so the control-plane
    /// lifecycle is visible and testable before adding remote Kubernetes behavior.
    ///
    /// Important:
    /// This class does not execute DAG steps.
    /// It does not claim work directly.
    /// It does not directly create Kubernetes pods.
    /// It does not replace local runtime queues.
    /// </remarks>
    public sealed class AiSharedRuntimeController : IAiSharedRuntimeController
    {
        private readonly IAiRunAdmissionController _admissionController;
        private readonly IAiSharedRunStore _store;
        private readonly IAiSharedQueue _sharedQueue;
        private readonly IAiSharedRunDispatcher _dispatcher;
        private readonly IAiRuntimeScaleOutRequestPublisher _scaleOutPublisher;
        private readonly IAiControlPlaneIdResolver _controlPlaneIdResolver;
        private readonly AiSharedRuntimeControllerOptions _options;
        private readonly IAiControlPlaneObserver _observer;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiSharedRuntimeController"/> class.
        /// </summary>
        /// <param name="admissionController">The run admission controller.</param>
        /// <param name="store">The shared run store.</param>
        /// <param name="sharedQueue">The shared/global queue used when admission queues runs globally.</param>
        /// <param name="dispatcher">The shared run dispatcher used when admission assigns a run to an instance.</param>
        /// <param name="scaleOutPublisher">The scale-out request publisher used when admission requests more capacity.</param>
        /// <param name="controlPlaneIdResolver">The control-plane identifier resolver.</param>
        /// <param name="options">The shared runtime controller options.</param>
        /// <param name="observer">The control-plane observer used to record operation events.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="admissionController"/>, <paramref name="store"/>,
        /// <paramref name="sharedQueue"/>, <paramref name="dispatcher"/>,
        /// <paramref name="scaleOutPublisher"/>, <paramref name="controlPlaneIdResolver"/>,
        /// <paramref name="options"/>, or <paramref name="observer"/> is null.
        /// </exception>
        public AiSharedRuntimeController(
            IAiRunAdmissionController admissionController,
            IAiSharedRunStore store,
            IAiSharedQueue sharedQueue,
            IAiSharedRunDispatcher dispatcher,
            IAiRuntimeScaleOutRequestPublisher scaleOutPublisher,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            IOptions<AiSharedRuntimeControllerOptions> options,
            IAiControlPlaneObserver observer)
        {
            _admissionController = admissionController ?? throw new ArgumentNullException(nameof(admissionController));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _sharedQueue = sharedQueue ?? throw new ArgumentNullException(nameof(sharedQueue));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _scaleOutPublisher = scaleOutPublisher ?? throw new ArgumentNullException(nameof(scaleOutPublisher));
            _controlPlaneIdResolver = controlPlaneIdResolver ?? throw new ArgumentNullException(nameof(controlPlaneIdResolver));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        }

        /// <inheritdoc />
        public Task<AiSharedRuntimeControllerResult> ExecuteAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            return request.Operation switch
            {
                AiSharedRuntimeControllerOperation.SubmitRun => SubmitRunAsync(request, cancellationToken),
                AiSharedRuntimeControllerOperation.GetRun => GetRunAsync(request, cancellationToken),
                AiSharedRuntimeControllerOperation.ListRuns => ListRunsAsync(request, cancellationToken),
                AiSharedRuntimeControllerOperation.CancelRun => CancelRunAsync(request, cancellationToken),

                _ => throw new NotSupportedException(
                    $"Shared runtime controller operation '{request.Operation}' is not supported.")
            };
        }

        /// <inheritdoc />
        public Task<AiSharedRuntimeControllerResult> SubmitRunAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteControllerOperationAsync(
                request,
                AiSharedRuntimeControllerOperation.SubmitRun,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiSharedRuntimeControllerResult> GetRunAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteControllerOperationAsync(
                request,
                AiSharedRuntimeControllerOperation.GetRun,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiSharedRuntimeControllerResult> ListRunsAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteControllerOperationAsync(
                request,
                AiSharedRuntimeControllerOperation.ListRuns,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiSharedRuntimeControllerResult> CancelRunAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteControllerOperationAsync(
                request,
                AiSharedRuntimeControllerOperation.CancelRun,
                cancellationToken);
        }

        /// <summary>
        /// Executes one shared runtime controller operation with validation,
        /// observability, duration measurement, and structured failure handling.
        /// </summary>
        private async Task<AiSharedRuntimeControllerResult> ExecuteControllerOperationAsync(
            AiSharedRuntimeControllerRequest request,
            AiSharedRuntimeControllerOperation operation,
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

                await RecordCompletedAsync(
                    request,
                    operation,
                    correlation,
                    operationResult,
                    durationMs,
                    cancellationToken).ConfigureAwait(false);

                return new AiSharedRuntimeControllerResult
                {
                    Operation = operation,
                    Success = true,
                    Message = $"Shared runtime controller operation '{operation}' completed successfully.",
                    SharedRunId =
                        operationResult.Run?.SharedRunId ??
                        request.SharedRunId ??
                        request.RequestedSharedRunId,
                    LocalRunId = operationResult.Run?.LocalRunId,
                    ExecutionId = operationResult.Run?.ExecutionId,
                    AssignedRuntimeInstanceId = operationResult.Run?.AssignedRuntimeInstanceId,
                    Run = operationResult.Run,
                    Runs = operationResult.Runs,
                    CorrelationId = correlation.CorrelationId,
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

                return new AiSharedRuntimeControllerResult
                {
                    Operation = operation,
                    Success = false,
                    Message = $"Shared runtime controller operation '{operation}' failed.",
                    SharedRunId = request.SharedRunId ?? request.RequestedSharedRunId,
                    Diagnostics = request.IncludeDiagnostics
                        ? new[] { exception.Message }
                        : Array.Empty<string>(),
                    CorrelationId = correlation.CorrelationId,
                    RequestedBy = request.RequestedBy,
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = completedAtUtc,
                    DurationMs = durationMs,
                    FailureReason = exception.Message
                };
            }
        }

        /// <summary>
        /// Dispatches the shared controller operation to the matching internal handler.
        /// </summary>
        private async Task<SharedRuntimeControllerOperationResult> ExecuteInnerAsync(
            AiSharedRuntimeControllerRequest request,
            AiSharedRuntimeControllerOperation operation,
            CancellationToken cancellationToken)
        {
            return operation switch
            {
                AiSharedRuntimeControllerOperation.SubmitRun =>
                    await SubmitRunInnerAsync(request, cancellationToken).ConfigureAwait(false),

                AiSharedRuntimeControllerOperation.GetRun =>
                    await GetRunInnerAsync(request, cancellationToken).ConfigureAwait(false),

                AiSharedRuntimeControllerOperation.ListRuns =>
                    await ListRunsInnerAsync(request, cancellationToken).ConfigureAwait(false),

                AiSharedRuntimeControllerOperation.CancelRun =>
                    await CancelRunInnerAsync(request, cancellationToken).ConfigureAwait(false),

                _ => throw new NotSupportedException(
                    $"Shared runtime controller operation '{operation}' is not supported.")
            };
        }

        /// <summary>
        /// Submits a run to the shared runtime controller and records the admission decision.
        /// </summary>
        private async Task<SharedRuntimeControllerOperationResult> SubmitRunInnerAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken)
        {
            var controlPlaneId =
                await ResolveControlPlaneIdAsync(cancellationToken)
                    .ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var sharedRunId = string.IsNullOrWhiteSpace(request.RequestedSharedRunId)
                ? Guid.NewGuid().ToString("N")
                : request.RequestedSharedRunId;

            var metadata =
                MergeMetadata(
                    request.Metadata,
                    new Dictionary<string, string>
                    {
                        ["controlPlaneId"] = controlPlaneId
                    });

            var admissionDecision = await _admissionController
                .AdmitAsync(
                    new AiRunAdmissionRequest
                    {
                        RunRequest = request.RunRequest!,
                        RunId = sharedRunId,
                        TenantId = request.TenantId,
                        PipelineKey = request.PipelineKey ?? request.RunRequest?.PipelineName,
                        PreferredRuntimeInstanceId = request.PreferredRuntimeInstanceId,
                        CorrelationId = request.CorrelationId,
                        RequestedBy = request.RequestedBy,
                        Source = request.Source,
                        Reason = request.Reason,
                        Metadata = metadata
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var queueFirst =
                _options.SubmitMode == AiSharedRuntimeSubmitMode.QueueFirst;

            var effectiveStatus = queueFirst
                ? AiSharedRunStatus.QueuedGlobally
                : MapAdmissionDecisionToStatus(admissionDecision);

            var failureReason = !queueFirst && admissionDecision.Rejected
                ? admissionDecision.Reason
                : null;

            var record = new AiSharedRunRecord
            {
                SharedRunId = sharedRunId,
                ControlPlaneId = controlPlaneId,
                Status = effectiveStatus,
                RunRequest = request.RunRequest!,
                AssignedRuntimeInstanceId = queueFirst
                    ? null
                    : admissionDecision.AssignedRuntimeInstanceId,
                AdmissionDecision = admissionDecision,
                TenantId = request.TenantId,
                PipelineKey = request.PipelineKey ?? request.RunRequest?.PipelineName,
                CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId)
                    ? sharedRunId
                    : request.CorrelationId,
                RequestedBy = request.RequestedBy,
                Source = request.Source,
                Reason = request.Reason,
                FailureReason = failureReason,
                SubmittedAtUtc = now,
                UpdatedAtUtc = now,
                Metadata = metadata
            };

            var created = await _store
                .CreateAsync(record, cancellationToken)
                .ConfigureAwait(false);

            var current = created;

            if (queueFirst)
            {
                await EnqueueGloballyAsync(
                        created,
                        admissionDecision,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);

                return new SharedRuntimeControllerOperationResult
                {
                    Run = current
                };
            }

            if (admissionDecision.DecisionType == AiRunAdmissionDecisionType.AssignToInstance &&
                !string.IsNullOrWhiteSpace(created.AssignedRuntimeInstanceId))
            {
                current = await DispatchAssignedRunAsync(
                        created,
                        admissionDecision,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (admissionDecision.DecisionType == AiRunAdmissionDecisionType.QueueGlobally)
            {
                await EnqueueGloballyAsync(
                        created,
                        admissionDecision,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (admissionDecision.DecisionType == AiRunAdmissionDecisionType.RequestScaleOut)
            {
                await PublishScaleOutRequestAsync(
                        created,
                        admissionDecision,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return new SharedRuntimeControllerOperationResult
            {
                Run = current
            };
        }

        /// <summary>
        /// Dispatches a shared run that was assigned to a runtime instance by admission.
        /// </summary>
        private async Task<AiSharedRunRecord> DispatchAssignedRunAsync(
            AiSharedRunRecord created,
            AiRunAdmissionDecision admissionDecision,
            CancellationToken cancellationToken)
        {
            var dispatchResult = await _dispatcher
                .DispatchAsync(
                    new AiSharedRunDispatchRequest
                    {
                        SharedRun = created,
                        RuntimeInstanceId = created.AssignedRuntimeInstanceId!,
                        CorrelationId = created.CorrelationId,
                        RequestedBy = created.RequestedBy,
                        Source = created.Source,
                        Reason = admissionDecision.Reason ?? "Admission assigned shared run to runtime instance.",
                        Metadata = created.Metadata
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!dispatchResult.Success)
            {
                return created;
            }

            return await _store
                .MarkDispatchedAsync(
                    created.SharedRunId,
                    created.AssignedRuntimeInstanceId!,
                    dispatchResult.LocalRunId,
                    dispatchResult.ExecutionId,
                    dispatchResult.Message,
                    cancellationToken)
                .ConfigureAwait(false) ?? created;
        }

        /// <summary>
        /// Enqueues a shared run into the global shared queue.
        /// </summary>
        private async Task EnqueueGloballyAsync(
            AiSharedRunRecord created,
            AiRunAdmissionDecision admissionDecision,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            await _sharedQueue
                .EnqueueAsync(
                    new AiSharedQueueItem
                    {
                        SharedRunId = created.SharedRunId,
                        ControlPlaneId = created.ControlPlaneId,
                        Status = AiSharedQueueItemStatus.Pending,
                        TenantId = created.TenantId,
                        PipelineKey = created.PipelineKey,
                        Priority = 0,
                        EnqueuedAtUtc = now,
                        UpdatedAtUtc = now,
                        Reason = admissionDecision.Reason,
                        Metadata = created.Metadata
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Publishes a scale-out request for a shared run when admission requests more runtime capacity.
        /// </summary>
        private async Task PublishScaleOutRequestAsync(
            AiSharedRunRecord created,
            AiRunAdmissionDecision admissionDecision,
            CancellationToken cancellationToken)
        {
            await _scaleOutPublisher
                .PublishAsync(
                    new AiRuntimeScaleOutRequest
                    {
                        SharedRun = created,
                        SharedRunId = created.SharedRunId,
                        TenantId = created.TenantId,
                        PipelineKey = created.PipelineKey,
                        VisibleInstanceCount = admissionDecision.VisibleInstanceCount,
                        AvailableInstanceCount = admissionDecision.AvailableInstanceCount,
                        CurrentInstanceCount = admissionDecision.CurrentInstanceCount,
                        MaxInstanceCount = admissionDecision.MaxInstanceCount,
                        CorrelationId = created.CorrelationId,
                        RequestedBy = created.RequestedBy,
                        Source = created.Source,
                        Reason = admissionDecision.Reason ?? "Admission requested runtime scale-out.",
                        Metadata = created.Metadata
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Gets a shared run record.
        /// </summary>
        private async Task<SharedRuntimeControllerOperationResult> GetRunInnerAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken)
        {
            var record = await _store
                .GetAsync(request.SharedRunId!, cancellationToken)
                .ConfigureAwait(false);

            return new SharedRuntimeControllerOperationResult
            {
                Run = record
            };
        }

        /// <summary>
        /// Lists shared run records known by the controller.
        /// </summary>
        private async Task<SharedRuntimeControllerOperationResult> ListRunsInnerAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken)
        {
            var runs = await _store
                .ListAsync(
                    request.IncludeCancelled,
                    request.IncludeCompleted,
                    request.IncludeFailed,
                    cancellationToken)
                .ConfigureAwait(false);

            return new SharedRuntimeControllerOperationResult
            {
                Runs = runs
            };
        }

        /// <summary>
        /// Cancels a shared run known by the controller.
        /// </summary>
        private async Task<SharedRuntimeControllerOperationResult> CancelRunInnerAsync(
            AiSharedRuntimeControllerRequest request,
            CancellationToken cancellationToken)
        {
            var updated = await _store
                .CancelAsync(
                    request.SharedRunId!,
                    request.Reason,
                    request.RequestedBy,
                    request.Source,
                    cancellationToken)
                .ConfigureAwait(false);

            return new SharedRuntimeControllerOperationResult
            {
                Run = updated
            };
        }

        /// <summary>
        /// Maps an admission decision to a shared run status.
        /// </summary>
        private static AiSharedRunStatus MapAdmissionDecisionToStatus(
            AiRunAdmissionDecision decision)
        {
            return decision.DecisionType switch
            {
                AiRunAdmissionDecisionType.AssignToInstance => AiSharedRunStatus.AssignedToInstance,
                AiRunAdmissionDecisionType.QueueGlobally => AiSharedRunStatus.QueuedGlobally,
                AiRunAdmissionDecisionType.RequestScaleOut => AiSharedRunStatus.ScaleOutRequested,
                AiRunAdmissionDecisionType.Reject => AiSharedRunStatus.Rejected,
                _ => AiSharedRunStatus.Accepted
            };
        }

        /// <summary>
        /// Creates a runtime correlation context for shared controller observability.
        /// </summary>
        private static AiRuntimeExecutionCorrelationContext CreateCorrelation(
            AiSharedRuntimeControllerRequest request)
        {
            var sharedRunId =
                request.SharedRunId ??
                request.RequestedSharedRunId;

            return new AiRuntimeExecutionCorrelationContext
            {
                CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId)
                    ? sharedRunId ?? Guid.NewGuid().ToString("N")
                    : request.CorrelationId,

                RunId = sharedRunId
            };
        }

        /// <summary>
        /// Records a control-plane operation started event.
        /// </summary>
        private async Task RecordStartedAsync(
            AiSharedRuntimeControllerRequest request,
            AiSharedRuntimeControllerOperation operation,
            AiRuntimeExecutionCorrelationContext correlation,
            CancellationToken cancellationToken)
        {
            await _observer.RecordAsync(
                new AiControlPlaneEvent
                {
                    EventType = AiControlPlaneEventType.OperationStarted,
                    Area = AiControlPlaneArea.SharedController,
                    Operation = operation.ToString(),
                    Correlation = correlation,
                    Message = $"Shared runtime controller operation '{operation}' started.",
                    Properties = new Dictionary<string, object?>
                    {
                        ["source"] = request.Source,
                        ["requestedBy"] = request.RequestedBy,
                        ["reason"] = request.Reason,
                        ["sharedRunId"] = request.SharedRunId ?? request.RequestedSharedRunId,
                        ["preferredRuntimeInstanceId"] = request.PreferredRuntimeInstanceId,
                        ["tenantId"] = request.TenantId,
                        ["pipelineKey"] = request.PipelineKey
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Records a control-plane operation completed event.
        /// </summary>
        private async Task RecordCompletedAsync(
            AiSharedRuntimeControllerRequest request,
            AiSharedRuntimeControllerOperation operation,
            AiRuntimeExecutionCorrelationContext correlation,
            SharedRuntimeControllerOperationResult operationResult,
            long durationMs,
            CancellationToken cancellationToken)
        {
            await _observer.RecordAsync(
                new AiControlPlaneEvent
                {
                    EventType = AiControlPlaneEventType.OperationCompleted,
                    Area = AiControlPlaneArea.SharedController,
                    Operation = operation.ToString(),
                    Outcome = AiControlPlaneOperationOutcome.Succeeded,
                    Correlation = correlation,
                    DurationMs = durationMs,
                    Message = $"Shared runtime controller operation '{operation}' completed successfully.",
                    Properties = new Dictionary<string, object?>
                    {
                        ["source"] = request.Source,
                        ["requestedBy"] = request.RequestedBy,
                        ["controlPlaneId"] = operationResult.Run?.ControlPlaneId,
                        ["sharedRunId"] = operationResult.Run?.SharedRunId ?? request.SharedRunId ?? request.RequestedSharedRunId,
                        ["status"] = operationResult.Run?.Status.ToString(),
                        ["assignedRuntimeInstanceId"] = operationResult.Run?.AssignedRuntimeInstanceId,
                        ["localRunId"] = operationResult.Run?.LocalRunId,
                        ["executionId"] = operationResult.Run?.ExecutionId,
                        ["runCount"] = operationResult.Runs.Count
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Records a control-plane operation failed event.
        /// </summary>
        private async Task RecordFailedAsync(
            AiSharedRuntimeControllerRequest? request,
            AiSharedRuntimeControllerOperation operation,
            AiRuntimeExecutionCorrelationContext correlation,
            Exception exception,
            long durationMs,
            CancellationToken cancellationToken)
        {
            await _observer.RecordAsync(
                new AiControlPlaneEvent
                {
                    EventType = AiControlPlaneEventType.OperationFailed,
                    Area = AiControlPlaneArea.SharedController,
                    Operation = operation.ToString(),
                    Outcome = AiControlPlaneOperationOutcome.Failed,
                    Correlation = correlation,
                    DurationMs = durationMs,
                    Message = $"Shared runtime controller operation '{operation}' failed.",
                    FailureReason = exception.Message,
                    Properties = new Dictionary<string, object?>
                    {
                        ["source"] = request?.Source,
                        ["requestedBy"] = request?.RequestedBy,
                        ["sharedRunId"] = request?.SharedRunId ?? request?.RequestedSharedRunId,
                        ["exceptionType"] = exception.GetType().Name
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Resolves the logical control-plane identifier used to scope shared run records.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved logical control-plane identifier.</returns>
        private async Task<string> ResolveControlPlaneIdAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await _controlPlaneIdResolver
                    .ResolveAsync(cancellationToken)
                    .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(controlPlaneId))
            {
                throw new InvalidOperationException(
                    "The resolved control-plane identifier cannot be null or empty.");
            }

            return controlPlaneId;
        }

        /// <summary>
        /// Validates a shared runtime controller request for the specified operation.
        /// </summary>
        private static void ValidateRequest(
            AiSharedRuntimeControllerRequest request,
            AiSharedRuntimeControllerOperation operation)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (operation == AiSharedRuntimeControllerOperation.SubmitRun &&
                request.RunRequest is null)
            {
                throw new ArgumentException(
                    "RunRequest is required for SubmitRun operations.",
                    nameof(request));
            }

            if (RequiresSharedRunId(operation) &&
                string.IsNullOrWhiteSpace(request.SharedRunId))
            {
                throw new ArgumentException(
                    "SharedRunId is required for this shared runtime controller operation.",
                    nameof(request));
            }
        }

        /// <summary>
        /// Determines whether the operation requires a shared run identifier.
        /// </summary>
        private static bool RequiresSharedRunId(
            AiSharedRuntimeControllerOperation operation)
        {
            return operation is
                AiSharedRuntimeControllerOperation.GetRun or
                AiSharedRuntimeControllerOperation.CancelRun;
        }

        /// <summary>
        /// Ensures the requested shared runtime controller operation is enabled.
        /// </summary>
        private void EnsureEnabled(
            AiSharedRuntimeControllerOperation operation)
        {
            var enabled = operation switch
            {
                AiSharedRuntimeControllerOperation.SubmitRun => _options.EnableSubmitRun,
                AiSharedRuntimeControllerOperation.GetRun => _options.EnableGetRun,
                AiSharedRuntimeControllerOperation.ListRuns => _options.EnableListRuns,
                AiSharedRuntimeControllerOperation.CancelRun => _options.EnableCancelRun,
                _ => false
            };

            if (!enabled)
            {
                throw new InvalidOperationException(
                    $"Shared runtime controller operation '{operation}' is disabled.");
            }
        }

        /// <summary>
        /// Calculates the control-plane operation duration in milliseconds.
        /// </summary>
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
        /// Copies shared run metadata into an immutable dictionary shape.
        /// </summary>
        private static IReadOnlyDictionary<string, string> CopyMetadata(
            IReadOnlyDictionary<string, string> metadata)
        {
            return new Dictionary<string, string>(
                metadata,
                StringComparer.Ordinal);
        }

        /// <summary>
        /// Merges shared run metadata dictionaries into an immutable dictionary shape.
        /// </summary>
        private static IReadOnlyDictionary<string, string> MergeMetadata(
            params IReadOnlyDictionary<string, string>[] sources)
        {
            var result =
                new Dictionary<string, string>(
                    StringComparer.Ordinal);

            foreach (var source in sources)
            {
                foreach (var item in source)
                {
                    if (!string.IsNullOrWhiteSpace(item.Key))
                    {
                        result[item.Key] = item.Value;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Internal operation result produced by the shared runtime controller.
        /// </summary>
        private sealed class SharedRuntimeControllerOperationResult
        {
            /// <summary>
            /// Gets the shared run record returned by single-run operations.
            /// </summary>
            public AiSharedRunRecord? Run { get; init; }

            /// <summary>
            /// Gets the shared run records returned by list operations.
            /// </summary>
            public IReadOnlyList<AiSharedRunRecord> Runs { get; init; } =
                Array.Empty<AiSharedRunRecord>();
        }
    }
}