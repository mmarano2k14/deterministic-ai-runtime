using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.Core.ExecutionContext;

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
    /// V1 records the admission decision, shared run status, tenant execution
    /// context snapshot, and shared run lifecycle so the control-plane behavior
    /// is visible, testable, auditable, and tenant-aware.
    ///
    /// Important:
    /// This class does not execute DAG steps.
    /// It does not claim work directly.
    /// It does not directly create Kubernetes pods.
    /// It does not replace local runtime queues.
    ///
    /// Tenant model:
    /// - ExecutionContextSnapshot.TenantId is the persistent tenant boundary for
    ///   shared run records created by this controller.
    /// - ExecutionContextSnapshot.ContextKey is volatile and is stored only for
    ///   traceability/debugging. It must not be used as a durable execution id,
    ///   orchestration key, or tenant partition key.
    /// </remarks>
    public sealed class AiSharedRuntimeController : IAiSharedRuntimeController
    {
        private readonly IAiRunAdmissionController _admissionController;
        private readonly IAiSharedRunStore _store;
        private readonly IAiSharedQueue _sharedQueue;
        private readonly IAiSharedRunDispatcher _dispatcher;
        private readonly IAiRuntimeScaleOutRequestPublisher _scaleOutPublisher;
        private readonly IAiControlPlaneIdResolver _controlPlaneIdResolver;
        private readonly IAiTenantRuntimeSettingsProvider _tenantRuntimeSettingsProvider;
        private readonly AiSharedRuntimeControllerOptions _options;
        private readonly IAiControlPlaneObserver _observer;
        private readonly IExecutionContextSnapshotProvider _executionContextSnapshotProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiSharedRuntimeController"/> class.
        /// </summary>
        /// <param name="admissionController">The run admission controller.</param>
        /// <param name="store">The shared run store.</param>
        /// <param name="sharedQueue">The shared/global queue used when admission queues runs globally.</param>
        /// <param name="dispatcher">The shared run dispatcher used when admission assigns a run to an instance.</param>
        /// <param name="scaleOutPublisher">The scale-out request publisher used when admission requests more capacity.</param>
        /// <param name="controlPlaneIdResolver">The control-plane identifier resolver.</param>
        /// <param name="tenantRuntimeSettingsProvider">
        /// The tenant runtime settings provider used to resolve tenant-specific runtime capacity settings
        /// before publishing scale-out requests.
        /// </param>
        /// <param name="options">The shared runtime controller options.</param>
        /// <param name="observer">The control-plane observer used to record operation events.</param>
        /// <param name="executionContextSnapshotProvider">
        /// The provider used to map the current RBAC execution context into a durable
        /// execution context snapshot for shared run records.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when one of the required dependencies is null.
        /// </exception>
        public AiSharedRuntimeController(
            IAiRunAdmissionController admissionController,
            IAiSharedRunStore store,
            IAiSharedQueue sharedQueue,
            IAiSharedRunDispatcher dispatcher,
            IAiRuntimeScaleOutRequestPublisher scaleOutPublisher,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            IAiTenantRuntimeSettingsProvider tenantRuntimeSettingsProvider,
            IOptions<AiSharedRuntimeControllerOptions> options,
            IAiControlPlaneObserver observer,
            IExecutionContextSnapshotProvider executionContextSnapshotProvider)
        {
            _admissionController =
                admissionController
                ?? throw new ArgumentNullException(nameof(admissionController));

            _store =
                store
                ?? throw new ArgumentNullException(nameof(store));

            _sharedQueue =
                sharedQueue
                ?? throw new ArgumentNullException(nameof(sharedQueue));

            _dispatcher =
                dispatcher
                ?? throw new ArgumentNullException(nameof(dispatcher));

            _scaleOutPublisher =
                scaleOutPublisher
                ?? throw new ArgumentNullException(nameof(scaleOutPublisher));

            _controlPlaneIdResolver =
                controlPlaneIdResolver
                ?? throw new ArgumentNullException(nameof(controlPlaneIdResolver));

            _tenantRuntimeSettingsProvider =
                tenantRuntimeSettingsProvider
                ?? throw new ArgumentNullException(nameof(tenantRuntimeSettingsProvider));

            _options =
                options?.Value
                ?? throw new ArgumentNullException(nameof(options));

            _observer =
                observer
                ?? throw new ArgumentNullException(nameof(observer));

            _executionContextSnapshotProvider =
                executionContextSnapshotProvider
                ?? throw new ArgumentNullException(nameof(executionContextSnapshotProvider));
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
        /// tenant execution context snapshot mapping, observability, duration
        /// measurement, and structured failure handling.
        /// </summary>
        /// <param name="request">The shared runtime controller request.</param>
        /// <param name="operation">The operation to execute.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The shared runtime controller result.</returns>
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

                var executionContextSnapshot =
                    _executionContextSnapshotProvider.MapToSnapshot();

                await RecordStartedAsync(
                        request,
                        operation,
                        correlation,
                        executionContextSnapshot,
                        cancellationToken)
                    .ConfigureAwait(false);

                var operationResult = await ExecuteInnerAsync(
                        request,
                        operation,
                        executionContextSnapshot,
                        cancellationToken)
                    .ConfigureAwait(false);

                var completedAtUtc = DateTimeOffset.UtcNow;
                var durationMs = CalculateDurationMs(startedAtUtc, completedAtUtc);

                await RecordCompletedAsync(
                        request,
                        operation,
                        correlation,
                        operationResult,
                        executionContextSnapshot,
                        durationMs,
                        cancellationToken)
                    .ConfigureAwait(false);

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
                    DurationMs = durationMs,
                    FailureReason = operationResult.Run?.FailureReason
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
                        cancellationToken)
                    .ConfigureAwait(false);

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
        /// <param name="request">The shared runtime controller request.</param>
        /// <param name="operation">The operation to execute.</param>
        /// <param name="executionContextSnapshot">
        /// The mapped execution context snapshot for the current operation.
        /// </param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The internal operation result.</returns>
        private async Task<SharedRuntimeControllerOperationResult> ExecuteInnerAsync(
            AiSharedRuntimeControllerRequest request,
            AiSharedRuntimeControllerOperation operation,
            ExecutionContextSnapshot executionContextSnapshot,
            CancellationToken cancellationToken)
        {
            return operation switch
            {
                AiSharedRuntimeControllerOperation.SubmitRun =>
                    await SubmitRunInnerAsync(
                            request,
                            executionContextSnapshot,
                            cancellationToken)
                        .ConfigureAwait(false),

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
        /// Submits a run to the shared runtime controller, maps the current execution
        /// context into a shared run snapshot, and records the admission decision.
        /// </summary>
        /// <param name="request">The shared runtime controller request.</param>
        /// <param name="executionContextSnapshot">
        /// The execution context snapshot used as the tenant and audit source for this run.
        /// </param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The internal operation result.</returns>
        private async Task<SharedRuntimeControllerOperationResult> SubmitRunInnerAsync(
            AiSharedRuntimeControllerRequest request,
            ExecutionContextSnapshot executionContextSnapshot,
            CancellationToken cancellationToken)
        {
            var controlPlaneId =
                await ResolveControlPlaneIdAsync(cancellationToken)
                    .ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var sharedRunId = string.IsNullOrWhiteSpace(request.RequestedSharedRunId)
                ? Guid.NewGuid().ToString("N")
                : request.RequestedSharedRunId;

            var runRequest =
                AttachExecutionContextSnapshot(
                    request.RunRequest!,
                    executionContextSnapshot);

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
                        RunRequest = runRequest,
                        RunId = sharedRunId,
                        TenantId = executionContextSnapshot.TenantId,
                        PipelineKey = request.PipelineKey ?? runRequest.PipelineName,
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
                RunRequest = runRequest,
                ExecutionContextSnapshot = executionContextSnapshot,
                AssignedRuntimeInstanceId = queueFirst
                    ? null
                    : admissionDecision.AssignedRuntimeInstanceId,
                AdmissionDecision = admissionDecision,
                PipelineKey = request.PipelineKey ?? runRequest.PipelineName,
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
        /// <param name="created">The created shared run record.</param>
        /// <param name="admissionDecision">The admission decision.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The updated shared run record when dispatch succeeds; otherwise the original record with dispatch failure metadata.</returns>
        private async Task<AiSharedRunRecord> DispatchAssignedRunAsync(
            AiSharedRunRecord created,
            AiRunAdmissionDecision admissionDecision,
            CancellationToken cancellationToken)
        {
            var dispatchReason =
                admissionDecision.Reason ??
                "Admission assigned shared run to runtime instance.";

            var dispatchResult = await _dispatcher
                .DispatchAsync(
                    new AiSharedRunDispatchRequest
                    {
                        SharedRun = created,
                        RuntimeInstanceId = created.AssignedRuntimeInstanceId!,
                        CorrelationId = created.CorrelationId,
                        RequestedBy = created.RequestedBy,
                        Source = created.Source,
                        Reason = dispatchReason,
                        Metadata = created.Metadata
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!dispatchResult.Success)
            {
                return await _store
                    .MarkDispatchFailedAsync(
                        created.SharedRunId,
                        created.AssignedRuntimeInstanceId!,
                        dispatchResult.FailureReason,
                        dispatchResult.Message,
                        cancellationToken)
                    .ConfigureAwait(false) ?? created;
            }

            if (string.IsNullOrWhiteSpace(dispatchResult.LocalRunId))
            {
                return await _store
                    .MarkDispatchFailedAsync(
                        created.SharedRunId,
                        created.AssignedRuntimeInstanceId!,
                        "Direct dispatch succeeded but did not return a local run id.",
                        dispatchResult.Message,
                        cancellationToken)
                    .ConfigureAwait(false) ?? created;
            }

            var dispatchedQueueItem = await EnsureSharedQueueDispatchedOwnershipAsync(
                    created,
                    created.AssignedRuntimeInstanceId!,
                    dispatchResult.LocalRunId,
                    dispatchResult.ExecutionId,
                    dispatchResult.Message ?? dispatchReason,
                    created.Metadata,
                    cancellationToken)
                .ConfigureAwait(false);

            var dispatchedRun = await _store
                .MarkDispatchedAsync(
                    created.SharedRunId,
                    created.AssignedRuntimeInstanceId!,
                    dispatchResult.LocalRunId,
                    dispatchResult.ExecutionId,
                    dispatchResult.Message,
                    cancellationToken)
                .ConfigureAwait(false);

            if (dispatchedRun is not null)
            {
                return dispatchedRun;
            }

            await RequeueDispatchedOwnershipBestEffortAsync(
                    dispatchedQueueItem,
                    "Shared run store rejected direct dispatch persistence after queue ownership was materialized.",
                    created.Metadata,
                    cancellationToken)
                .ConfigureAwait(false);

            return await _store
                .MarkDispatchFailedAsync(
                    created.SharedRunId,
                    created.AssignedRuntimeInstanceId!,
                    "Shared run store rejected direct dispatch persistence.",
                    dispatchResult.Message,
                    cancellationToken)
                .ConfigureAwait(false) ?? created;
        }

        /// <summary>
        /// Ensures that a direct-dispatched shared run has durable shared queue ownership.
        /// </summary>
        /// <param name="sharedRun">The shared run record.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier that owns the local run.</param>
        /// <param name="localRunId">The local runtime run identifier returned by dispatch.</param>
        /// <param name="executionId">The optional execution identifier returned by dispatch.</param>
        /// <param name="reason">The ownership materialization reason.</param>
        /// <param name="metadata">Additional metadata to persist on the queue item.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The dispatched shared queue item.</returns>
        private async Task<AiSharedQueueItem> EnsureSharedQueueDispatchedOwnershipAsync(
            AiSharedRunRecord sharedRun,
            string runtimeInstanceId,
            string localRunId,
            string? executionId,
            string? reason,
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(sharedRun);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(localRunId);

            var existing = await _sharedQueue
                .GetAsync(
                    sharedRun.SharedRunId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is { Status: AiSharedQueueItemStatus.Dispatched } &&
                !string.IsNullOrWhiteSpace(existing.ClaimToken))
            {
                return existing;
            }

            var ownershipMetadata = MergeMetadata(
                sharedRun.Metadata,
                metadata ?? new Dictionary<string, string>(),
                new Dictionary<string, string>
                {
                    ["sharedRunId"] = sharedRun.SharedRunId,
                    ["shared.run.id"] = sharedRun.SharedRunId,
                    ["runtimeInstanceId"] = runtimeInstanceId,
                    ["runtime.instance.id"] = runtimeInstanceId,
                    ["localRunId"] = localRunId,
                    ["local.run.id"] = localRunId,
                    ["executionId"] = executionId ?? string.Empty,
                    ["execution.id"] = executionId ?? string.Empty,
                    ["claim.owner.runtimeInstanceId"] = runtimeInstanceId,
                    ["ownership.source"] = "shared-runtime-controller-direct-dispatch"
                });

            if (existing is null)
            {
                await _sharedQueue
                    .EnqueueAsync(
                        new AiSharedQueueItem
                        {
                            SharedRunId = sharedRun.SharedRunId,
                            ControlPlaneId = sharedRun.ControlPlaneId,
                            Status = AiSharedQueueItemStatus.Pending,
                            ExecutionContextSnapshot = sharedRun.ExecutionContextSnapshot,
                            PipelineKey = sharedRun.PipelineKey,
                            Priority = 0,
                            EnqueuedAtUtc = sharedRun.SubmittedAtUtc == default
                                ? DateTimeOffset.UtcNow
                                : sharedRun.SubmittedAtUtc,
                            UpdatedAtUtc = DateTimeOffset.UtcNow,
                            Reason = reason ?? "Direct dispatch ownership materialized.",
                            Metadata = ownershipMetadata
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (existing.Status == AiSharedQueueItemStatus.Claimed &&
                !string.IsNullOrWhiteSpace(existing.ClaimToken))
            {
                var claimedDispatched = await _sharedQueue
                    .MarkDispatchedAsync(
                        existing.SharedRunId,
                        existing.ClaimToken,
                        reason ?? "Direct dispatch ownership marked as dispatched from existing claim.",
                        cancellationToken)
                    .ConfigureAwait(false);

                if (claimedDispatched is not null)
                {
                    return claimedDispatched;
                }
            }

            var claim = await _sharedQueue
                .ClaimNextAsync(
                    new AiSharedQueueClaimRequest
                    {
                        RuntimeInstanceId = runtimeInstanceId,
                        WorkerId = $"direct-dispatch-{localRunId}",
                        TenantId = sharedRun.ExecutionContextSnapshot.TenantId,
                        PipelineKey = sharedRun.PipelineKey,
                        ClaimTtl = TimeSpan.FromMinutes(30),
                        CorrelationId = sharedRun.CorrelationId,
                        Reason = reason ?? "Direct dispatch ownership claimed."
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (claim is null ||
                !string.Equals(
                    claim.SharedRunId,
                    sharedRun.SharedRunId,
                    StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(claim.ClaimToken))
            {
                throw new InvalidOperationException(
                    $"Direct dispatch ownership could not be claimed for shared run '{sharedRun.SharedRunId}'.");
            }

            var dispatched = await _sharedQueue
                .MarkDispatchedAsync(
                    sharedRun.SharedRunId,
                    claim.ClaimToken,
                    reason ?? "Direct dispatch ownership marked as dispatched.",
                    cancellationToken)
                .ConfigureAwait(false);

            if (dispatched is null)
            {
                throw new InvalidOperationException(
                    $"Direct dispatch ownership could not be marked as dispatched for shared run '{sharedRun.SharedRunId}'.");
            }

            return dispatched;
        }

        /// <summary>
        /// Attempts to requeue a dispatched queue item without masking the original failure.
        /// </summary>
        /// <param name="queueItem">The dispatched queue item.</param>
        /// <param name="reason">The requeue reason.</param>
        /// <param name="metadata">The metadata to merge into the queue item.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        private async Task RequeueDispatchedOwnershipBestEffortAsync(
            AiSharedQueueItem queueItem,
            string reason,
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(queueItem.ClaimToken))
            {
                return;
            }

            try
            {
                await _sharedQueue
                    .RequeueDispatchedAsync(
                        queueItem.SharedRunId,
                        queueItem.ClaimToken,
                        reason,
                        metadata,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Best-effort rollback. The original dispatch failure must remain the visible outcome.
            }
        }

        /// <summary>
        /// Enqueues a shared run into the global shared queue.
        /// </summary>
        /// <param name="created">The created shared run record.</param>
        /// <param name="admissionDecision">The admission decision that caused the enqueue.</param>
        /// <param name="now">The timestamp used for queue creation/update.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
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
                        ExecutionContextSnapshot = created.ExecutionContextSnapshot,
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
        /// <param name="created">The created shared run record.</param>
        /// <param name="admissionDecision">The scale-out admission decision.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        private async Task PublishScaleOutRequestAsync(
            AiSharedRunRecord created,
            AiRunAdmissionDecision admissionDecision,
            CancellationToken cancellationToken)
        {
            var tenantRuntimeSettings =
                admissionDecision.TenantRuntimeSettings ??
                _tenantRuntimeSettingsProvider.GetSettings(
                    created.ExecutionContextSnapshot.TenantId,
                    created.ExecutionContextSnapshot.TenantGroupId);

            var tenantId =
                !string.IsNullOrWhiteSpace(admissionDecision.TenantId)
                    ? admissionDecision.TenantId
                    : tenantRuntimeSettings.TenantId ?? created.ExecutionContextSnapshot.TenantId;

            var tenantGroupId =
                !string.IsNullOrWhiteSpace(admissionDecision.TenantGroupId)
                    ? admissionDecision.TenantGroupId
                    : tenantRuntimeSettings.TenantGroupId ?? created.ExecutionContextSnapshot.TenantGroupId;

            await _scaleOutPublisher
                .PublishAsync(
                    new AiRuntimeScaleOutRequest
                    {
                        SharedRun = created,
                        SharedRunId = created.SharedRunId,
                        ExecutionContextSnapshot = created.ExecutionContextSnapshot,

                        TenantId = tenantId,
                        TenantGroupId = tenantGroupId,
                        PipelineKey = created.PipelineKey,

                        IsolationMode = tenantRuntimeSettings.IsolationMode,
                        PreferDedicatedCapacity = tenantRuntimeSettings.PreferDedicatedCapacity,
                        AllowSharedFallback = tenantRuntimeSettings.AllowSharedFallback,
                        MaxRuntimeInstances = tenantRuntimeSettings.MaxRuntimeInstances,
                        RuntimeInstanceIdPrefix = tenantRuntimeSettings.RuntimeInstanceIdPrefix,
                        WorkerCountPerInstance = tenantRuntimeSettings.WorkerCountPerInstance,
                        MaxConcurrentRunsPerInstance = tenantRuntimeSettings.MaxConcurrentRunsPerInstance,
                        LocalQueueCapacity = tenantRuntimeSettings.LocalQueueCapacity,

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
        /// <param name="request">The shared runtime controller request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The internal operation result.</returns>
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
        /// <param name="request">The shared runtime controller request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The internal operation result.</returns>
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
        /// <param name="request">The shared runtime controller request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The internal operation result.</returns>
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
        /// Attaches the durable execution context snapshot to the runtime pipeline
        /// run request before the shared run is persisted and dispatched.
        /// </summary>
        /// <param name="request">The original runtime pipeline run request.</param>
        /// <param name="executionContextSnapshot">
        /// The durable execution context snapshot captured by the control plane.
        /// </param>
        /// <returns>
        /// A runtime pipeline run request carrying the execution context snapshot.
        /// </returns>
        private static AiRuntimePipelineRunRequest AttachExecutionContextSnapshot(
            AiRuntimePipelineRunRequest request,
            ExecutionContextSnapshot executionContextSnapshot)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(executionContextSnapshot);

            return new AiRuntimePipelineRunRequest
            {
                PipelineName = request.PipelineName,
                ExecutionContextSnapshot = executionContextSnapshot,
                PipelineJson = request.PipelineJson,
                PipelineJsonFilePath = request.PipelineJsonFilePath,
                PipelineDefinition = request.PipelineDefinition,
                Input = request.Input
            };
        }

        /// <summary>
        /// Maps an admission decision to a shared run status.
        /// </summary>
        /// <param name="decision">The admission decision.</param>
        /// <returns>The shared run status.</returns>
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
        /// <param name="request">The shared runtime controller request.</param>
        /// <returns>The runtime execution correlation context.</returns>
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
        /// <param name="request">The shared runtime controller request.</param>
        /// <param name="operation">The operation that started.</param>
        /// <param name="correlation">The runtime correlation context.</param>
        /// <param name="executionContextSnapshot">
        /// The execution context snapshot associated with the operation.
        /// </param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        private async Task RecordStartedAsync(
            AiSharedRuntimeControllerRequest request,
            AiSharedRuntimeControllerOperation operation,
            AiRuntimeExecutionCorrelationContext correlation,
            ExecutionContextSnapshot executionContextSnapshot,
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
                        [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = executionContextSnapshot.TenantId,
                        [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = executionContextSnapshot.TenantGroupId,
                        ["project"] = executionContextSnapshot.Project,
                        ["userId"] = executionContextSnapshot.UserId,
                        ["contextKey"] = executionContextSnapshot.ContextKey,
                        ["pipelineKey"] = request.PipelineKey
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Records a control-plane operation completed event.
        /// </summary>
        /// <param name="request">The shared runtime controller request.</param>
        /// <param name="operation">The operation that completed.</param>
        /// <param name="correlation">The runtime correlation context.</param>
        /// <param name="operationResult">The internal operation result.</param>
        /// <param name="executionContextSnapshot">
        /// The execution context snapshot associated with the operation.
        /// </param>
        /// <param name="durationMs">The operation duration in milliseconds.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        private async Task RecordCompletedAsync(
            AiSharedRuntimeControllerRequest request,
            AiSharedRuntimeControllerOperation operation,
            AiRuntimeExecutionCorrelationContext correlation,
            SharedRuntimeControllerOperationResult operationResult,
            ExecutionContextSnapshot executionContextSnapshot,
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
                        ["failureReason"] = operationResult.Run?.FailureReason,
                        [AiRuntimeInstanceIsolationMetadataKeys.TenantId] =
                            operationResult.Run?.ExecutionContextSnapshot.TenantId
                            ?? executionContextSnapshot.TenantId,
                        [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] =
                            operationResult.Run?.ExecutionContextSnapshot.TenantGroupId
                            ?? executionContextSnapshot.TenantGroupId,
                        ["project"] =
                            operationResult.Run?.ExecutionContextSnapshot.Project
                            ?? executionContextSnapshot.Project,
                        ["userId"] =
                            operationResult.Run?.ExecutionContextSnapshot.UserId
                            ?? executionContextSnapshot.UserId,
                        ["contextKey"] =
                            operationResult.Run?.ExecutionContextSnapshot.ContextKey
                            ?? executionContextSnapshot.ContextKey,
                        ["runCount"] = operationResult.Runs.Count
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Records a control-plane operation failed event.
        /// </summary>
        /// <param name="request">The shared runtime controller request, if available.</param>
        /// <param name="operation">The operation that failed.</param>
        /// <param name="correlation">The runtime correlation context.</param>
        /// <param name="exception">The exception that caused the failure.</param>
        /// <param name="durationMs">The operation duration in milliseconds.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
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
        /// <param name="request">The request to validate.</param>
        /// <param name="operation">The operation to validate for.</param>
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
        /// <param name="operation">The operation to inspect.</param>
        /// <returns><c>true</c> when the operation requires a shared run id; otherwise <c>false</c>.</returns>
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
        /// <param name="operation">The requested operation.</param>
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
        /// <param name="startedAtUtc">The operation start timestamp.</param>
        /// <param name="completedAtUtc">The operation completed timestamp.</param>
        /// <returns>The operation duration in milliseconds.</returns>
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
        /// <param name="metadata">The metadata to copy.</param>
        /// <returns>The copied metadata dictionary.</returns>
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
        /// <param name="sources">The metadata sources to merge.</param>
        /// <returns>The merged metadata dictionary.</returns>
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