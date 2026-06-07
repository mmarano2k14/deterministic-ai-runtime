using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;

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
    /// - dispatch the shared run to the selected runtime instance
    /// - mark the queue item as dispatched on success
    /// - mark the shared run record as dispatched on success
    /// - requeue the item when dispatch fails
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

        /// <summary>
        /// Initializes a new instance of the <see cref="AiSharedQueueDispatcher"/> class.
        /// </summary>
        public AiSharedQueueDispatcher(
            IAiSharedQueue sharedQueue,
            IAiSharedRunStore sharedRunStore,
            IAiSharedRunDispatcher sharedRunDispatcher,
            IAiRunAdmissionController admissionController)
        {
            _sharedQueue = sharedQueue ?? throw new ArgumentNullException(nameof(sharedQueue));
            _sharedRunStore = sharedRunStore ?? throw new ArgumentNullException(nameof(sharedRunStore));
            _sharedRunDispatcher = sharedRunDispatcher ?? throw new ArgumentNullException(nameof(sharedRunDispatcher));
            _admissionController = admissionController ?? throw new ArgumentNullException(nameof(admissionController));
        }

        /// <inheritdoc />
        public async Task<AiSharedQueueDispatchResult> DispatchNextAsync(
            AiSharedQueueDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.RuntimeInstanceId);

            var startedAtUtc = DateTimeOffset.UtcNow;

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

                var sharedRun = await _sharedRunStore
                    .GetAsync(
                        queueItem.SharedRunId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (sharedRun is null)
                {
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

                var admissionDecision = await _admissionController
                    .AdmitAsync(
                        new AiRunAdmissionRequest
                        {
                            RunRequest = sharedRun.RunRequest,
                            RunId = sharedRun.SharedRunId,
                            TenantId = sharedRun.TenantId,
                            PipelineKey = sharedRun.PipelineKey,
                            PreferredRuntimeInstanceId = sharedRun.AssignedRuntimeInstanceId,
                            CorrelationId = request.CorrelationId ?? sharedRun.CorrelationId,
                            RequestedBy = request.RequestedBy,
                            Source = request.Source,
                            Reason = request.Reason ?? "Selecting runtime instance for shared queue dispatch.",
                            Metadata = MergeMetadata(
                                sharedRun.Metadata,
                                request.Metadata)
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                if (admissionDecision.DecisionType != AiRunAdmissionDecisionType.AssignToInstance ||
                    string.IsNullOrWhiteSpace(admissionDecision.AssignedRuntimeInstanceId))
                {
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

                var dispatchResult = await _sharedRunDispatcher
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
                            Metadata = MergeMetadata(
                                sharedRun.Metadata,
                                request.Metadata)
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!dispatchResult.Success)
                {
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
                        SharedRun = sharedRun,
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

                var dispatchedRun = await _sharedRunStore
                    .MarkDispatchedAsync(
                        sharedRun.SharedRunId,
                        targetRuntimeInstanceId,
                        dispatchResult.LocalRunId,
                        dispatchResult.ExecutionId,
                        dispatchResult.Message,
                        cancellationToken)
                    .ConfigureAwait(false);

                var completed = dispatchedRun ?? sharedRun;
                var completedAtUtcSuccess = DateTimeOffset.UtcNow;

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
            catch (Exception exception)
            {
                var completedAtUtc = DateTimeOffset.UtcNow;

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
            }
            catch
            {
                // Best-effort requeue:
                // do not hide the original dispatch/load failure.
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
        /// Merges metadata dictionaries.
        /// </summary>
        private static IReadOnlyDictionary<string, string> MergeMetadata(
            IReadOnlyDictionary<string, string> baseMetadata,
            IReadOnlyDictionary<string, string> overrideMetadata)
        {
            var merged = new Dictionary<string, string>(
                baseMetadata,
                StringComparer.Ordinal);

            foreach (var pair in overrideMetadata)
            {
                merged[pair.Key] = pair.Value;
            }

            return merged;
        }
    }
}