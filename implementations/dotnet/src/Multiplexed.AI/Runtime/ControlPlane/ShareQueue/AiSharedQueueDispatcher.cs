using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.Signals;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.Rbac.Core.ExecutionContext;
using RbacExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances;
using Multiplexed.Abstractions.AI.Observability.Events;

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
        private const string SharedQueueRedispatchReplacementReason = "Shared queue redispatch requested replacement runtime capacity.";
        private static readonly TimeSpan ReservationHandoffPollInterval =
            TimeSpan.FromMilliseconds(100);

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
        private readonly IAiControlPlaneObserver _observer;
        private readonly IAiRuntimeSignalPublisher? _runtimeSignalPublisher;
        private readonly AiRuntimeLifecycleEventWriter _lifecycleWriter;
        private readonly AiSharedQueuePumpOptions _queuePumpOptions;
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
            ILogger<AiSharedQueueDispatcher> logger,
            IOptions<AiSharedQueuePumpOptions>? queuePumpOptions = null)
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
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                queuePumpOptions: queuePumpOptions)
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
            IAiRuntimeRecoveryForensicsRecorder forensicsRecorder,
            IAiRuntimeSignalPublisher? runtimeSignalPublisher = null,
            IAiRuntimeLifecycleJournal? lifecycleJournal = null,
            IOptions<AiSharedQueuePumpOptions>? queuePumpOptions = null,
            IAiControlPlaneObserver? observer = null)
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
            ArgumentNullException.ThrowIfNull(forensicsRecorder);
            var recoveryObserver = observer is null
                ? AiRecoveryObservabilityCompatibility.Create(forensicsRecorder)
                : AiRecoveryObservabilityCompatibility.Compose(observer, forensicsRecorder);
            var resolvedLifecycleJournal = lifecycleJournal ?? NoopAiRuntimeLifecycleJournal.Instance;
            _observer = AiRuntimeLifecycleObservabilityCompatibility.Compose(
                recoveryObserver,
                resolvedLifecycleJournal);
            _lifecycleWriter = new AiRuntimeLifecycleEventWriter(resolvedLifecycleJournal);
            _runtimeSignalPublisher = runtimeSignalPublisher;
            _queuePumpOptions =
                queuePumpOptions?.Value ??
                new AiSharedQueuePumpOptions();
        }

        public async Task<AiSharedQueueDispatchResult> DispatchNextAsync(
            AiSharedQueueDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.RuntimeInstanceId);

            var startedAtUtc = DateTimeOffset.UtcNow;
            string? reservedRuntimeInstanceId = null;
            string? reservedSharedRunId = null;
            DateTimeOffset? runtimeAcceptedAtUtc = null;
            bool deferQueueLessReservationRelease = false;
            AiSharedQueueItem? ownedQueueItem = null;

            static bool MatchesDispatchOwnership(
                AiSharedRunRecord? candidate,
                string runtimeInstanceId,
                string localRunId,
                string? executionId)
            {
                if (candidate is null ||
                    candidate.Status != AiSharedRunStatus.Dispatched ||
                    !string.Equals(candidate.AssignedRuntimeInstanceId, runtimeInstanceId, StringComparison.Ordinal) ||
                    !string.Equals(candidate.LocalRunId, localRunId, StringComparison.Ordinal))
                {
                    return false;
                }

                return string.IsNullOrWhiteSpace(executionId)
                    ? string.IsNullOrWhiteSpace(candidate.ExecutionId)
                    : string.Equals(candidate.ExecutionId, executionId, StringComparison.Ordinal);
            }

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

                ownedQueueItem = await _sharedQueue
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

                var queueItem = ownedQueueItem;

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

                sharedRun =
                    await ReleaseFailedDispatchOwnershipIfCurrentAsync(
                            queueItem,
                            sharedRun,
                            cancellationToken)
                        .ConfigureAwait(false);

                var existingDurableDispatch =
                    await TryFinalizeExistingDurableDispatchAsync(
                            queueItem,
                            sharedRun,
                            startedAtUtc)
                        .ConfigureAwait(false);

                if (existingDurableDispatch is not null)
                {
                    return existingDurableDispatch;
                }

                var tenantRuntimeSettings =
                    _tenantRuntimeSettingsProvider.GetSettings(
                        sharedRun.ExecutionContextSnapshot.TenantId,
                        sharedRun.ExecutionContextSnapshot.TenantGroupId);

                var queueLessDispatchPolicy =
                    tenantRuntimeSettings.LocalQueueCapacity == 0;

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

                    var isFailedOwnershipRedispatch =
                        TryResolveFailedDispatchOwnership(
                            queueItem.Metadata,
                            sharedRun,
                            out _,
                            out _);

                    var dispatchPlacement =
                        isFailedOwnershipRedispatch
                            ? null
                            : sharedRun.Placement;

                    var admissionDecision = await _admissionController
                        .AdmitAsync(
                            new AiRunAdmissionRequest
                            {
                                RunRequest = sharedRun.RunRequest,
                                RunId = sharedRun.SharedRunId,
                                TenantId = sharedRun.ExecutionContextSnapshot.TenantId,
                                PipelineKey = sharedRun.PipelineKey,
                                PreferredRuntimeInstanceId = safePreferredRuntimeInstanceId,
                                Placement = dispatchPlacement,
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
                            FailureReason = AiRuntimeInstanceFailureReasons.RuntimeInstanceNotRoutable,
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

                    var dispatchMetadata =
                        CreateRuntimeDispatchMetadata(
                            operationMetadata,
                            targetRuntimeInstanceId);

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
                                        Metadata = dispatchMetadata
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

                        if (string.IsNullOrWhiteSpace(dispatchResult.LocalRunId))
                        {
                            const string missingLocalRunIdReason =
                                "Runtime dispatch reported success without returning a local run id.";

                            _logger.LogWarning(
                                "Shared run dispatch returned success without a local run id. Persisting failure metadata and requeuing shared queue item. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, ExecutionId={ExecutionId}",
                                sharedRun.SharedRunId,
                                controlPlaneId,
                                targetRuntimeInstanceId,
                                dispatchResult.ExecutionId);

                            var failedSharedRun =
                                await _sharedRunStore
                                    .MarkDispatchFailedAsync(
                                        sharedRun.SharedRunId,
                                        targetRuntimeInstanceId,
                                        "dispatch-local-run-id-missing",
                                        missingLocalRunIdReason,
                                        cancellationToken)
                                    .ConfigureAwait(false);

                            await RequeueBestEffortAsync(
                                    queueItem,
                                    missingLocalRunIdReason,
                                    cancellationToken)
                                .ConfigureAwait(false);

                            var completedAtUtc =
                                DateTimeOffset.UtcNow;

                            return new AiSharedQueueDispatchResult
                            {
                                Success = false,
                                SharedRunId = queueItem.SharedRunId,
                                RuntimeInstanceId = targetRuntimeInstanceId,
                                QueueItem = queueItem,
                                SharedRun = failedSharedRun ?? sharedRun,
                                DispatchResult = dispatchResult,
                                Message = "Shared queue item was requeued because runtime dispatch did not return a local run id.",
                                FailureReason = "dispatch-local-run-id-missing",
                                StartedAtUtc = startedAtUtc,
                                CompletedAtUtc = completedAtUtc,
                                DurationMs = CalculateDurationMs(
                                    startedAtUtc,
                                    completedAtUtc),
                                Diagnostics = new[]
                                    {
                                         missingLocalRunIdReason,
                                         $"SharedRunId='{sharedRun.SharedRunId}'",
                                         $"RuntimeInstanceId='{targetRuntimeInstanceId}'",
                                         $"ExecutionId='{dispatchResult.ExecutionId}'"
                                    }
                            };
                        }

                        var dispatchedLocalRunId =
                            dispatchResult.LocalRunId!;

                        var acceptedRuntimeInstanceId =
                            string.IsNullOrWhiteSpace(dispatchResult.RuntimeInstanceId)
                                ? targetRuntimeInstanceId
                                : dispatchResult.RuntimeInstanceId;

                        runtimeAcceptedAtUtc =
                            DateTimeOffset.UtcNow;

                        deferQueueLessReservationRelease =
                            queueLessDispatchPolicy &&
                            string.Equals(
                                acceptedRuntimeInstanceId,
                                reservedRuntimeInstanceId,
                                StringComparison.Ordinal) &&
                            _queuePumpOptions
                                .QueueLessDispatchReservationHandoffTimeout >
                                TimeSpan.Zero;

                        /*
                         * Runtime acceptance has already happened. From this point forward,
                         * persistence must not reuse the caller cancellation token.
                         *
                         * Durable shared-run ownership is committed and verified before the
                         * queue item is allowed to become terminally Dispatched.
                         */
                        AiSharedRunRecord? dispatchedRun = null;
                        Exception? lastSharedRunPersistenceException = null;

                        for (var attempt = 1;
                             attempt <= 2 &&
                             !MatchesDispatchOwnership(
                                 dispatchedRun,
                                 acceptedRuntimeInstanceId,
                                 dispatchedLocalRunId,
                                 dispatchResult.ExecutionId);
                             attempt++)
                        {
                            try
                            {
                                dispatchedRun = await _sharedRunStore
                                    .MarkDispatchedAsync(
                                        sharedRun.SharedRunId,
                                        acceptedRuntimeInstanceId,
                                        dispatchedLocalRunId,
                                        dispatchResult.ExecutionId,
                                        dispatchResult.Message,
                                        CancellationToken.None)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception exception)
                            {
                                lastSharedRunPersistenceException = exception;

                                _logger.LogWarning(
                                    exception,
                                    "Shared-run dispatch ownership persistence attempt failed after runtime acceptance. The store will be read back before the attempt is classified. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, LocalRunId={LocalRunId}, ExecutionId={ExecutionId}, Attempt={Attempt}",
                                    sharedRun.SharedRunId,
                                    controlPlaneId,
                                    acceptedRuntimeInstanceId,
                                    dispatchedLocalRunId,
                                    dispatchResult.ExecutionId,
                                    attempt);
                            }

                            if (MatchesDispatchOwnership(
                                    dispatchedRun,
                                    acceptedRuntimeInstanceId,
                                    dispatchedLocalRunId,
                                    dispatchResult.ExecutionId))
                            {
                                break;
                            }

                            try
                            {
                                var persistedRun = await _sharedRunStore
                                    .GetAsync(
                                        sharedRun.SharedRunId,
                                        CancellationToken.None)
                                    .ConfigureAwait(false);

                                if (MatchesDispatchOwnership(
                                        persistedRun,
                                        acceptedRuntimeInstanceId,
                                        dispatchedLocalRunId,
                                        dispatchResult.ExecutionId))
                                {
                                    dispatchedRun = persistedRun;
                                    break;
                                }
                            }
                            catch (Exception exception)
                            {
                                lastSharedRunPersistenceException = exception;

                                _logger.LogWarning(
                                    exception,
                                    "Shared-run dispatch ownership read-back failed after runtime acceptance. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, LocalRunId={LocalRunId}, ExecutionId={ExecutionId}, Attempt={Attempt}",
                                    sharedRun.SharedRunId,
                                    controlPlaneId,
                                    acceptedRuntimeInstanceId,
                                    dispatchedLocalRunId,
                                    dispatchResult.ExecutionId,
                                    attempt);
                            }
                        }

                        if (!MatchesDispatchOwnership(
                                dispatchedRun,
                                acceptedRuntimeInstanceId,
                                dispatchedLocalRunId,
                                dispatchResult.ExecutionId) &&
                            HasDurableDispatchOwnership(
                                dispatchedRun))
                        {
                            _logger.LogWarning(
                                "Shared-run dispatch ownership was already committed by another dispatcher. The existing owner remains authoritative and the current queue claim will be finalized without requeue. SharedRunId={SharedRunId}, ExistingRuntimeInstanceId={ExistingRuntimeInstanceId}, ExistingLocalRunId={ExistingLocalRunId}, ExistingExecutionId={ExistingExecutionId}, AttemptedRuntimeInstanceId={AttemptedRuntimeInstanceId}, AttemptedLocalRunId={AttemptedLocalRunId}, AttemptedExecutionId={AttemptedExecutionId}",
                                sharedRun.SharedRunId,
                                dispatchedRun!.AssignedRuntimeInstanceId,
                                dispatchedRun.LocalRunId,
                                dispatchedRun.ExecutionId,
                                acceptedRuntimeInstanceId,
                                dispatchedLocalRunId,
                                dispatchResult.ExecutionId);

                            acceptedRuntimeInstanceId =
                                dispatchedRun.AssignedRuntimeInstanceId!;

                            dispatchedLocalRunId =
                                dispatchedRun.LocalRunId!;
                        }

                        if (!MatchesDispatchOwnership(
                                dispatchedRun,
                                acceptedRuntimeInstanceId,
                                dispatchedLocalRunId,
                                dispatchedRun?.ExecutionId))
                        {
                            _logger.LogWarning(
                                "Shared-run dispatch ownership could not be confirmed after runtime acceptance. The claimed queue item will be requeued and must not become Dispatched. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, LocalRunId={LocalRunId}, ExecutionId={ExecutionId}, LastException={LastException}",
                                sharedRun.SharedRunId,
                                controlPlaneId,
                                acceptedRuntimeInstanceId,
                                dispatchedLocalRunId,
                                dispatchResult.ExecutionId,
                                lastSharedRunPersistenceException?.Message);

                            await RequeueBestEffortAsync(
                                    queueItem,
                                    "Runtime accepted the run but durable shared-run ownership was not confirmed.",
                                    CancellationToken.None)
                                .ConfigureAwait(false);

                            var completedAtUtc = DateTimeOffset.UtcNow;

                            return new AiSharedQueueDispatchResult
                            {
                                Success = false,
                                SharedRunId = queueItem.SharedRunId,
                                RuntimeInstanceId = acceptedRuntimeInstanceId,
                                QueueItem = queueItem,
                                SharedRun = dispatchedRun ?? sharedRun,
                                DispatchResult = dispatchResult,
                                Message = "Shared queue item was requeued because durable dispatch ownership was not confirmed.",
                                FailureReason = "shared-run-dispatch-ownership-not-confirmed",
                                StartedAtUtc = startedAtUtc,
                                CompletedAtUtc = completedAtUtc,
                                DurationMs = CalculateDurationMs(startedAtUtc, completedAtUtc),
                                Diagnostics = new[]
                                {
                                    $"ExpectedRuntimeInstanceId='{acceptedRuntimeInstanceId}'",
                                    $"ExpectedLocalRunId='{dispatchedLocalRunId}'",
                                    $"ExpectedExecutionId='{dispatchResult.ExecutionId}'",
                                    $"ActualRuntimeInstanceId='{dispatchedRun?.AssignedRuntimeInstanceId}'",
                                    $"ActualLocalRunId='{dispatchedRun?.LocalRunId}'",
                                    $"ActualExecutionId='{dispatchedRun?.ExecutionId}'",
                                    $"LastPersistenceException='{lastSharedRunPersistenceException?.Message}'"
                                }
                            };
                        }

                        _logger.LogInformation(
                            "Shared-run dispatch ownership durably persisted before queue finalization. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, LocalRunId={LocalRunId}, ExecutionId={ExecutionId}",
                            dispatchedRun!.SharedRunId,
                            controlPlaneId,
                            dispatchedRun.AssignedRuntimeInstanceId,
                            dispatchedRun.LocalRunId,
                            dispatchedRun.ExecutionId);

                        /*
                         * Finalize the queue only after exact durable ownership is confirmed.
                         * A timeout can be ambiguous, so retry the idempotent claim-token
                         * transition and read the queue item back before declaring failure.
                         */
                        AiSharedQueueItem? dispatchedQueueItem = null;
                        Exception? lastQueueFinalizationException = null;

                        for (var attempt = 1; attempt <= 3 && dispatchedQueueItem is null; attempt++)
                        {
                            try
                            {
                                dispatchedQueueItem = await _sharedQueue
                                    .MarkDispatchedAsync(
                                        queueItem.SharedRunId,
                                        queueItem.ClaimToken!,
                                        dispatchResult.Message,
                                        CancellationToken.None)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception exception)
                            {
                                lastQueueFinalizationException = exception;

                                _logger.LogWarning(
                                    exception,
                                    "Shared queue finalization attempt failed after durable shared-run ownership was confirmed. The queue item will be read back before the attempt is classified. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, ClaimToken={ClaimToken}, RuntimeInstanceId={RuntimeInstanceId}, LocalRunId={LocalRunId}, Attempt={Attempt}",
                                    queueItem.SharedRunId,
                                    controlPlaneId,
                                    queueItem.ClaimToken,
                                    acceptedRuntimeInstanceId,
                                    dispatchedLocalRunId,
                                    attempt);
                            }

                            if (dispatchedQueueItem is not null)
                            {
                                break;
                            }

                            try
                            {
                                var currentQueueItem = await _sharedQueue
                                    .GetAsync(
                                        queueItem.SharedRunId,
                                        CancellationToken.None)
                                    .ConfigureAwait(false);

                                if (currentQueueItem?.Status == AiSharedQueueItemStatus.Dispatched &&
                                    string.Equals(
                                        currentQueueItem.ClaimToken,
                                        queueItem.ClaimToken,
                                        StringComparison.Ordinal))
                                {
                                    dispatchedQueueItem = currentQueueItem;
                                    break;
                                }
                            }
                            catch (Exception exception)
                            {
                                lastQueueFinalizationException = exception;

                                _logger.LogWarning(
                                    exception,
                                    "Shared queue finalization read-back failed after durable shared-run ownership was confirmed. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, ClaimToken={ClaimToken}, RuntimeInstanceId={RuntimeInstanceId}, LocalRunId={LocalRunId}, Attempt={Attempt}",
                                    queueItem.SharedRunId,
                                    controlPlaneId,
                                    queueItem.ClaimToken,
                                    acceptedRuntimeInstanceId,
                                    dispatchedLocalRunId,
                                    attempt);
                            }
                        }

                        if (dispatchedQueueItem is null)
                        {
                            _logger.LogError(
                                "Shared-run ownership is durable, but shared queue finalization could not be confirmed. The queue item is intentionally not requeued because redispatching would duplicate an already accepted runtime run. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, ClaimToken={ClaimToken}, RuntimeInstanceId={RuntimeInstanceId}, LocalRunId={LocalRunId}, ExecutionId={ExecutionId}, LastException={LastException}",
                                queueItem.SharedRunId,
                                controlPlaneId,
                                queueItem.ClaimToken,
                                acceptedRuntimeInstanceId,
                                dispatchedLocalRunId,
                                dispatchResult.ExecutionId,
                                lastQueueFinalizationException?.Message);

                            var completedAtUtc = DateTimeOffset.UtcNow;

                            return new AiSharedQueueDispatchResult
                            {
                                Success = false,
                                SharedRunId = queueItem.SharedRunId,
                                RuntimeInstanceId = acceptedRuntimeInstanceId,
                                QueueItem = queueItem,
                                SharedRun = dispatchedRun,
                                DispatchResult = dispatchResult,
                                Message = "Durable dispatch ownership exists, but shared queue finalization could not be confirmed.",
                                FailureReason = "shared-queue-dispatch-finalization-not-confirmed",
                                StartedAtUtc = startedAtUtc,
                                CompletedAtUtc = completedAtUtc,
                                DurationMs = CalculateDurationMs(startedAtUtc, completedAtUtc),
                                Diagnostics = new[]
                                {
                                    "The queue item was not requeued because the runtime already accepted the run.",
                                    $"ClaimToken='{queueItem.ClaimToken}'",
                                    $"RuntimeInstanceId='{acceptedRuntimeInstanceId}'",
                                    $"LocalRunId='{dispatchedLocalRunId}'",
                                    $"ExecutionId='{dispatchResult.ExecutionId}'",
                                    $"LastFinalizationException='{lastQueueFinalizationException?.Message}'"
                                }
                            };
                        }

                        ownedQueueItem =
                            dispatchedQueueItem;

                        _logger.LogInformation(
                            "Shared queue item finalized after durable shared-run ownership persistence. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, LocalRunId={LocalRunId}, ExecutionId={ExecutionId}, ClaimToken={ClaimToken}",
                            sharedRun.SharedRunId,
                            controlPlaneId,
                            acceptedRuntimeInstanceId,
                            dispatchedLocalRunId,
                            dispatchResult.ExecutionId,
                            queueItem.ClaimToken);

                        await RecordWorkPlacementLifecycleBestEffortAsync(
                                controlPlaneId,
                                queueItem,
                                dispatchedRun!,
                                operationMetadata)
                            .ConfigureAwait(false);

                        await PublishSharedRunDispatchedSignalBestEffortAsync(
                                controlPlaneId,
                                dispatchedRun!)
                            .ConfigureAwait(false);

                        var completedAtUtcSuccess =
                            DateTimeOffset.UtcNow;

                        return new AiSharedQueueDispatchResult
                        {
                            Success = true,
                            SharedRunId = queueItem.SharedRunId,
                            RuntimeInstanceId = acceptedRuntimeInstanceId,
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
                        if (deferQueueLessReservationRelease &&
                            runtimeAcceptedAtUtc.HasValue &&
                            !string.IsNullOrWhiteSpace(
                                reservedRuntimeInstanceId))
                        {
                            ScheduleReservationReleaseAfterCapacityHandoff(
                                reservedSharedRunId,
                                reservedRuntimeInstanceId,
                                runtimeAcceptedAtUtc.Value);

                            reservedSharedRunId = null;
                            reservedRuntimeInstanceId = null;
                        }
                        else
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
                    "Shared queue dispatch failed with exception. " +
                    "PumpRuntimeInstanceId={PumpRuntimeInstanceId}, " +
                    "WorkerId={WorkerId}, " +
                    "OwnedSharedRunId={OwnedSharedRunId}, " +
                    "OwnedQueueStatus={OwnedQueueStatus}, " +
                    "OwnedClaimToken={OwnedClaimToken}, " +
                    "ReservedSharedRunId={ReservedSharedRunId}, " +
                    "ReservedRuntimeInstanceId={ReservedRuntimeInstanceId}",
                    request.RuntimeInstanceId,
                    request.WorkerId,
                    ownedQueueItem?.SharedRunId,
                    ownedQueueItem?.Status,
                    ownedQueueItem?.ClaimToken,
                    reservedSharedRunId,
                    reservedRuntimeInstanceId);

                /*
                 * Cleanup must not reuse the failing operation cancellation token.
                 * Once ownership has been acquired, an unhandled exception must not
                 * leave the queue item permanently Claimed or Dispatched.
                 */
                if (ownedQueueItem is not null)
                {
                    var requeueReason =
                        "Unhandled shared queue dispatch exception: " +
                        $"{exception.GetType().Name}: {exception.Message}";

                    if (ownedQueueItem.Status ==
                        AiSharedQueueItemStatus.Dispatched)
                    {
                        /*
                         * Runtime acceptance, durable shared-run ownership, and queue
                         * finalization have already committed. No later observability,
                         * lifecycle, signal, or response failure may make this work
                         * claimable again.
                         */
                        _logger.LogError(
                            "Post-commit shared queue dispatch failure was isolated without requeue. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, ClaimToken={ClaimToken}, Reason={Reason}",
                            ownedQueueItem.SharedRunId,
                            ownedQueueItem.ControlPlaneId,
                            ownedQueueItem.ClaimToken,
                            requeueReason);
                    }
                    else if (ownedQueueItem.Status ==
                             AiSharedQueueItemStatus.Claimed)
                    {
                        await RequeueBestEffortAsync(
                                ownedQueueItem,
                                requeueReason,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }

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
                    SharedRunId = ownedQueueItem?.SharedRunId,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    QueueItem = ownedQueueItem,
                    Message = "Shared queue dispatch failed.",
                    FailureReason = exception.Message,
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = completedAtUtc,
                    DurationMs = CalculateDurationMs(
                        startedAtUtc,
                        completedAtUtc),
                    Diagnostics = new[]
                    {
                         exception.Message,
                         $"ExceptionType='{exception.GetType().FullName}'",
                         $"OwnedSharedRunId='{ownedQueueItem?.SharedRunId}'",
                         $"OwnedQueueStatus='{ownedQueueItem?.Status}'",
                         $"OwnedClaimTokenPresent='{!string.IsNullOrWhiteSpace(ownedQueueItem?.ClaimToken)}'"
                    }
                };
            }
        }

        /// <summary>
        /// Releases the exact failed physical dispatch ownership carried by a claimed queue item
        /// before the stale durable-dispatch guard is evaluated.
        /// </summary>
        /// <remarks>
        /// The existing atomic shared-run compare-and-set is reused by both crash recovery and normal
        /// external-wait continuation re-drive. A continuation re-drive is intentionally not assigned
        /// <c>recovery.mode</c>; it remains a normal continuation of the existing parent execution.
        /// </remarks>
        /// <param name="queueItem">The claimed queue item.</param>
        /// <param name="sharedRun">The current durable shared-run record.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The current durable shared-run record after the exact compare-and-set release.</returns>
        private async Task<AiSharedRunRecord>
            ReleaseFailedDispatchOwnershipIfCurrentAsync(
                AiSharedQueueItem queueItem,
                AiSharedRunRecord sharedRun,
                CancellationToken cancellationToken)
        {
            if (!TryResolveFailedDispatchOwnership(
                    queueItem.Metadata,
                    sharedRun,
                    out var failedRuntimeInstanceId,
                    out var failedLocalRunId) ||
                !string.Equals(
                    sharedRun.AssignedRuntimeInstanceId,
                    failedRuntimeInstanceId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    sharedRun.LocalRunId,
                    failedLocalRunId,
                    StringComparison.Ordinal))
            {
                return sharedRun;
            }

            var released =
                await _sharedRunStore
                    .MarkRequeuedAfterScaleOutIfCurrentAsync(
                        sharedRun.SharedRunId,
                        failedRuntimeInstanceId,
                        failedLocalRunId,
                        "Shared queue redispatch claimed; failed durable ownership released before replacement dispatch.",
                        queueItem.Metadata,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (released is null)
            {
                throw new InvalidOperationException(
                    $"Shared queue redispatch could not reload shared run '{sharedRun.SharedRunId}' after releasing failed durable ownership.");
            }

            _logger.LogInformation(
                "Shared queue redispatch released failed durable shared-run ownership before replacement dispatch. SharedRunId={SharedRunId}, FailedRuntimeInstanceId={FailedRuntimeInstanceId}, FailedLocalRunId={FailedLocalRunId}, CurrentStatus={CurrentStatus}, CurrentRuntimeInstanceId={CurrentRuntimeInstanceId}, CurrentLocalRunId={CurrentLocalRunId}",
                sharedRun.SharedRunId,
                failedRuntimeInstanceId,
                failedLocalRunId,
                released.Status,
                released.AssignedRuntimeInstanceId,
                released.LocalRunId);

            return released;
        }

        /// <summary>
        /// Resolves exact failed physical dispatch ownership for either a supported crash-recovery redispatch
        /// or a normal external-wait continuation re-drive.
        /// </summary>
        /// <param name="metadata">The claimed queue metadata.</param>
        /// <param name="sharedRun">The durable shared run used to distinguish normal continuation re-drive from crash recovery.</param>
        /// <param name="failedRuntimeInstanceId">The failed runtime instance identifier.</param>
        /// <param name="failedLocalRunId">The failed local run identifier.</param>
        /// <returns><c>true</c> when a complete supported failed dispatch ownership is present.</returns>
        private static bool TryResolveFailedDispatchOwnership(
            IReadOnlyDictionary<string, string> metadata,
            AiSharedRunRecord sharedRun,
            out string failedRuntimeInstanceId,
            out string failedLocalRunId)
        {
            failedRuntimeInstanceId = string.Empty;
            failedLocalRunId = string.Empty;

            var hasRecoveryMode = TryGetMetadataValue(
                metadata,
                AiRuntimeRecoveryMetadataKeys.Mode,
                out var recoveryMode);

            var supportedRecoveryRedispatch =
                hasRecoveryMode &&
                (string.Equals(
                     recoveryMode,
                     AiRuntimeRecoveryModes.ResumeExistingExecution,
                     StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(
                     recoveryMode,
                     AiRuntimeRecoveryModes.RequeueLocalQueuedRun,
                     StringComparison.OrdinalIgnoreCase));

            var normalExternalWaitContinuationRedrive =
                !hasRecoveryMode &&
                sharedRun.RunRequest.ExternalWaitContinuation is not null;

            if (!supportedRecoveryRedispatch &&
                !normalExternalWaitContinuationRedrive)
            {
                return false;
            }

            return TryGetMetadataValue(
                       metadata,
                       AiRuntimeRecoveryMetadataKeys.FailedRuntimeInstanceId,
                       out failedRuntimeInstanceId) &&
                   TryGetMetadataValue(
                       metadata,
                       AiRuntimeRecoveryMetadataKeys.FailedLocalRunId,
                       out failedLocalRunId);
        }

        /// <summary>
        /// Finalizes a stale claimed queue item from existing durable shared-run
        /// ownership without calling the runtime a second time.
        /// </summary>
        private async Task<AiSharedQueueDispatchResult?>
            TryFinalizeExistingDurableDispatchAsync(
                AiSharedQueueItem queueItem,
                AiSharedRunRecord sharedRun,
                DateTimeOffset startedAtUtc)
        {
            if (!HasDurableDispatchOwnership(sharedRun))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(queueItem.ClaimToken))
            {
                throw new InvalidOperationException(
                    $"A stale durable dispatch for shared run '{sharedRun.SharedRunId}' was claimed without a claim token.");
            }

            var finalizedQueueItem =
                await _sharedQueue
                    .MarkDispatchedAsync(
                        queueItem.SharedRunId,
                        queueItem.ClaimToken,
                        "Existing durable shared-run ownership finalized without another runtime dispatch.",
                        CancellationToken.None)
                    .ConfigureAwait(false);

            if (finalizedQueueItem is null)
            {
                var currentQueueItem =
                    await _sharedQueue
                        .GetAsync(
                            queueItem.SharedRunId,
                            CancellationToken.None)
                        .ConfigureAwait(false);

                if (currentQueueItem?.Status !=
                    AiSharedQueueItemStatus.Dispatched)
                {
                    throw new InvalidOperationException(
                        $"Existing durable dispatch ownership for shared run '{sharedRun.SharedRunId}' could not finalize its queue item.");
                }

                finalizedQueueItem = currentQueueItem;
            }

            _logger.LogWarning(
                "Stale shared queue item healed from immutable durable dispatch ownership. SharedRunId={SharedRunId}, RuntimeInstanceId={RuntimeInstanceId}, LocalRunId={LocalRunId}, ExecutionId={ExecutionId}, ClaimToken={ClaimToken}",
                sharedRun.SharedRunId,
                sharedRun.AssignedRuntimeInstanceId,
                sharedRun.LocalRunId,
                sharedRun.ExecutionId,
                queueItem.ClaimToken);

            var completedAtUtc =
                DateTimeOffset.UtcNow;

            return new AiSharedQueueDispatchResult
            {
                Success = true,
                SharedRunId = sharedRun.SharedRunId,
                RuntimeInstanceId =
                    sharedRun.AssignedRuntimeInstanceId,
                QueueItem = finalizedQueueItem,
                SharedRun = sharedRun,
                Message =
                    "Existing durable dispatch ownership was preserved and the stale queue item was finalized without another runtime call.",
                StartedAtUtc = startedAtUtc,
                CompletedAtUtc = completedAtUtc,
                DurationMs =
                    CalculateDurationMs(
                        startedAtUtc,
                        completedAtUtc),
                Diagnostics = new[]
                {
                    "Runtime dispatch skipped because immutable durable ownership already existed.",
                    $"RuntimeInstanceId='{sharedRun.AssignedRuntimeInstanceId}'",
                    $"LocalRunId='{sharedRun.LocalRunId}'",
                    $"ExecutionId='{sharedRun.ExecutionId}'"
                }
            };
        }

        /// <summary>
        /// Records post-commit placement evidence without allowing an observability
        /// failure to requeue already accepted work.
        /// </summary>
        private async Task RecordWorkPlacementLifecycleBestEffortAsync(
            string controlPlaneId,
            AiSharedQueueItem queueItem,
            AiSharedRunRecord dispatchedRun,
            IReadOnlyDictionary<string, string> metadata)
        {
            Exception? lastException = null;

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await RecordWorkPlacementLifecycleAsync(
                            controlPlaneId,
                            queueItem,
                            dispatchedRun,
                            metadata,
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    return;
                }
                catch (Exception exception)
                {
                    lastException = exception;

                    _logger.LogWarning(
                        exception,
                        "Post-commit work-placement lifecycle append failed. Durable dispatch remains authoritative and will not be requeued. SharedRunId={SharedRunId}, RuntimeInstanceId={RuntimeInstanceId}, LocalRunId={LocalRunId}, Attempt={Attempt}",
                        dispatchedRun.SharedRunId,
                        dispatchedRun.AssignedRuntimeInstanceId,
                        dispatchedRun.LocalRunId,
                        attempt);

                    if (attempt < 3)
                    {
                        await Task
                            .Delay(
                                TimeSpan.FromMilliseconds(
                                    50 * attempt),
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
            }

            _logger.LogError(
                lastException,
                "Post-commit work-placement lifecycle evidence could not be appended after retries. The dispatch remains committed and is intentionally not requeued. SharedRunId={SharedRunId}, RuntimeInstanceId={RuntimeInstanceId}, LocalRunId={LocalRunId}, ExecutionId={ExecutionId}",
                dispatchedRun.SharedRunId,
                dispatchedRun.AssignedRuntimeInstanceId,
                dispatchedRun.LocalRunId,
                dispatchedRun.ExecutionId);
        }

        private static bool HasDurableDispatchOwnership(
            AiSharedRunRecord? sharedRun)
        {
            return sharedRun is not null &&
                sharedRun.Status is
                    AiSharedRunStatus.Dispatched or
                    AiSharedRunStatus.Completed &&
                !string.IsNullOrWhiteSpace(
                    sharedRun.AssignedRuntimeInstanceId) &&
                !string.IsNullOrWhiteSpace(
                    sharedRun.LocalRunId);
        }

        /// <summary>
        /// Publishes a best-effort signal after durable shared-run ownership and queue finalization
        /// have both been confirmed.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="dispatchedRun">The durably dispatched shared run.</param>
        /// <returns>A task representing the publication attempt.</returns>
        private async Task PublishSharedRunDispatchedSignalBestEffortAsync(
            string controlPlaneId,
            AiSharedRunRecord dispatchedRun)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentNullException.ThrowIfNull(dispatchedRun);

            if (_runtimeSignalPublisher is null)
            {
                return;
            }

            try
            {
                await _runtimeSignalPublisher
                    .PublishAsync(
                        new AiRuntimeSignal
                        {
                            Type = AiRuntimeSignalType.SharedRunDispatched,
                            ControlPlaneId = controlPlaneId,
                            TenantId = dispatchedRun.ExecutionContextSnapshot.TenantId,
                            SharedRunId = dispatchedRun.SharedRunId,
                            RuntimeInstanceId = dispatchedRun.AssignedRuntimeInstanceId!,
                            LocalRunId = dispatchedRun.LocalRunId!,
                            ExecutionId = dispatchedRun.ExecutionId
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                /*
                 * Runtime signals are wake-up notifications only. Publication must
                 * never invalidate the already confirmed durable dispatch or trigger
                 * the outer dispatcher cleanup path.
                 */
                _logger.LogWarning(
                    exception,
                    "Shared-run dispatched signal publication failed after durable dispatch confirmation. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, LocalRunId={LocalRunId}, ExecutionId={ExecutionId}",
                    dispatchedRun.SharedRunId,
                    controlPlaneId,
                    dispatchedRun.AssignedRuntimeInstanceId,
                    dispatchedRun.LocalRunId,
                    dispatchedRun.ExecutionId);
            }
        }


        /// <summary>
        /// Records the durable initial or recovery placement only after shared-run ownership
        /// and queue finalization have both converged.
        /// </summary>
        private async Task RecordWorkPlacementLifecycleAsync(
            string controlPlaneId,
            AiSharedQueueItem queueItem,
            AiSharedRunRecord dispatchedRun,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken)
        {
            var runtimeInstanceId = dispatchedRun.AssignedRuntimeInstanceId!;
            var context = await _lifecycleWriter
                .ResolveContextAsync(
                    runtimeInstanceId,
                    hostId: null,
                    poolId: null,
                    fallbackControlPlaneId: controlPlaneId,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var isRecovery = TryGetMetadataValue(
                metadata,
                AiRuntimeRecoveryMetadataKeys.FailureIncidentId,
                out var runtimeFailureIncidentId);
            var eventType = isRecovery
                ? AiRuntimeLifecycleEvents.WorkReassigned
                : AiRuntimeLifecycleEvents.WorkAssigned;
            var subjectId = string.Join(
                ":",
                dispatchedRun.SharedRunId,
                dispatchedRun.LocalRunId);
            var forensicsId = ResolveMetadataValue(
                metadata,
                AiRuntimeRecoveryMetadataKeys.ForensicsId);

            await _observer
                .RecordLifecycleAsync(
                    new AiRuntimeLifecycleEvent
                    {
                        EventId = AiRuntimeLifecycleEventWriter.CreateEventId(
                            eventType,
                            subjectId,
                            isRecovery ? runtimeFailureIncidentId : null),
                        EventType = eventType,
                        TimestampUtc = DateTimeOffset.UtcNow,
                        ControlPlaneId = controlPlaneId,
                        HostCreationMode = context.HostCreationMode,
                        ProviderName = context.ProviderName,
                        PoolId = context.PoolId,
                        HostId = context.HostId,
                        KubernetesPodUid = context.KubernetesPodUid,
                        KubernetesNamespace = context.KubernetesNamespace,
                        KubernetesPodName = context.KubernetesPodName,
                        KubernetesNodeName = context.KubernetesNodeName,
                        RuntimeInstanceId = runtimeInstanceId,
                        RuntimeId = context.RuntimeId,
                        ProcessId = context.ProcessId,
                        TenantId = dispatchedRun.ExecutionContextSnapshot.TenantId,
                        TenantGroupId = dispatchedRun.ExecutionContextSnapshot.TenantGroupId,
                        SharedRunId = dispatchedRun.SharedRunId,
                        LocalRunId = dispatchedRun.LocalRunId,
                        ExecutionId = dispatchedRun.ExecutionId,
                        RuntimeFailureIncidentId = isRecovery
                            ? runtimeFailureIncidentId
                            : null,
                        LedgerEntryId = NullIfWhiteSpace(ResolveMetadataValue(
                            metadata,
                            AiRuntimeRecoveryMetadataKeys.LedgerEntryId)),
                        ForensicsId = string.IsNullOrWhiteSpace(forensicsId)
                            ? null
                            : forensicsId,
                        CorrelationId = FirstNonEmpty(
                            ResolveMetadataValue(metadata, AiRuntimeRecoveryMetadataKeys.CorrelationId),
                            dispatchedRun.CorrelationId),
                        CausationId = NullIfWhiteSpace(ResolveMetadataValue(
                            metadata,
                            AiRuntimeRecoveryMetadataKeys.CausationId)),
                        PreviousStatus = isRecovery
                            ? AiRuntimeRecoveryTransitionStatuses.ReleasedForRecovery
                            : null,
                        CurrentStatus = "assigned",
                        Reason = isRecovery
                            ? "recovery-redispatch-confirmed"
                            : "initial-dispatch-confirmed",
                        Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["queue.claimToken"] = queueItem.ClaimToken ?? string.Empty,
                            [AiRuntimeRecoveryMetadataKeys.TransitionFailedRuntimeInstanceId] = ResolveMetadataValue(
                                metadata,
                                AiRuntimeRecoveryMetadataKeys.FailedRuntimeInstanceId),
                            [AiRuntimeRecoveryMetadataKeys.TransitionFailedLocalRunId] = ResolveMetadataValue(
                                metadata,
                                AiRuntimeRecoveryMetadataKeys.FailedLocalRunId)
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
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

            var eventType = AiEngineEvents.Recovery.ReplacementRuntimeSelected;

            await _observer
                .RecordAsync(
                    AiRecoveryEngineEventFactory.Create(
                        semanticEventType: eventType,
                        eventId: string.Join(":", forensicsId, eventType, replacementRuntimeInstanceId),
                        forensicsId: forensicsId,
                        timestampUtc: DateTimeOffset.UtcNow,
                        outcome: "selected",
                        reason: "replacement-runtime-selected-for-recovery-redispatch",
                        executionId: executionId,
                        sharedRunId: sharedRun.SharedRunId,
                        localRunId: null,
                        runtimeInstanceId: replacementRuntimeInstanceId,
                        metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = sharedRun.ExecutionContextSnapshot.TenantId ?? string.Empty,
                            [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = sharedRun.ExecutionContextSnapshot.TenantGroupId ?? string.Empty,
                            [AiRuntimeRecoveryMetadataKeys.ReplacementRuntimeInstanceId] = replacementRuntimeInstanceId,
                            [AiRuntimeRecoveryMetadataKeys.ReplacementExecutionId] = executionId ?? string.Empty,
                            [AiRuntimeRecoveryMetadataKeys.TransitionFailedRuntimeInstanceId] = ResolveMetadataValue(metadata, AiRuntimeRecoveryMetadataKeys.FailedRuntimeInstanceId),
                            [AiRuntimeRecoveryMetadataKeys.TransitionFailedLocalRunId] = failedLocalRunId ?? string.Empty,
                            ["queue.claimToken"] = queueItem.ClaimToken ?? string.Empty,
                            [AiRuntimeRecoveryMetadataKeys.ResumeContextKey] = sharedRun.ExecutionContextSnapshot.ContextKey ?? string.Empty
                        }),
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
                    AiRuntimeRecoveryMetadataKeys.ForensicsId,
                    out var explicitForensicsId))
            {
                forensicsId = explicitForensicsId;
                executionId = ResolveMetadataValue(metadata, AiRuntimeRecoveryMetadataKeys.FailedExecutionId);
                failedLocalRunId = ResolveMetadataValue(metadata, AiRuntimeRecoveryMetadataKeys.FailedLocalRunId);

                return true;
            }

            executionId =
                ResolveMetadataValue(metadata, AiRuntimeRecoveryMetadataKeys.FailedExecutionId);

            failedLocalRunId =
                ResolveMetadataValue(metadata, AiRuntimeRecoveryMetadataKeys.FailedLocalRunId);

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

            metadata[AiRuntimeScaleOutMetadataKeys.Intent] =
                AiRuntimeScaleOutIntents.SharedQueueRedispatchReplacement;

            metadata[AiRuntimeScaleOutMetadataKeys.RequestId] =
                $"scale-out-redispatch-{sharedRun.SharedRunId}-{Guid.NewGuid():N}";

            if (TryGetMetadataValue(
                    operationMetadata,
                    AiRuntimeRecoveryMetadataKeys.FailedRuntimeInstanceId,
                    out var failedRuntimeInstanceId))
            {
                metadata[AiRuntimeScaleOutMetadataKeys.ExcludedRuntimeInstanceId] =
                    failedRuntimeInstanceId;

                metadata[AiRuntimeScaleOutMetadataKeys.ReplacementForRuntimeInstanceId] =
                    failedRuntimeInstanceId;

                metadata[AiRuntimeRecoveryMetadataKeys.Replacement] =
                    "true";
            }

            return metadata;
        }

        /// <summary>
        /// Creates metadata for the actual runtime dispatch by removing stale runtime assignment
        /// and transport values before stamping the selected runtime instance.
        /// </summary>
        /// <param name="operationMetadata">The merged operation metadata.</param>
        /// <param name="targetRuntimeInstanceId">The selected runtime instance id.</param>
        /// <returns>The sanitized runtime dispatch metadata.</returns>
        private static IReadOnlyDictionary<string, string> CreateRuntimeDispatchMetadata(
            IReadOnlyDictionary<string, string> operationMetadata,
            string targetRuntimeInstanceId)
        {
            var metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var item in operationMetadata)
            {
                if (string.IsNullOrWhiteSpace(item.Key) ||
                    IsStaleRuntimeAssignmentMetadataKey(item.Key) ||
                    IsStaleRuntimeTransportMetadataKey(item.Key))
                {
                    continue;
                }

                metadata[item.Key] =
                    item.Value ?? string.Empty;
            }

            metadata[AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] =
                targetRuntimeInstanceId;

            metadata[AiRuntimeInstanceMetadataKeys.RuntimeInstanceId] =
                targetRuntimeInstanceId;

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
                   string.Equals(key, AiRuntimeScaleOutMetadataKeys.RuntimeInstanceId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "scaleout.runtime.instance.id", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, AiRuntimeInstanceMetadataKeys.RuntimeInstanceId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "runtime.instanceId", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "host.runtimeInstanceId", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "transport.runtimeInstanceId", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines whether a metadata key carries stale runtime transport information
        /// that must not be propagated to replacement runtime dispatch.
        /// </summary>
        /// <param name="key">The metadata key.</param>
        /// <returns><c>true</c> when the key must be removed; otherwise, <c>false</c>.</returns>
        private static bool IsStaleRuntimeTransportMetadataKey(
            string key)
        {
            return string.Equals(key, AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, AiRuntimeInstanceCommandTransportMetadataKeys.CamelCaseTransportEndpoint, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, AiRuntimeInstanceCommandTransportMetadataKeys.GrpcEndpoint, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, AiRuntimeInstanceCommandTransportMetadataKeys.RuntimeCommandEndpoint, StringComparison.OrdinalIgnoreCase);
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
                    AiRuntimeRecoveryMetadataKeys.FailedRuntimeInstanceId,
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
                    AiRuntimeRecoveryMetadataKeys.FailedRuntimeInstanceId,
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
        /// Attempts to requeue a claimed queue item without masking the original
        /// dispatch failure.
        /// </summary>
        /// <param name="queueItem">The claimed queue item.</param>
        /// <param name="reason">The requeue reason.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// <c>true</c> when the item is pending after the operation; otherwise,
        /// <c>false</c>.
        /// </returns>
        private async Task<bool> RequeueBestEffortAsync(
            AiSharedQueueItem queueItem,
            string reason,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(queueItem);

            if (string.IsNullOrWhiteSpace(
                    queueItem.ClaimToken))
            {
                _logger.LogWarning(
                    "Shared queue item could not be requeued because claim token is missing. " +
                    "SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, " +
                    "Status={Status}, Reason={Reason}",
                    queueItem.SharedRunId,
                    queueItem.ControlPlaneId,
                    queueItem.Status,
                    reason);

                return false;
            }

            try
            {
                var requeued =
                    await _sharedQueue
                        .RequeueAsync(
                            queueItem.SharedRunId,
                            queueItem.ClaimToken,
                            reason,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (requeued is not null)
                {
                    _logger.LogDebug(
                        "Shared queue item requeued. " +
                        "SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, " +
                        "ClaimToken={ClaimToken}, Status={Status}, Reason={Reason}",
                        queueItem.SharedRunId,
                        queueItem.ControlPlaneId,
                        queueItem.ClaimToken,
                        requeued.Status,
                        reason);

                    return true;
                }

                /*
                 * Re-read the item to distinguish an idempotent already-pending
                 * transition from a rejected claim ownership transition.
                 */
                var current =
                    await _sharedQueue
                        .GetAsync(
                            queueItem.SharedRunId,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (current is
                    {
                        Status: AiSharedQueueItemStatus.Pending
                    })
                {
                    _logger.LogDebug(
                        "Shared queue item was already pending after requeue returned null. " +
                        "SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, " +
                        "OriginalClaimToken={OriginalClaimToken}, CurrentReason={CurrentReason}",
                        queueItem.SharedRunId,
                        queueItem.ControlPlaneId,
                        queueItem.ClaimToken,
                        current.Reason);

                    return true;
                }

                _logger.LogWarning(
                    "Shared queue item requeue was rejected and the item is not pending. " +
                    "SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, " +
                    "OriginalStatus={OriginalStatus}, CurrentStatus={CurrentStatus}, " +
                    "OriginalClaimToken={OriginalClaimToken}, CurrentClaimToken={CurrentClaimToken}, " +
                    "CurrentClaimedByRuntimeInstanceId={CurrentClaimedByRuntimeInstanceId}, " +
                    "CurrentClaimedByWorkerId={CurrentClaimedByWorkerId}, " +
                    "CurrentClaimExpiresAtUtc={CurrentClaimExpiresAtUtc}, Reason={Reason}",
                    queueItem.SharedRunId,
                    queueItem.ControlPlaneId,
                    queueItem.Status,
                    current?.Status,
                    queueItem.ClaimToken,
                    current?.ClaimToken,
                    current?.ClaimedByRuntimeInstanceId,
                    current?.ClaimedByWorkerId,
                    current?.ClaimExpiresAtUtc,
                    reason);

                return false;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Shared queue item requeue failed. " +
                    "SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, " +
                    "ClaimToken={ClaimToken}, Status={Status}, Reason={Reason}",
                    queueItem.SharedRunId,
                    queueItem.ControlPlaneId,
                    queueItem.ClaimToken,
                    queueItem.Status,
                    reason);

                return false;
            }
        }

        /// <summary>
        /// Transfers a successful queue-less reservation to a bounded handoff.
        /// </summary>
        private void ScheduleReservationReleaseAfterCapacityHandoff(
            string? sharedRunId,
            string runtimeInstanceId,
            DateTimeOffset runtimeAcceptedAtUtc)
        {
            _ = ReleaseReservationAfterCapacityHandoffBestEffortAsync(
                sharedRunId,
                runtimeInstanceId,
                runtimeAcceptedAtUtc);
        }

        /// <summary>
        /// Releases a queue-less reservation after observing a heartbeat produced
        /// after acceptance, or after the bounded handoff timeout expires.
        /// </summary>
        private async Task ReleaseReservationAfterCapacityHandoffBestEffortAsync(
            string? sharedRunId,
            string runtimeInstanceId,
            DateTimeOffset runtimeAcceptedAtUtc)
        {
            var timeout =
                _queuePumpOptions
                    .QueueLessDispatchReservationHandoffTimeout;

            var deadlineUtc =
                DateTimeOffset.UtcNow + timeout;

            var refreshedCapacityObserved =
                false;

            try
            {
                while (DateTimeOffset.UtcNow < deadlineUtc)
                {
                    var snapshot =
                        await _runtimeInstanceRegistry
                            .GetAsync(
                                runtimeInstanceId,
                                CancellationToken.None)
                            .ConfigureAwait(false);

                    if (snapshot is not null &&
                        snapshot.LastHeartbeatAtUtc >=
                            runtimeAcceptedAtUtc)
                    {
                        refreshedCapacityObserved = true;
                        break;
                    }

                    var remaining =
                        deadlineUtc - DateTimeOffset.UtcNow;

                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    await Task
                        .Delay(
                            remaining < ReservationHandoffPollInterval
                                ? remaining
                                : ReservationHandoffPollInterval,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Queue-less admission reservation handoff observation failed. " +
                    "SharedRunId={SharedRunId}, RuntimeInstanceId={RuntimeInstanceId}",
                    sharedRunId,
                    runtimeInstanceId);
            }
            finally
            {
                _logger.LogInformation(
                    "Queue-less admission reservation handoff completed. " +
                    "SharedRunId={SharedRunId}, RuntimeInstanceId={RuntimeInstanceId}, " +
                    "RefreshedCapacityObserved={RefreshedCapacityObserved}",
                    sharedRunId,
                    runtimeInstanceId,
                    refreshedCapacityObserved);

                await ReleaseReservationBestEffortAsync(
                        sharedRunId,
                        runtimeInstanceId,
                        CancellationToken.None)
                    .ConfigureAwait(false);
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
        private static string? NullIfWhiteSpace(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : value;
        }

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