using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Rbac.Core.ExecutionContext;
using RbacExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedQueue
{
    /// <summary>
    /// Default implementation of the shared queue dispatcher.
    /// </summary>
    /// <remarks>
    /// This service bridges the global shared queue and the shared run dispatcher.
    ///
    /// Responsibilities:
    /// - atomically claim one pending shared queue item
    /// - load the associated shared run record
    /// - select an available runtime instance through admission
    /// - publish a replacement scale-out request when admission asks for more runtime capacity
    /// - reserve temporary admission capacity before dispatch
    /// - dispatch the shared run to the selected runtime instance
    /// - mark the queue item as dispatched on success
    /// - mark the shared run record as dispatched on success
    /// - persist dispatch failure metadata when dispatch fails
    /// - requeue the item when dispatch fails or throws
    /// - release temporary admission reservations after dispatch attempts
    ///
    /// This service does not scale Kubernetes directly.
    /// It does not execute DAG steps directly.
    /// </remarks>
    public sealed class AiSharedQueueDispatcher : IAiSharedQueueDispatcher
    {
        private const string ScaleOutRequestIdMetadataKey = "scaleout.requestId";
        private const string ScaleOutIntentMetadataKey = "scaleout.intent";
        private const string ScaleOutIntentSharedQueueRedispatchReplacement = "shared-queue-redispatch-replacement";
        private const string SharedQueueRedispatchReplacementReason = "Shared queue redispatch requested replacement runtime capacity.";
        private const string RecoveryForensicsIdMetadataKey = "recovery.forensicsId";
        private const string RecoveryFailedExecutionIdMetadataKey = "recovery.failedExecutionId";
        private const string RecoveryFailedRuntimeInstanceIdMetadataKey = "recovery.failedRuntimeInstanceId";
        private const string RecoveryFailedLocalRunIdMetadataKey = "recovery.failedLocalRunId";

        private readonly IAiSharedQueue _sharedQueue;
        private readonly IAiSharedRunStore _sharedRunStore;
        private readonly IAiSharedRunDispatcher _sharedRunDispatcher;
        private readonly IAiRunAdmissionController _admissionController;
        private readonly IAiRuntimeAdmissionReservationStore _reservationStore;
        private readonly IAiRuntimeInstanceRegistry _runtimeInstanceRegistry;
        private readonly IAiRuntimeScaleOutRequestPublisher _scaleOutPublisher;
        private readonly IAiTenantRuntimeSettingsProvider _tenantRuntimeSettingsProvider;
        private readonly IAiControlPlaneIdResolver _controlPlaneIdResolver;
        private readonly IExecutionContextAccessor _executionContextAccessor;
        private readonly IAiRuntimeRecoveryForensicsRecorder _forensicsRecorder;
        private readonly ILogger<AiSharedQueueDispatcher> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiSharedQueueDispatcher"/> class.
        /// </summary>
        public AiSharedQueueDispatcher(
            IAiSharedQueue sharedQueue,
            IAiSharedRunStore sharedRunStore,
            IAiSharedRunDispatcher sharedRunDispatcher,
            IAiRunAdmissionController admissionController,
            IAiRuntimeAdmissionReservationStore reservationStore,
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            IAiRuntimeScaleOutRequestPublisher scaleOutPublisher,
            IAiTenantRuntimeSettingsProvider tenantRuntimeSettingsProvider,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            IExecutionContextAccessor executionContextAccessor,
            ILogger<AiSharedQueueDispatcher> logger)
            : this(
                sharedQueue,
                sharedRunStore,
                sharedRunDispatcher,
                admissionController,
                reservationStore,
                runtimeInstanceRegistry,
                scaleOutPublisher,
                tenantRuntimeSettingsProvider,
                controlPlaneIdResolver,
                executionContextAccessor,
                logger,
                new NoopAiRuntimeRecoveryForensicsRecorder())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AiSharedQueueDispatcher"/> class.
        /// </summary>
        public AiSharedQueueDispatcher(
            IAiSharedQueue sharedQueue,
            IAiSharedRunStore sharedRunStore,
            IAiSharedRunDispatcher sharedRunDispatcher,
            IAiRunAdmissionController admissionController,
            IAiRuntimeAdmissionReservationStore reservationStore,
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            IAiRuntimeScaleOutRequestPublisher scaleOutPublisher,
            IAiTenantRuntimeSettingsProvider tenantRuntimeSettingsProvider,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            IExecutionContextAccessor executionContextAccessor,
            ILogger<AiSharedQueueDispatcher> logger,
            IAiRuntimeRecoveryForensicsRecorder forensicsRecorder)
        {
            _sharedQueue = sharedQueue ?? throw new ArgumentNullException(nameof(sharedQueue));
            _sharedRunStore = sharedRunStore ?? throw new ArgumentNullException(nameof(sharedRunStore));
            _sharedRunDispatcher = sharedRunDispatcher ?? throw new ArgumentNullException(nameof(sharedRunDispatcher));
            _admissionController = admissionController ?? throw new ArgumentNullException(nameof(admissionController));
            _reservationStore = reservationStore ?? throw new ArgumentNullException(nameof(reservationStore));
            _runtimeInstanceRegistry = runtimeInstanceRegistry ?? throw new ArgumentNullException(nameof(runtimeInstanceRegistry));
            _scaleOutPublisher = scaleOutPublisher ?? throw new ArgumentNullException(nameof(scaleOutPublisher));
            _tenantRuntimeSettingsProvider = tenantRuntimeSettingsProvider ?? throw new ArgumentNullException(nameof(tenantRuntimeSettingsProvider));
            _controlPlaneIdResolver = controlPlaneIdResolver ?? throw new ArgumentNullException(nameof(controlPlaneIdResolver));
            _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _forensicsRecorder = forensicsRecorder ?? throw new ArgumentNullException(nameof(forensicsRecorder));
        }

        /// <inheritdoc />
        public async Task<AiSharedQueueDispatchResult> DispatchNextAsync(
            AiSharedQueueDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.RuntimeInstanceId);

            var startedAtUtc = DateTimeOffset.UtcNow;
            string? reservedRuntimeInstanceId = null;
            string? reservedSharedRunId = null;

            _logger.LogDebug(
                "Shared queue dispatch started. PumpRuntimeInstanceId={PumpRuntimeInstanceId}, WorkerId={WorkerId}, TenantId={TenantId}, PipelineKey={PipelineKey}, ClaimTtlMs={ClaimTtlMs}, CorrelationId={CorrelationId}",
                request.RuntimeInstanceId,
                request.WorkerId,
                request.TenantId,
                request.PipelineKey,
                request.ClaimTtl.TotalMilliseconds,
                request.CorrelationId);

            try
            {
                var requestControlPlaneId =
                    await ResolveControlPlaneIdAsync(
                            requestedControlPlaneId: null,
                            metadata: request.Metadata,
                            source: "shared-queue-dispatcher-claim",
                            cancellationToken)
                        .ConfigureAwait(false);

                var queueItem = await _sharedQueue
                    .ClaimNextAsync(
                        new AiSharedQueueClaimRequest
                        {
                            ControlPlaneId = requestControlPlaneId,
                            RuntimeInstanceId = request.RuntimeInstanceId,
                            WorkerId = request.WorkerId,
                            TenantId = request.TenantId,
                            PipelineKey = request.PipelineKey,
                            ClaimTtl = request.ClaimTtl,
                            CorrelationId = request.CorrelationId,
                            Reason = request.Reason ?? "Claimed for shared queue dispatch.",
                            Metadata = request.Metadata
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                if (queueItem is null)
                {
                    var completedAtUtc = DateTimeOffset.UtcNow;

                    _logger.LogDebug(
                        "Shared queue dispatch found no pending item. PumpRuntimeInstanceId={PumpRuntimeInstanceId}, WorkerId={WorkerId}, TenantId={TenantId}, PipelineKey={PipelineKey}, DurationMs={DurationMs}",
                        request.RuntimeInstanceId,
                        request.WorkerId,
                        request.TenantId,
                        request.PipelineKey,
                        CalculateDurationMs(startedAtUtc, completedAtUtc));

                    return new AiSharedQueueDispatchResult
                    {
                        Success = false,
                        NoItemAvailable = true,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        Message = "No pending shared queue item is available.",
                        StartedAtUtc = startedAtUtc,
                        CompletedAtUtc = completedAtUtc,
                        DurationMs = CalculateDurationMs(startedAtUtc, completedAtUtc)
                    };
                }

                _logger.LogInformation(
                    "Shared queue item claimed. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, ClaimToken={ClaimToken}, PumpRuntimeInstanceId={PumpRuntimeInstanceId}, WorkerId={WorkerId}, TenantId={TenantId}, PipelineKey={PipelineKey}",
                    queueItem.SharedRunId,
                    queueItem.ControlPlaneId,
                    queueItem.ClaimToken,
                    request.RuntimeInstanceId,
                    request.WorkerId,
                    request.TenantId,
                    request.PipelineKey);

                var sharedRun = await _sharedRunStore
                    .GetAsync(
                        queueItem.SharedRunId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (sharedRun is null)
                {
                    _logger.LogWarning(
                        "Shared run record was not found after queue claim. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, ClaimToken={ClaimToken}, PumpRuntimeInstanceId={PumpRuntimeInstanceId}",
                        queueItem.SharedRunId,
                        queueItem.ControlPlaneId,
                        queueItem.ClaimToken,
                        request.RuntimeInstanceId);

                    await RequeueBestEffortAsync(
                            queueItem,
                            "Shared run record was not found.",
                            cancellationToken)
                        .ConfigureAwait(false);

                    var completedAtUtc = DateTimeOffset.UtcNow;

                    return new AiSharedQueueDispatchResult
                    {
                        Success = false,
                        SharedRunId = queueItem.SharedRunId,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        QueueItem = queueItem,
                        Message = "Shared queue item was claimed but the shared run record was not found.",
                        FailureReason = "Shared run record was not found.",
                        StartedAtUtc = startedAtUtc,
                        CompletedAtUtc = completedAtUtc,
                        DurationMs = CalculateDurationMs(startedAtUtc, completedAtUtc)
                    };
                }

                var baseOperationMetadata =
                    MergeMetadata(
                        sharedRun.Metadata,
                        queueItem.Metadata,
                        request.Metadata);

                var controlPlaneId =
                    await ResolveControlPlaneIdAsync(
                            requestedControlPlaneId: FirstNonEmpty(
                                queueItem.ControlPlaneId,
                                sharedRun.ControlPlaneId),
                            metadata: baseOperationMetadata,
                            source: "shared-queue-dispatcher-operation",
                            cancellationToken)
                        .ConfigureAwait(false);

                var controlPlaneMetadata =
                    await _controlPlaneIdResolver
                        .ResolveMetadataAsync(
                            new AiControlPlaneIdResolutionRequest
                            {
                                RequestedControlPlaneId = controlPlaneId,
                                Metadata = baseOperationMetadata,
                                Source = "shared-queue-dispatcher-operation-metadata",
                                AllowGeneratedFallback = false
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                var operationMetadata =
                    MergeMetadata(
                        baseOperationMetadata,
                        controlPlaneMetadata);

                _logger.LogDebug(
                    "Shared run record loaded. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, TenantId={TenantId}, PipelineKey={PipelineKey}, AssignedRuntimeInstanceId={AssignedRuntimeInstanceId}, Status={Status}",
                    sharedRun.SharedRunId,
                    controlPlaneId,
                    sharedRun.ExecutionContextSnapshot.TenantId,
                    sharedRun.PipelineKey,
                    sharedRun.AssignedRuntimeInstanceId,
                    sharedRun.Status);

                var previousExecutionContext =
                    _executionContextAccessor.Current;

                try
                {
                    _executionContextAccessor.Set(
                        CreateExecutionContext(
                            sharedRun.ExecutionContextSnapshot));

                    _logger.LogDebug(
                        "Shared queue dispatcher restored execution context from shared run snapshot. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, TenantId={TenantId}, TenantGroupId={TenantGroupId}, ContextKey={ContextKey}",
                        sharedRun.SharedRunId,
                        controlPlaneId,
                        sharedRun.ExecutionContextSnapshot.TenantId,
                        sharedRun.ExecutionContextSnapshot.TenantGroupId,
                        sharedRun.ExecutionContextSnapshot.ContextKey);

                    var safePreferredRuntimeInstanceId =
                        await ResolveSafePreferredRuntimeInstanceIdAsync(
                                sharedRun.AssignedRuntimeInstanceId,
                                operationMetadata,
                                sharedRun.SharedRunId,
                                controlPlaneId,
                                cancellationToken)
                            .ConfigureAwait(false);

                    var admissionDecision = await _admissionController
                        .AdmitAsync(
                            new AiRunAdmissionRequest
                            {
                                RunRequest = sharedRun.RunRequest,
                                RunId = sharedRun.SharedRunId,
                                TenantId = sharedRun.ExecutionContextSnapshot.TenantId,
                                PipelineKey = sharedRun.PipelineKey,
                                PreferredRuntimeInstanceId = safePreferredRuntimeInstanceId,
                                CorrelationId = request.CorrelationId ?? sharedRun.CorrelationId,
                                RequestedBy = request.RequestedBy,
                                Source = request.Source,
                                Reason = request.Reason ?? "Selecting runtime instance for shared queue dispatch.",
                                Metadata = operationMetadata
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                    _logger.LogInformation(
                        "Shared queue admission decision received. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, DecisionType={DecisionType}, AssignedRuntimeInstanceId={AssignedRuntimeInstanceId}, Reason={Reason}",
                        sharedRun.SharedRunId,
                        controlPlaneId,
                        admissionDecision.DecisionType,
                        admissionDecision.AssignedRuntimeInstanceId,
                        admissionDecision.Reason);

                    if (admissionDecision.DecisionType == AiRunAdmissionDecisionType.RequestScaleOut)
                    {
                        await PublishScaleOutRequestAsync(
                                sharedRun,
                                admissionDecision,
                                operationMetadata,
                                cancellationToken)
                            .ConfigureAwait(false);

                        await RequeueBestEffortAsync(
                                queueItem,
                                SharedQueueRedispatchReplacementReason,
                                cancellationToken)
                            .ConfigureAwait(false);

                        var completedAtUtc = DateTimeOffset.UtcNow;

                        return new AiSharedQueueDispatchResult
                        {
                            Success = false,
                            SharedRunId = queueItem.SharedRunId,
                            RuntimeInstanceId = request.RuntimeInstanceId,
                            QueueItem = queueItem,
                            SharedRun = sharedRun,
                            Message = "Shared queue item was requeued after publishing a replacement scale-out request.",
                            FailureReason = "scale-out-requested",
                            StartedAtUtc = startedAtUtc,
                            CompletedAtUtc = completedAtUtc,
                            DurationMs = CalculateDurationMs(startedAtUtc, completedAtUtc),
                            Diagnostics = new[]
                            {
                        admissionDecision.Reason ?? SharedQueueRedispatchReplacementReason
                    }
                        };
                    }

                    if (admissionDecision.DecisionType != AiRunAdmissionDecisionType.AssignToInstance ||
                        string.IsNullOrWhiteSpace(admissionDecision.AssignedRuntimeInstanceId))
                    {
                        _logger.LogWarning(
                            "Shared queue dispatch could not assign runtime instance. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, DecisionType={DecisionType}, Reason={Reason}",
                            sharedRun.SharedRunId,
                            controlPlaneId,
                            admissionDecision.DecisionType,
                            admissionDecision.Reason);

                        await RequeueBestEffortAsync(
                                queueItem,
                                admissionDecision.Reason ?? "No runtime instance available for shared queue dispatch.",
                                cancellationToken)
                            .ConfigureAwait(false);

                        var completedAtUtc = DateTimeOffset.UtcNow;

                        return new AiSharedQueueDispatchResult
                        {
                            Success = false,
                            SharedRunId = queueItem.SharedRunId,
                            RuntimeInstanceId = request.RuntimeInstanceId,
                            QueueItem = queueItem,
                            SharedRun = sharedRun,
                            Message = "Shared queue item could not be dispatched because admission did not assign a runtime instance.",
                            FailureReason = admissionDecision.Reason,
                            StartedAtUtc = startedAtUtc,
                            CompletedAtUtc = completedAtUtc,
                            DurationMs = CalculateDurationMs(startedAtUtc, completedAtUtc),
                            Diagnostics = new[]
                            {
                        admissionDecision.Reason ?? "Admission did not assign a runtime instance."
                    }
                        };
                    }

                    var targetRuntimeInstanceId =
                        admissionDecision.AssignedRuntimeInstanceId;

                    if (IsRecoveryFailedRuntimeInstance(
                            operationMetadata,
                            targetRuntimeInstanceId))
                    {
                        _logger.LogWarning(
                            "Shared queue dispatch rejected the failed runtime instance selected by admission during recovery redispatch. Publishing replacement scale-out request and requeuing item. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}",
                            sharedRun.SharedRunId,
                            controlPlaneId,
                            targetRuntimeInstanceId);

                        await PublishScaleOutRequestAsync(
                                sharedRun,
                                admissionDecision,
                                operationMetadata,
                                cancellationToken)
                            .ConfigureAwait(false);

                        await RequeueBestEffortAsync(
                                queueItem,
                                "Selected runtime instance is the failed runtime for this recovery redispatch.",
                                cancellationToken)
                            .ConfigureAwait(false);

                        var completedAtUtc = DateTimeOffset.UtcNow;

                        return new AiSharedQueueDispatchResult
                        {
                            Success = false,
                            SharedRunId = queueItem.SharedRunId,
                            RuntimeInstanceId = targetRuntimeInstanceId,
                            QueueItem = queueItem,
                            SharedRun = sharedRun,
                            Message = "Shared queue item was requeued because admission selected the failed runtime during recovery redispatch.",
                            FailureReason = "recovery-selected-failed-runtime-instance",
                            StartedAtUtc = startedAtUtc,
                            CompletedAtUtc = completedAtUtc,
                            DurationMs = CalculateDurationMs(startedAtUtc, completedAtUtc),
                            Diagnostics = new[]
                            {
                                "Selected runtime instance is the failed runtime for this recovery redispatch.",
                                "Replacement scale-out request was published."
                            }
                        };
                    }

                    if (!await IsRuntimeInstanceRoutableAsync(targetRuntimeInstanceId, cancellationToken).ConfigureAwait(false))
                    {
                        _logger.LogWarning(
                            "Shared queue dispatch rejected unsafe runtime instance selected by admission. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}",
                            sharedRun.SharedRunId,
                            controlPlaneId,
                            targetRuntimeInstanceId);

                        await RequeueBestEffortAsync(
                                queueItem,
                                "Selected runtime instance is not routable.",
                                cancellationToken)
                            .ConfigureAwait(false);

                        var completedAtUtc = DateTimeOffset.UtcNow;

                        return new AiSharedQueueDispatchResult
                        {
                            Success = false,
                            SharedRunId = queueItem.SharedRunId,
                            RuntimeInstanceId = targetRuntimeInstanceId,
                            QueueItem = queueItem,
                            SharedRun = sharedRun,
                            Message = "Shared queue item was requeued because admission selected an unsafe runtime instance.",
                            FailureReason = "runtime-instance-not-routable",
                            StartedAtUtc = startedAtUtc,
                            CompletedAtUtc = completedAtUtc,
                            DurationMs = CalculateDurationMs(startedAtUtc, completedAtUtc),
                            Diagnostics = new[]
                            {
                        "Selected runtime instance is not routable."
                    }
                        };
                    }

                    _logger.LogDebug(
                        "Reserving temporary admission capacity. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, RunCount={RunCount}",
                        sharedRun.SharedRunId,
                        controlPlaneId,
                        targetRuntimeInstanceId,
                        1);

                    await _reservationStore
                        .ReserveAsync(
                            targetRuntimeInstanceId,
                            runCount: 1,
                            cancellationToken)
                        .ConfigureAwait(false);

                    reservedRuntimeInstanceId =
                        targetRuntimeInstanceId;

                    reservedSharedRunId =
                        sharedRun.SharedRunId;

                    _logger.LogInformation(
                        "Temporary admission capacity reserved. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, RunCount={RunCount}",
                        sharedRun.SharedRunId,
                        controlPlaneId,
                        targetRuntimeInstanceId,
                        1);

                    await RecordReplacementRuntimeSelectedForensicsAsync(
                            queueItem,
                            sharedRun,
                            operationMetadata,
                            targetRuntimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                    try
                    {
                        _logger.LogInformation(
                            "Shared run dispatch to runtime instance started. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, ClaimToken={ClaimToken}",
                            sharedRun.SharedRunId,
                            controlPlaneId,
                            targetRuntimeInstanceId,
                            queueItem.ClaimToken);

                        AiSharedRunDispatchResult dispatchResult;

                        try
                        {
                            dispatchResult = await _sharedRunDispatcher
                                .DispatchAsync(
                                    new AiSharedRunDispatchRequest
                                    {
                                        SharedRun = sharedRun,
                                        QueueItem = queueItem,
                                        RuntimeInstanceId = targetRuntimeInstanceId,
                                        ClaimToken = queueItem.ClaimToken,
                                        CorrelationId = request.CorrelationId ?? sharedRun.CorrelationId,
                                        RequestedBy = request.RequestedBy,
                                        Source = request.Source,
                                        Reason = request.Reason ?? "Dispatching claimed shared queue item.",
                                        Metadata = operationMetadata
                                    },
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            _logger.LogWarning(
                                exception,
                                "Shared run dispatch threw an exception. Persisting failure metadata and requeuing shared queue item. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, ClaimToken={ClaimToken}",
                                sharedRun.SharedRunId,
                                controlPlaneId,
                                targetRuntimeInstanceId,
                                queueItem.ClaimToken);

                            var failedSharedRun = await _sharedRunStore
                                .MarkDispatchFailedAsync(
                                    sharedRun.SharedRunId,
                                    targetRuntimeInstanceId,
                                    exception.Message,
                                    exception.Message,
                                    cancellationToken)
                                .ConfigureAwait(false);

                            await RequeueBestEffortAsync(
                                    queueItem,
                                    exception.Message,
                                    cancellationToken)
                                .ConfigureAwait(false);

                            var completedAtUtc = DateTimeOffset.UtcNow;

                            return new AiSharedQueueDispatchResult
                            {
                                Success = false,
                                SharedRunId = queueItem.SharedRunId,
                                RuntimeInstanceId = targetRuntimeInstanceId,
                                QueueItem = queueItem,
                                SharedRun = failedSharedRun ?? sharedRun,
                                Message = "Shared queue item dispatch threw an exception and was requeued.",
                                FailureReason = exception.Message,
                                StartedAtUtc = startedAtUtc,
                                CompletedAtUtc = completedAtUtc,
                                DurationMs = CalculateDurationMs(startedAtUtc, completedAtUtc),
                                Diagnostics = new[] { exception.Message }
                            };
                        }

                        _logger.LogInformation(
                            "Shared run dispatch result received. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, Success={Success}, LocalRunId={LocalRunId}, ExecutionId={ExecutionId}, FailureReason={FailureReason}",
                            sharedRun.SharedRunId,
                            controlPlaneId,
                            targetRuntimeInstanceId,
                            dispatchResult.Success,
                            dispatchResult.LocalRunId,
                            dispatchResult.ExecutionId,
                            dispatchResult.FailureReason);

                        if (!dispatchResult.Success)
                        {
                            _logger.LogWarning(
                                "Shared run dispatch failed. Persisting failure metadata and requeuing shared queue item. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, FailureReason={FailureReason}",
                                sharedRun.SharedRunId,
                                controlPlaneId,
                                targetRuntimeInstanceId,
                                dispatchResult.FailureReason);

                            var failedSharedRun = await _sharedRunStore
                                .MarkDispatchFailedAsync(
                                    sharedRun.SharedRunId,
                                    targetRuntimeInstanceId,
                                    dispatchResult.FailureReason,
                                    dispatchResult.Message,
                                    cancellationToken)
                                .ConfigureAwait(false);

                            await RequeueBestEffortAsync(
                                    queueItem,
                                    dispatchResult.FailureReason ?? "Shared run dispatch failed.",
                                    cancellationToken)
                                .ConfigureAwait(false);

                            var completedAtUtc = DateTimeOffset.UtcNow;

                            return new AiSharedQueueDispatchResult
                            {
                                Success = false,
                                SharedRunId = queueItem.SharedRunId,
                                RuntimeInstanceId = targetRuntimeInstanceId,
                                QueueItem = queueItem,
                                SharedRun = failedSharedRun ?? sharedRun,
                                DispatchResult = dispatchResult,
                                Message = "Shared queue item dispatch failed and was requeued.",
                                FailureReason = dispatchResult.FailureReason,
                                StartedAtUtc = startedAtUtc,
                                CompletedAtUtc = completedAtUtc,
                                DurationMs = CalculateDurationMs(startedAtUtc, completedAtUtc),
                                Diagnostics = dispatchResult.Diagnostics
                            };
                        }

                        var dispatchedQueueItem = await _sharedQueue
                            .MarkDispatchedAsync(
                                queueItem.SharedRunId,
                                queueItem.ClaimToken!,
                                dispatchResult.Message,
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (dispatchedQueueItem is null)
                        {
                            _logger.LogWarning(
                                "Shared queue item could not be marked as dispatched before marking the shared run as dispatched. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, ClaimToken={ClaimToken}, LocalRunId={LocalRunId}, ExecutionId={ExecutionId}",
                                queueItem.SharedRunId,
                                controlPlaneId,
                                targetRuntimeInstanceId,
                                queueItem.ClaimToken,
                                dispatchResult.LocalRunId,
                                dispatchResult.ExecutionId);

                            var failedSharedRun = await _sharedRunStore
                                .MarkDispatchFailedAsync(
                                    sharedRun.SharedRunId,
                                    targetRuntimeInstanceId,
                                    "Shared queue item could not be marked as dispatched.",
                                    "Shared queue item could not be marked as dispatched before shared run dispatch persistence.",
                                    cancellationToken)
                                .ConfigureAwait(false);

                            var completedAtUtc = DateTimeOffset.UtcNow;

                            return new AiSharedQueueDispatchResult
                            {
                                Success = false,
                                SharedRunId = queueItem.SharedRunId,
                                RuntimeInstanceId = targetRuntimeInstanceId,
                                QueueItem = queueItem,
                                SharedRun = failedSharedRun ?? sharedRun,
                                DispatchResult = dispatchResult,
                                Message = "Shared queue item could not be marked as dispatched, so the shared run record was not marked as dispatched.",
                                FailureReason = "shared-queue-mark-dispatched-rejected",
                                StartedAtUtc = startedAtUtc,
                                CompletedAtUtc = completedAtUtc,
                                DurationMs = CalculateDurationMs(startedAtUtc, completedAtUtc),
                                Diagnostics = new[]
                                {
                                    "Shared queue item could not be marked as dispatched.",
                                    $"SharedRunId='{queueItem.SharedRunId}'",
                                    $"RuntimeInstanceId='{targetRuntimeInstanceId}'",
                                    $"ClaimTokenPresent='{!string.IsNullOrWhiteSpace(queueItem.ClaimToken)}'",
                                    $"LocalRunId='{dispatchResult.LocalRunId}'",
                                    $"ExecutionId='{dispatchResult.ExecutionId}'"
                                }
                            };
                        }

                        _logger.LogDebug(
                            "Shared queue item marked as dispatched before shared run persistence. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, ClaimToken={ClaimToken}",
                            queueItem.SharedRunId,
                            controlPlaneId,
                            targetRuntimeInstanceId,
                            queueItem.ClaimToken);

                        var dispatchedRun = await _sharedRunStore
                            .MarkDispatchedAsync(
                                sharedRun.SharedRunId,
                                targetRuntimeInstanceId,
                                dispatchResult.LocalRunId,
                                dispatchResult.ExecutionId,
                                dispatchResult.Message,
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (dispatchedRun is null)
                        {
                            _logger.LogWarning(
                                "Shared run record could not be marked as dispatched after the shared queue item was marked as dispatched. Requeueing dispatched queue item for safety. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, LocalRunId={LocalRunId}, ExecutionId={ExecutionId}",
                                sharedRun.SharedRunId,
                                controlPlaneId,
                                targetRuntimeInstanceId,
                                dispatchResult.LocalRunId,
                                dispatchResult.ExecutionId);

                            await RequeueDispatchedBestEffortAsync(
                                    dispatchedQueueItem,
                                    "Shared run record could not be marked as dispatched after queue dispatch persistence.",
                                    operationMetadata,
                                    cancellationToken)
                                .ConfigureAwait(false);

                            var completedAtUtc = DateTimeOffset.UtcNow;

                            return new AiSharedQueueDispatchResult
                            {
                                Success = false,
                                SharedRunId = queueItem.SharedRunId,
                                RuntimeInstanceId = targetRuntimeInstanceId,
                                QueueItem = dispatchedQueueItem,
                                SharedRun = sharedRun,
                                DispatchResult = dispatchResult,
                                Message = "Shared queue item was requeued because the shared run record could not be marked as dispatched.",
                                FailureReason = "shared-run-mark-dispatched-rejected",
                                StartedAtUtc = startedAtUtc,
                                CompletedAtUtc = completedAtUtc,
                                DurationMs = CalculateDurationMs(startedAtUtc, completedAtUtc),
                                Diagnostics = new[]
                                {
                                    "Shared run record could not be marked as dispatched."
                                }
                            };
                        }

                        _logger.LogInformation(
                            "Shared run record marked as dispatched. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, LocalRunId={LocalRunId}, ExecutionId={ExecutionId}",
                            sharedRun.SharedRunId,
                            controlPlaneId,
                            targetRuntimeInstanceId,
                            dispatchResult.LocalRunId,
                            dispatchResult.ExecutionId);

                        var verifyDispatchedRun = await _sharedRunStore
                            .GetAsync(
                                sharedRun.SharedRunId,
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (verifyDispatchedRun is null ||
                            !string.Equals(
                                verifyDispatchedRun.AssignedRuntimeInstanceId,
                                targetRuntimeInstanceId,
                                StringComparison.Ordinal))
                        {
                            _logger.LogWarning(
                                "Shared run dispatch assignment verification failed after queue dispatch persistence. Requeueing dispatched queue item for safety. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, ExpectedRuntimeInstanceId={ExpectedRuntimeInstanceId}, ActualRuntimeInstanceId={ActualRuntimeInstanceId}, LocalRunId={LocalRunId}, ExecutionId={ExecutionId}",
                                sharedRun.SharedRunId,
                                controlPlaneId,
                                targetRuntimeInstanceId,
                                verifyDispatchedRun?.AssignedRuntimeInstanceId,
                                dispatchResult.LocalRunId,
                                dispatchResult.ExecutionId);

                            await RequeueDispatchedBestEffortAsync(
                                    dispatchedQueueItem,
                                    "Shared run dispatch assignment verification failed after queue dispatch persistence.",
                                    operationMetadata,
                                    cancellationToken)
                                .ConfigureAwait(false);

                            var completedAtUtc = DateTimeOffset.UtcNow;

                            return new AiSharedQueueDispatchResult
                            {
                                Success = false,
                                SharedRunId = queueItem.SharedRunId,
                                RuntimeInstanceId = targetRuntimeInstanceId,
                                QueueItem = dispatchedQueueItem,
                                SharedRun = verifyDispatchedRun ?? sharedRun,
                                DispatchResult = dispatchResult,
                                Message = "Shared queue item was requeued because shared run dispatch assignment verification failed.",
                                FailureReason = "shared-run-dispatch-assignment-verification-failed",
                                StartedAtUtc = startedAtUtc,
                                CompletedAtUtc = completedAtUtc,
                                DurationMs = CalculateDurationMs(startedAtUtc, completedAtUtc),
                                Diagnostics = new[]
                                {
                                    $"ExpectedRuntimeInstanceId='{targetRuntimeInstanceId}', ActualRuntimeInstanceId='{verifyDispatchedRun?.AssignedRuntimeInstanceId}'"
                                }
                            };
                        }

                        var completedAtUtcSuccess =
                            DateTimeOffset.UtcNow;

                        return new AiSharedQueueDispatchResult
                        {
                            Success = true,
                            SharedRunId = queueItem.SharedRunId,
                            RuntimeInstanceId = targetRuntimeInstanceId,
                            QueueItem = dispatchedQueueItem,
                            SharedRun = dispatchedRun,
                            DispatchResult = dispatchResult,
                            Message = "Shared queue item dispatched successfully.",
                            StartedAtUtc = startedAtUtc,
                            CompletedAtUtc = completedAtUtcSuccess,
                            DurationMs = CalculateDurationMs(startedAtUtc, completedAtUtcSuccess),
                            Diagnostics = dispatchResult.Diagnostics
                        };
                    }
                    finally
                    {
                        await ReleaseReservationBestEffortAsync(
                                reservedSharedRunId,
                                reservedRuntimeInstanceId,
                                CancellationToken.None)
                            .ConfigureAwait(false);

                        reservedSharedRunId = null;
                        reservedRuntimeInstanceId = null;
                    }
                }
                finally
                {
                    RestoreExecutionContext(
                        previousExecutionContext);
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Shared queue dispatch failed with exception. PumpRuntimeInstanceId={PumpRuntimeInstanceId}, WorkerId={WorkerId}, ReservedSharedRunId={ReservedSharedRunId}, ReservedRuntimeInstanceId={ReservedRuntimeInstanceId}",
                    request.RuntimeInstanceId,
                    request.WorkerId,
                    reservedSharedRunId,
                    reservedRuntimeInstanceId);

                await ReleaseReservationBestEffortAsync(
                        reservedSharedRunId,
                        reservedRuntimeInstanceId,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                var completedAtUtc =
                    DateTimeOffset.UtcNow;

                return new AiSharedQueueDispatchResult
                {
                    Success = false,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    Message = "Shared queue dispatch failed.",
                    FailureReason = exception.Message,
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = completedAtUtc,
                    DurationMs = CalculateDurationMs(startedAtUtc, completedAtUtc),
                    Diagnostics = new[] { exception.Message }
                };
            }
        }

        /// <summary>
        /// Records that a replacement runtime instance was selected for a recovered shared run.
        /// </summary>
        /// <param name="queueItem">The claimed shared queue item.</param>
        /// <param name="sharedRun">The shared run record.</param>
        /// <param name="metadata">The merged operation metadata.</param>
        /// <param name="replacementRuntimeInstanceId">The replacement runtime instance identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when the forensics event has been recorded.</returns>
        private async Task RecordReplacementRuntimeSelectedForensicsAsync(
            AiSharedQueueItem queueItem,
            AiSharedRunRecord sharedRun,
            IReadOnlyDictionary<string, string> metadata,
            string replacementRuntimeInstanceId,
            CancellationToken cancellationToken)
        {
            if (!TryResolveRecoveryForensicsId(
                    queueItem,
                    sharedRun,
                    metadata,
                    out var forensicsId,
                    out var executionId,
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
                            AiRuntimeRecoveryForensicsEventType.ReplacementRuntimeSelected,
                            replacementRuntimeInstanceId),
                        ForensicsId = forensicsId,
                        TimestampUtc = DateTimeOffset.UtcNow,
                        EventType = AiRuntimeRecoveryForensicsEventType.ReplacementRuntimeSelected,
                        Outcome = "selected",
                        Reason = "replacement-runtime-selected-for-recovery-redispatch",
                        ExecutionId = executionId,
                        SharedRunId = sharedRun.SharedRunId,
                        RuntimeInstanceId = replacementRuntimeInstanceId,
                        Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["tenant.id"] = sharedRun.ExecutionContextSnapshot.TenantId ?? string.Empty,
                            ["tenant.group.id"] = sharedRun.ExecutionContextSnapshot.TenantGroupId ?? string.Empty,
                            ["replacement.runtimeInstanceId"] = replacementRuntimeInstanceId,
                            ["replacement.executionId"] = executionId ?? string.Empty,
                            ["failed.runtimeInstanceId"] = ResolveMetadataValue(metadata, RecoveryFailedRuntimeInstanceIdMetadataKey),
                            ["failed.localRunId"] = failedLocalRunId ?? string.Empty,
                            ["queue.claimToken"] = queueItem.ClaimToken ?? string.Empty,
                            ["resume.contextKey"] = sharedRun.ExecutionContextSnapshot.ContextKey ?? string.Empty
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Tries to resolve recovery forensics identity from dispatch metadata.
        /// </summary>
        /// <param name="queueItem">The claimed queue item.</param>
        /// <param name="sharedRun">The shared run record.</param>
        /// <param name="metadata">The merged operation metadata.</param>
        /// <param name="forensicsId">The resolved forensics identifier.</param>
        /// <param name="executionId">The resolved durable execution identifier.</param>
        /// <param name="failedLocalRunId">The failed local run identifier.</param>
        /// <returns><c>true</c> when the recovery forensics identity can be resolved; otherwise, <c>false</c>.</returns>
        private static bool TryResolveRecoveryForensicsId(
            AiSharedQueueItem queueItem,
            AiSharedRunRecord sharedRun,
            IReadOnlyDictionary<string, string> metadata,
            out string forensicsId,
            out string? executionId,
            out string? failedLocalRunId)
        {
            if (TryGetMetadataValue(
                    metadata,
                    RecoveryForensicsIdMetadataKey,
                    out var explicitForensicsId))
            {
                forensicsId = explicitForensicsId;
                executionId = ResolveMetadataValue(metadata, RecoveryFailedExecutionIdMetadataKey);
                failedLocalRunId = ResolveMetadataValue(metadata, RecoveryFailedLocalRunIdMetadataKey);

                return true;
            }

            executionId =
                ResolveMetadataValue(metadata, RecoveryFailedExecutionIdMetadataKey);

            failedLocalRunId =
                ResolveMetadataValue(metadata, RecoveryFailedLocalRunIdMetadataKey);

            if (string.IsNullOrWhiteSpace(executionId) ||
                string.IsNullOrWhiteSpace(failedLocalRunId))
            {
                forensicsId = string.Empty;
                return false;
            }

            forensicsId = string.Join(
                ":",
                "runtime-recovery",
                executionId,
                sharedRun.SharedRunId,
                failedLocalRunId);

            return !string.IsNullOrWhiteSpace(queueItem.SharedRunId);
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
        /// Publishes a replacement scale-out request for a shared run when admission requests more runtime capacity during shared queue dispatch.
        /// </summary>
        /// <param name="sharedRun">The shared run record.</param>
        /// <param name="admissionDecision">The scale-out admission decision.</param>
        /// <param name="operationMetadata">The merged dispatch metadata, including queue item recovery metadata.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        private async Task PublishScaleOutRequestAsync(
            AiSharedRunRecord sharedRun,
            AiRunAdmissionDecision admissionDecision,
            IReadOnlyDictionary<string, string> operationMetadata,
            CancellationToken cancellationToken)
        {
            var tenantRuntimeSettings =
                admissionDecision.TenantRuntimeSettings ??
                _tenantRuntimeSettingsProvider.GetSettings(
                    sharedRun.ExecutionContextSnapshot.TenantId,
                    sharedRun.ExecutionContextSnapshot.TenantGroupId);

            var tenantId =
                !string.IsNullOrWhiteSpace(admissionDecision.TenantId)
                    ? admissionDecision.TenantId
                    : tenantRuntimeSettings.TenantId ?? sharedRun.ExecutionContextSnapshot.TenantId;

            var tenantGroupId =
                !string.IsNullOrWhiteSpace(admissionDecision.TenantGroupId)
                    ? admissionDecision.TenantGroupId
                    : tenantRuntimeSettings.TenantGroupId ?? sharedRun.ExecutionContextSnapshot.TenantGroupId;

            var metadata =
                CreateScaleOutRedispatchMetadata(
                    sharedRun,
                    operationMetadata);

            var publishResult =
                await _scaleOutPublisher
                    .PublishAsync(
                        new AiRuntimeScaleOutRequest
                        {
                            SharedRun = sharedRun,
                            SharedRunId = sharedRun.SharedRunId,
                            ExecutionContextSnapshot = sharedRun.ExecutionContextSnapshot,

                            TenantId = tenantId,
                            TenantGroupId = tenantGroupId,
                            PipelineKey = sharedRun.PipelineKey,

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

                            CorrelationId = sharedRun.CorrelationId,
                            RequestedBy = sharedRun.RequestedBy,
                            Source = sharedRun.Source,
                            Reason = SharedQueueRedispatchReplacementReason,
                            Metadata = metadata
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            _logger.LogInformation(
                "Shared queue dispatch published replacement scale-out request. SharedRunId={SharedRunId}, ScaleOutRequestId={ScaleOutRequestId}, TenantId={TenantId}, TenantGroupId={TenantGroupId}, PipelineKey={PipelineKey}, MaxRuntimeInstances={MaxRuntimeInstances}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}, Message={Message}",
                sharedRun.SharedRunId,
                publishResult.ScaleOutRequestId,
                tenantId,
                tenantGroupId,
                sharedRun.PipelineKey,
                tenantRuntimeSettings.MaxRuntimeInstances,
                tenantRuntimeSettings.RuntimeInstanceIdPrefix,
                publishResult.Message);
        }

        /// <summary>
        /// Creates metadata for replacement scale-out requests emitted from shared queue redispatch.
        /// </summary>
        /// <param name="sharedRun">The shared run record.</param>
        /// <param name="operationMetadata">The merged dispatch metadata to propagate to the replacement scale-out request.</param>
        /// <returns>The scale-out metadata.</returns>
        private static IReadOnlyDictionary<string, string> CreateScaleOutRedispatchMetadata(
            AiSharedRunRecord sharedRun,
            IReadOnlyDictionary<string, string> operationMetadata)
        {
            var metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            CopyScaleOutRedispatchMetadata(
                metadata,
                sharedRun.Metadata);

            CopyScaleOutRedispatchMetadata(
                metadata,
                operationMetadata);

            metadata[ScaleOutIntentMetadataKey] =
                ScaleOutIntentSharedQueueRedispatchReplacement;

            metadata[ScaleOutRequestIdMetadataKey] =
                $"scale-out-redispatch-{sharedRun.SharedRunId}-{Guid.NewGuid():N}";

            if (TryGetMetadataValue(
                    operationMetadata,
                    RecoveryFailedRuntimeInstanceIdMetadataKey,
                    out var failedRuntimeInstanceId))
            {
                metadata["scaleout.excludedRuntimeInstanceId"] =
                    failedRuntimeInstanceId;

                metadata["scaleout.replacementForRuntimeInstanceId"] =
                    failedRuntimeInstanceId;

                metadata["recovery.replacement"] =
                    "true";
            }

            return metadata;
        }

        /// <summary>
        /// Copies metadata into the replacement scale-out metadata while removing stale runtime assignment keys.
        /// </summary>
        /// <param name="target">The target metadata dictionary.</param>
        /// <param name="source">The source metadata dictionary.</param>
        private static void CopyScaleOutRedispatchMetadata(
            IDictionary<string, string> target,
            IReadOnlyDictionary<string, string> source)
        {
            foreach (var item in source)
            {
                if (string.IsNullOrWhiteSpace(item.Key) ||
                    IsStaleRuntimeAssignmentMetadataKey(item.Key))
                {
                    continue;
                }

                target[item.Key] =
                    item.Value ?? string.Empty;
            }
        }

        /// <summary>
        /// Determines whether a metadata key carries a stale runtime assignment that must not be propagated to replacement scale-out.
        /// </summary>
        /// <param name="key">The metadata key.</param>
        /// <returns><c>true</c> when the key must be removed from replacement scale-out metadata; otherwise, <c>false</c>.</returns>
        private static bool IsStaleRuntimeAssignmentMetadataKey(
            string key)
        {
            return string.Equals(key, "scaleOutRuntimeInstanceId", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "scaleout.runtimeInstanceId", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "runtimeInstanceId", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "runtime.instance.id", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "host.runtimeInstanceId", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "transport.runtimeInstanceId", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves a preferred runtime instance only when it is still routable and not the failed runtime for recovery redispatch.
        /// </summary>
        /// <param name="preferredRuntimeInstanceId">The preferred runtime instance id.</param>
        /// <param name="metadata">The merged dispatch metadata.</param>
        /// <param name="sharedRunId">The shared run id.</param>
        /// <param name="controlPlaneId">The control-plane id.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The preferred runtime instance id when safe; otherwise, <c>null</c>.</returns>
        private async Task<string?> ResolveSafePreferredRuntimeInstanceIdAsync(
            string? preferredRuntimeInstanceId,
            IReadOnlyDictionary<string, string> metadata,
            string sharedRunId,
            string controlPlaneId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(preferredRuntimeInstanceId))
            {
                return null;
            }

            if (TryGetMetadataValue(
                    metadata,
                    RecoveryFailedRuntimeInstanceIdMetadataKey,
                    out var failedRuntimeInstanceId) &&
                string.Equals(
                    preferredRuntimeInstanceId,
                    failedRuntimeInstanceId,
                    StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Ignoring preferred runtime instance because it is the failed runtime instance for this recovery redispatch. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, PreferredRuntimeInstanceId={PreferredRuntimeInstanceId}, FailedRuntimeInstanceId={FailedRuntimeInstanceId}",
                    sharedRunId,
                    controlPlaneId,
                    preferredRuntimeInstanceId,
                    failedRuntimeInstanceId);

                return null;
            }

            var snapshot =
                await _runtimeInstanceRegistry
                    .GetAsync(
                        preferredRuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (snapshot is not null &&
                snapshot.CanAcceptRun)
            {
                return preferredRuntimeInstanceId;
            }

            _logger.LogInformation(
                "Ignoring stale preferred runtime instance because it is not routable. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, PreferredRuntimeInstanceId={PreferredRuntimeInstanceId}, Status={Status}, CanAcceptRun={CanAcceptRun}",
                sharedRunId,
                controlPlaneId,
                preferredRuntimeInstanceId,
                snapshot?.Status,
                snapshot?.CanAcceptRun);

            return null;
        }

        /// <summary>
        /// Determines whether admission selected the runtime instance that triggered recovery.
        /// </summary>
        /// <param name="metadata">The merged dispatch metadata.</param>
        /// <param name="runtimeInstanceId">The selected runtime instance identifier.</param>
        /// <returns><c>true</c> when the selected runtime is the failed recovery runtime; otherwise, <c>false</c>.</returns>
        private static bool IsRecoveryFailedRuntimeInstance(
            IReadOnlyDictionary<string, string> metadata,
            string runtimeInstanceId)
        {
            return !string.IsNullOrWhiteSpace(runtimeInstanceId) &&
                TryGetMetadataValue(
                    metadata,
                    RecoveryFailedRuntimeInstanceIdMetadataKey,
                    out var failedRuntimeInstanceId) &&
                string.Equals(
                    runtimeInstanceId,
                    failedRuntimeInstanceId,
                    StringComparison.Ordinal);
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
        /// Determines whether a runtime instance is still routable immediately before dispatch.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><c>true</c> when the runtime instance can accept runs; otherwise, <c>false</c>.</returns>
        private async Task<bool> IsRuntimeInstanceRoutableAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(runtimeInstanceId))
            {
                return false;
            }

            var snapshot =
                await _runtimeInstanceRegistry
                    .GetAsync(
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            return snapshot is not null &&
                   snapshot.CanAcceptRun;
        }

        /// <summary>
        /// Attempts to requeue a dispatched queue item without masking the original failure.
        /// </summary>
        /// <param name="queueItem">The dispatched queue item.</param>
        /// <param name="reason">The requeue reason.</param>
        /// <param name="metadata">The metadata to merge into the queue item before making it pending again.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task RequeueDispatchedBestEffortAsync(
            AiSharedQueueItem queueItem,
            string reason,
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(queueItem.ClaimToken))
            {
                _logger.LogWarning(
                    "Dispatched shared queue item could not be requeued because claim token is missing. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, Reason={Reason}",
                    queueItem.SharedRunId,
                    queueItem.ControlPlaneId,
                    reason);

                return;
            }

            try
            {
                var requeued = await _sharedQueue
                    .RequeueDispatchedAsync(
                        queueItem.SharedRunId,
                        queueItem.ClaimToken,
                        reason,
                        metadata,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (requeued is null)
                {
                    _logger.LogWarning(
                        "Dispatched shared queue item requeue was rejected. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, ClaimToken={ClaimToken}, Status={Status}, Reason={Reason}",
                        queueItem.SharedRunId,
                        queueItem.ControlPlaneId,
                        queueItem.ClaimToken,
                        queueItem.Status,
                        reason);

                    return;
                }

                _logger.LogDebug(
                    "Dispatched shared queue item requeued. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, ClaimToken={ClaimToken}, Reason={Reason}",
                    queueItem.SharedRunId,
                    queueItem.ControlPlaneId,
                    queueItem.ClaimToken,
                    reason);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Dispatched shared queue item requeue failed. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, ClaimToken={ClaimToken}, Reason={Reason}",
                    queueItem.SharedRunId,
                    queueItem.ControlPlaneId,
                    queueItem.ClaimToken,
                    reason);
            }
        }

        /// <summary>
        /// Attempts to requeue a claimed queue item without masking the original failure.
        /// </summary>
        private async Task RequeueBestEffortAsync(
            AiSharedQueueItem queueItem,
            string reason,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(queueItem.ClaimToken))
            {
                _logger.LogWarning(
                    "Shared queue item could not be requeued because claim token is missing. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, Reason={Reason}",
                    queueItem.SharedRunId,
                    queueItem.ControlPlaneId,
                    reason);

                return;
            }

            try
            {
                await _sharedQueue
                    .RequeueAsync(
                        queueItem.SharedRunId,
                        queueItem.ClaimToken,
                        reason,
                        cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogDebug(
                    "Shared queue item requeued. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, ClaimToken={ClaimToken}, Reason={Reason}",
                    queueItem.SharedRunId,
                    queueItem.ControlPlaneId,
                    queueItem.ClaimToken,
                    reason);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Shared queue item requeue failed. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, ClaimToken={ClaimToken}, Reason={Reason}",
                    queueItem.SharedRunId,
                    queueItem.ControlPlaneId,
                    queueItem.ClaimToken,
                    reason);
            }
        }

        /// <summary>
        /// Attempts to release temporary admission capacity without masking the original failure.
        /// </summary>
        private async Task ReleaseReservationBestEffortAsync(
            string? sharedRunId,
            string? runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(runtimeInstanceId))
            {
                return;
            }

            try
            {
                await _reservationStore
                    .ReleaseAsync(
                        runtimeInstanceId,
                        runCount: 1,
                        cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Temporary admission capacity released. SharedRunId={SharedRunId}, RuntimeInstanceId={RuntimeInstanceId}, RunCount={RunCount}",
                    sharedRunId,
                    runtimeInstanceId,
                    1);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Temporary admission capacity release failed. SharedRunId={SharedRunId}, RuntimeInstanceId={RuntimeInstanceId}, RunCount={RunCount}",
                    sharedRunId,
                    runtimeInstanceId,
                    1);
            }
        }

        /// <summary>
        /// Calculates operation duration in milliseconds.
        /// </summary>
        private static long CalculateDurationMs(
            DateTimeOffset startedAtUtc,
            DateTimeOffset completedAtUtc)
        {
            return (long)(completedAtUtc - startedAtUtc).TotalMilliseconds;
        }

        /// <summary>
        /// Resolves the logical control-plane identifier used by shared queue dispatch operations.
        /// </summary>
        /// <param name="requestedControlPlaneId">The preferred control-plane identifier when already known.</param>
        /// <param name="metadata">The metadata that may contain a logical control-plane identifier.</param>
        /// <param name="source">The diagnostic source of the resolution request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved logical control-plane identifier.</returns>
        private async Task<string> ResolveControlPlaneIdAsync(
            string? requestedControlPlaneId,
            IReadOnlyDictionary<string, string>? metadata,
            string source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await _controlPlaneIdResolver
                    .ResolveAsync(
                        new AiControlPlaneIdResolutionRequest
                        {
                            RequestedControlPlaneId = requestedControlPlaneId,
                            Metadata = metadata,
                            Source = source,
                            AllowGeneratedFallback = false
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(controlPlaneId))
            {
                throw new InvalidOperationException(
                    "The resolved control-plane identifier cannot be null or empty.");
            }

            return controlPlaneId;
        }

        /// <summary>
        /// Returns the first non-empty value.
        /// </summary>
        /// <param name="values">The candidate values.</param>
        /// <returns>The first non-empty value, or null when none is available.</returns>
        private static string? FirstNonEmpty(
            params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        /// <summary>
        /// Merges metadata dictionaries.
        /// </summary>
        /// <param name="sources">The metadata sources to merge.</param>
        /// <returns>The merged metadata dictionary.</returns>
        private static IReadOnlyDictionary<string, string> MergeMetadata(
            params IReadOnlyDictionary<string, string>[] sources)
        {
            var merged = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var source in sources)
            {
                foreach (var pair in source)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key))
                    {
                        merged[pair.Key] = pair.Value;
                    }
                }
            }

            return merged;
        }

        /// <summary>
        /// Creates an RBAC execution context from a durable execution context snapshot.
        /// </summary>
        /// <param name="snapshot">The durable execution context snapshot.</param>
        /// <returns>The restored RBAC execution context.</returns>
        private static RbacExecutionContext CreateExecutionContext(
            ExecutionContextSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            return new RbacExecutionContext
            {
                ContextKey = string.IsNullOrWhiteSpace(snapshot.ContextKey)
                    ? $"ctx-{Guid.NewGuid():N}"
                    : snapshot.ContextKey,

                Project = snapshot.Project ?? string.Empty,
                UserId = snapshot.UserId ?? string.Empty,

                TenantId = snapshot.TenantId ?? string.Empty,
                TenantGroupId = snapshot.TenantGroupId ?? string.Empty,

                CurrentNamespace = snapshot.CurrentNamespace ?? snapshot.TenantId ?? string.Empty,

                Namespaces = CloneNamespaces(
                    snapshot.Namespaces),

                InFlightCount = 0,
                TtlSeconds = 30,
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Restores the previous RBAC execution context.
        /// </summary>
        /// <param name="previousExecutionContext">The previous execution context.</param>
        private void RestoreExecutionContext(
            RbacExecutionContext? previousExecutionContext)
        {
            if (previousExecutionContext is not null)
            {
                _executionContextAccessor.Set(
                    previousExecutionContext);

                return;
            }

            _executionContextAccessor.Clear();
        }

        /// <summary>
        /// Clones namespace entries from a durable execution context snapshot.
        /// </summary>
        /// <param name="namespaces">The namespace entries to clone.</param>
        /// <returns>The cloned namespace entries.</returns>
        private static List<NamespaceEntry> CloneNamespaces(
            IEnumerable<NamespaceEntry>? namespaces)
        {
            if (namespaces is null)
            {
                return new List<NamespaceEntry>();
            }

            return namespaces
                .Select(
                    item => new NamespaceEntry
                    {
                        Name = item.Name,
                        Trns = item.Trns is null
                            ? new HashSet<string>()
                            : new HashSet<string>(
                                item.Trns,
                                StringComparer.Ordinal)
                    })
                .ToList();
        }

    }
}