using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.Core.ExecutionContext;
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
    /// - reserve temporary admission capacity before dispatch
    /// - dispatch the shared run to the selected runtime instance
    /// - mark the queue item as dispatched on success
    /// - mark the shared run record as dispatched on success
    /// - persist dispatch failure metadata when dispatch fails
    /// - requeue the item when dispatch fails or throws
    /// - release temporary admission reservations after dispatch attempts
    ///
    /// This service does not scale Kubernetes.
    /// It does not execute DAG steps directly.
    /// </remarks>
    public sealed class AiSharedQueueDispatcher : IAiSharedQueueDispatcher
    {
        private readonly IAiSharedQueue _sharedQueue;
        private readonly IAiSharedRunStore _sharedRunStore;
        private readonly IAiSharedRunDispatcher _sharedRunDispatcher;
        private readonly IAiRunAdmissionController _admissionController;
        private readonly IAiRuntimeAdmissionReservationStore _reservationStore;
        private readonly IExecutionContextAccessor _executionContextAccessor;
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
            IExecutionContextAccessor executionContextAccessor,
            ILogger<AiSharedQueueDispatcher> logger)
        {
            _sharedQueue = sharedQueue ?? throw new ArgumentNullException(nameof(sharedQueue));
            _sharedRunStore = sharedRunStore ?? throw new ArgumentNullException(nameof(sharedRunStore));
            _sharedRunDispatcher = sharedRunDispatcher ?? throw new ArgumentNullException(nameof(sharedRunDispatcher));
            _admissionController = admissionController ?? throw new ArgumentNullException(nameof(admissionController));
            _reservationStore = reservationStore ?? throw new ArgumentNullException(nameof(reservationStore));
            _executionContextAccessor = executionContextAccessor ?? throw new ArgumentNullException(nameof(executionContextAccessor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
                var queueItem = await _sharedQueue
                    .ClaimNextAsync(
                        new AiSharedQueueClaimRequest
                        {
                            RuntimeInstanceId = request.RuntimeInstanceId,
                            WorkerId = request.WorkerId,
                            TenantId = request.TenantId,
                            PipelineKey = request.PipelineKey,
                            ClaimTtl = request.ClaimTtl,
                            CorrelationId = request.CorrelationId,
                            Reason = request.Reason ?? "Claimed for shared queue dispatch."
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

                var controlPlaneId =
                    ResolveControlPlaneId(
                        queueItem,
                        sharedRun);

                var operationMetadata =
                    MergeMetadata(
                        sharedRun.Metadata,
                        queueItem.Metadata,
                        request.Metadata,
                        new Dictionary<string, string>
                        {
                            ["controlPlaneId"] = controlPlaneId
                        });

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

                    var admissionDecision = await _admissionController
                        .AdmitAsync(
                            new AiRunAdmissionRequest
                            {
                                RunRequest = sharedRun.RunRequest,
                                RunId = sharedRun.SharedRunId,
                                TenantId = sharedRun.ExecutionContextSnapshot.TenantId,
                                PipelineKey = sharedRun.PipelineKey,
                                PreferredRuntimeInstanceId = sharedRun.AssignedRuntimeInstanceId,
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

                        _logger.LogDebug(
                            "Shared queue item marked as dispatched. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, ClaimToken={ClaimToken}",
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

                        _logger.LogInformation(
                            "Shared run record marked as dispatched. SharedRunId={SharedRunId}, ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, LocalRunId={LocalRunId}, ExecutionId={ExecutionId}",
                            sharedRun.SharedRunId,
                            controlPlaneId,
                            targetRuntimeInstanceId,
                            dispatchResult.LocalRunId,
                            dispatchResult.ExecutionId);

                        var completed =
                            dispatchedRun ?? sharedRun;

                        var completedAtUtcSuccess =
                            DateTimeOffset.UtcNow;

                        return new AiSharedQueueDispatchResult
                        {
                            Success = true,
                            SharedRunId = queueItem.SharedRunId,
                            RuntimeInstanceId = targetRuntimeInstanceId,
                            QueueItem = dispatchedQueueItem ?? queueItem,
                            SharedRun = completed,
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
        /// Resolves the logical control-plane identifier from a queue item and its shared run.
        /// </summary>
        /// <param name="queueItem">The claimed shared queue item.</param>
        /// <param name="sharedRun">The loaded shared run record.</param>
        /// <returns>The resolved logical control-plane identifier.</returns>
        private static string ResolveControlPlaneId(
            AiSharedQueueItem queueItem,
            AiSharedRunRecord sharedRun)
        {
            if (!string.IsNullOrWhiteSpace(queueItem.ControlPlaneId))
            {
                return queueItem.ControlPlaneId;
            }

            if (!string.IsNullOrWhiteSpace(sharedRun.ControlPlaneId))
            {
                return sharedRun.ControlPlaneId;
            }

            if (queueItem.Metadata.TryGetValue("controlPlaneId", out var queueControlPlaneId) &&
                !string.IsNullOrWhiteSpace(queueControlPlaneId))
            {
                return queueControlPlaneId;
            }

            if (sharedRun.Metadata.TryGetValue("controlPlaneId", out var sharedRunControlPlaneId) &&
                !string.IsNullOrWhiteSpace(sharedRunControlPlaneId))
            {
                return sharedRunControlPlaneId;
            }

            return string.Empty;
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
                StringComparer.Ordinal);

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