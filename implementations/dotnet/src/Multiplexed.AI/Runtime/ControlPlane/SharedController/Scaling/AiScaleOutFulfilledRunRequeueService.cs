using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Requeues shared runs after scale-out fulfillment.
    /// </summary>
    public sealed class AiScaleOutFulfilledRunRequeueService :
        IAiScaleOutFulfilledRunRequeueService
    {
        private const int MaxBacklogRequeueCount = 100;
        private readonly IAiSharedRunStore sharedRunStore;
        private readonly IAiSharedQueue sharedQueue;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiScaleOutFulfilledRunRequeueService" /> class.
        /// </summary>
        /// <param name="sharedRunStore">The shared run store.</param>
        /// <param name="sharedQueue">The shared queue.</param>
        public AiScaleOutFulfilledRunRequeueService(
            IAiSharedRunStore sharedRunStore,
            IAiSharedQueue sharedQueue)
        {
            this.sharedRunStore = sharedRunStore ?? throw new ArgumentNullException(nameof(sharedRunStore));
            this.sharedQueue = sharedQueue ?? throw new ArgumentNullException(nameof(sharedQueue));
        }

        /// <inheritdoc />
        public async Task<AiScaleOutFulfilledRunRequeueResult> RequeueAsync(
            AiRuntimeScaleOutRequestRecord request,
            string? runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            Console.WriteLine(
                $"[SCALEOUT REQUEUE ENTER] RequestId='{request.RequestId}', SharedRunId='{request.SharedRunId}', RuntimeInstanceId='{runtimeInstanceId}', ControlPlaneId='{request.ControlPlaneId}', PipelineKey='{request.PipelineKey}', TenantId='{request.TenantId}', TenantGroupId='{request.TenantGroupId}'.");

            var allRuns =
                await this.sharedRunStore
                    .ListAsync(
                        includeCancelled: false,
                        includeCompleted: false,
                        includeFailed: false,
                        cancellationToken)
                    .ConfigureAwait(false);

            Console.WriteLine(
                $"[SCALEOUT REQUEUE STORE LIST] RequestId='{request.RequestId}', SharedRunId='{request.SharedRunId}', AllRunCount='{allRuns.Count}'.");

            foreach (var run in allRuns.Where(item =>
                string.Equals(item.SharedRunId, request.SharedRunId, StringComparison.Ordinal) ||
                item.Status == AiSharedRunStatus.ScaleOutRequested))
            {
                Console.WriteLine(
                    $"[SCALEOUT REQUEUE RUN INSPECT] RequestId='{request.RequestId}', RequestSharedRunId='{request.SharedRunId}', RunSharedRunId='{run.SharedRunId}', Status='{run.Status}', ControlPlaneId='{run.ControlPlaneId}', PipelineKey='{run.PipelineKey}', TenantId='{run.ExecutionContextSnapshot.TenantId}', TenantGroupId='{run.ExecutionContextSnapshot.TenantGroupId}', AssignedRuntimeInstanceId='{run.AssignedRuntimeInstanceId}', LocalRunId='{run.LocalRunId}', ExecutionId='{run.ExecutionId}'.");
            }

            if (!string.IsNullOrWhiteSpace(request.SharedRunId))
            {
                return await this.RequeueLinkedSharedRunAsync(
                        request,
                        runtimeInstanceId,
                        allRuns,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return await this.RequeueBacklogSharedRunsAsync(
                    request,
                    runtimeInstanceId,
                    allRuns,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Requeues the exact linked shared run when a scale-out request was created for a specific blocked run.
        /// </summary>
        /// <param name="request">The fulfilled scale-out request.</param>
        /// <param name="runtimeInstanceId">The runtime instance id created by scale-out.</param>
        /// <param name="allRuns">The currently active shared runs.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The requeue result.</returns>
        private async Task<AiScaleOutFulfilledRunRequeueResult> RequeueLinkedSharedRunAsync(
            AiRuntimeScaleOutRequestRecord request,
            string? runtimeInstanceId,
            IReadOnlyCollection<AiSharedRunRecord> allRuns,
            CancellationToken cancellationToken)
        {
            var linkedSharedRun =
                allRuns.SingleOrDefault(sharedRun => string.Equals(
                    sharedRun.SharedRunId,
                    request.SharedRunId,
                    StringComparison.Ordinal));

            if (linkedSharedRun is null)
            {
                Console.WriteLine(
                    $"[SCALEOUT REQUEUE LINKED MISSING] RequestId='{request.RequestId}', SharedRunId='{request.SharedRunId}'.");

                return AiScaleOutFulfilledRunRequeueResult.NoLinkedSharedRun(request.SharedRunId);
            }

            Console.WriteLine(
                $"[SCALEOUT REQUEUE LINKED LOOKUP] RequestId='{request.RequestId}', RequestSharedRunId='{request.SharedRunId}', Found='True'.");

            if (linkedSharedRun.Status != AiSharedRunStatus.ScaleOutRequested)
            {
                var existingQueueItem =
                    await this.sharedQueue
                        .GetAsync(
                            linkedSharedRun.SharedRunId,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (existingQueueItem is not null)
                {
                    var markedExisting =
                        await this.sharedRunStore
                            .MarkRequeuedAfterScaleOutAsync(
                                linkedSharedRun.SharedRunId,
                                "Scale-out fulfilled; linked shared run already had a queue item.",
                                existingQueueItem.Metadata,
                                cancellationToken)
                            .ConfigureAwait(false);

                    Console.WriteLine(
                        $"[SCALEOUT SHARED RUN MARKED REQUEUED EXISTING LINKED STATUS] SharedRunId='{linkedSharedRun.SharedRunId}', Marked='{markedExisting is not null}', Status='{markedExisting?.Status}', ExistingQueueControlPlaneId='{existingQueueItem.ControlPlaneId}'.");

                    return AiScaleOutFulfilledRunRequeueResult.Succeeded(
                        linkedSharedRun.SharedRunId,
                        candidateCount: 1,
                        reason: $"Linked shared run '{linkedSharedRun.SharedRunId}' already has a shared queue item.");
                }

                return AiScaleOutFulfilledRunRequeueResult.Failed(
                    linkedSharedRun.SharedRunId,
                    candidateCount: 1,
                    reason: $"Linked shared run '{linkedSharedRun.SharedRunId}' is not waiting for scale-out. Status='{linkedSharedRun.Status}'.");
            }

            if (!IsUnassignedScaleOutRun(linkedSharedRun))
            {
                var existingQueueItem =
                    await this.sharedQueue
                        .GetAsync(
                            linkedSharedRun.SharedRunId,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (existingQueueItem is not null)
                {
                    var markedExisting =
                        await this.sharedRunStore
                            .MarkRequeuedAfterScaleOutAsync(
                                linkedSharedRun.SharedRunId,
                                "Scale-out fulfilled; linked assigned shared run already had a queue item.",
                                existingQueueItem.Metadata,
                                cancellationToken)
                            .ConfigureAwait(false);

                    Console.WriteLine(
                        $"[SCALEOUT SHARED RUN MARKED REQUEUED EXISTING ASSIGNED] SharedRunId='{linkedSharedRun.SharedRunId}', Marked='{markedExisting is not null}', Status='{markedExisting?.Status}', ExistingQueueControlPlaneId='{existingQueueItem.ControlPlaneId}'.");

                    return AiScaleOutFulfilledRunRequeueResult.Succeeded(
                        linkedSharedRun.SharedRunId,
                        candidateCount: 1,
                        reason: $"Linked shared run '{linkedSharedRun.SharedRunId}' already has a shared queue item.");
                }

                return AiScaleOutFulfilledRunRequeueResult.Failed(
                    linkedSharedRun.SharedRunId,
                    candidateCount: 1,
                    reason: $"Linked shared run '{linkedSharedRun.SharedRunId}' is already assigned or executing. AssignedRuntimeInstanceId='{linkedSharedRun.AssignedRuntimeInstanceId}', LocalRunId='{linkedSharedRun.LocalRunId}', ExecutionId='{linkedSharedRun.ExecutionId}'.");
            }

            var matchesLinkedScope =
                IsSameLinkedRunScope(
                    request,
                    linkedSharedRun);

            if (!matchesLinkedScope)
            {
                return AiScaleOutFulfilledRunRequeueResult.Failed(
                    linkedSharedRun.SharedRunId,
                    candidateCount: 1,
                    reason: $"Linked shared run '{linkedSharedRun.SharedRunId}' does not match scale-out request tenant or pipeline scope. RequestControlPlaneId='{request.ControlPlaneId}', RunControlPlaneId='{linkedSharedRun.ControlPlaneId}', RequestPipelineKey='{request.PipelineKey}', RunPipelineKey='{linkedSharedRun.PipelineKey}', RequestTenantId='{request.TenantId}', RunTenantId='{linkedSharedRun.ExecutionContextSnapshot.TenantId}', RequestTenantGroupId='{request.TenantGroupId}', RunTenantGroupId='{linkedSharedRun.ExecutionContextSnapshot.TenantGroupId}'.");
            }

            Console.WriteLine(
                $"[SCALEOUT REQUEUE LINKED MATCHED] RequestId='{request.RequestId}', SharedRunId='{linkedSharedRun.SharedRunId}', Status='{linkedSharedRun.Status}', ScopeMatched='True', RequestControlPlaneId='{request.ControlPlaneId}', OriginalSharedRunControlPlaneId='{linkedSharedRun.ControlPlaneId}'.");

            var requeued =
                await this.RequeueSingleRunAsync(
                        request,
                        linkedSharedRun,
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!requeued)
            {
                return AiScaleOutFulfilledRunRequeueResult.Failed(
                    linkedSharedRun.SharedRunId,
                    candidateCount: 1,
                    reason: $"Linked shared run '{linkedSharedRun.SharedRunId}' was found but was not requeued.");
            }

            return AiScaleOutFulfilledRunRequeueResult.Succeeded(
                linkedSharedRun.SharedRunId,
                candidateCount: 1,
                reason: $"Linked shared run '{linkedSharedRun.SharedRunId}' was requeued after scale-out fulfillment.");
        }

        /// <summary>
        /// Requeues backlog shared runs for scale-out requests that are not tied to one exact shared run.
        /// </summary>
        /// <param name="request">The fulfilled scale-out request.</param>
        /// <param name="runtimeInstanceId">The runtime instance id created by scale-out.</param>
        /// <param name="allRuns">The currently active shared runs.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The requeue result.</returns>
        private async Task<AiScaleOutFulfilledRunRequeueResult> RequeueBacklogSharedRunsAsync(
            AiRuntimeScaleOutRequestRecord request,
            string? runtimeInstanceId,
            IReadOnlyCollection<AiSharedRunRecord> allRuns,
            CancellationToken cancellationToken)
        {
            var candidateSharedRuns =
                allRuns
                    .Where(sharedRun => IsBacklogRequeueCandidateSharedRun(request, sharedRun))
                    .OrderBy(sharedRun => sharedRun.SubmittedAtUtc)
                    .ThenBy(sharedRun => sharedRun.SharedRunId, StringComparer.Ordinal)
                    .Take(MaxBacklogRequeueCount)
                    .ToArray();

            if (candidateSharedRuns.Length == 0)
            {
                return AiScaleOutFulfilledRunRequeueResult.NoLinkedSharedRun(request.SharedRunId);
            }

            var requeuedAny =
                false;

            foreach (var sharedRun in candidateSharedRuns)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var requeued =
                    await this.RequeueSingleRunAsync(
                            request,
                            sharedRun,
                            runtimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                requeuedAny = requeuedAny || requeued;
            }

            if (!requeuedAny)
            {
                return AiScaleOutFulfilledRunRequeueResult.Failed(
                    candidateSharedRuns[0].SharedRunId,
                    candidateSharedRuns.Length,
                    "Backlog shared runs were found but none were requeued.");
            }

            return AiScaleOutFulfilledRunRequeueResult.Succeeded(
                candidateSharedRuns[0].SharedRunId,
                candidateSharedRuns.Length,
                "One or more backlog shared runs were requeued after scale-out fulfillment.");
        }

        /// <summary>
        /// Determines whether a shared run is still unassigned and waiting for scale-out.
        /// </summary>
        /// <param name="sharedRun">The shared run.</param>
        /// <returns><see langword="true" /> when the run is unassigned; otherwise, <see langword="false" />.</returns>
        private static bool IsUnassignedScaleOutRun(
            AiSharedRunRecord sharedRun)
        {
            return string.IsNullOrWhiteSpace(sharedRun.AssignedRuntimeInstanceId) &&
                string.IsNullOrWhiteSpace(sharedRun.LocalRunId) &&
                string.IsNullOrWhiteSpace(sharedRun.ExecutionId);
        }

        /// <summary>
        /// Determines whether a shared run belongs to the same scale-out request scope.
        /// </summary>
        /// <param name="request">The fulfilled scale-out request.</param>
        /// <param name="sharedRun">The shared run.</param>
        /// <returns><see langword="true" /> when the run matches the scale-out request scope; otherwise, <see langword="false" />.</returns>
        private static bool IsSameScaleOutScope(
            AiRuntimeScaleOutRequestRecord request,
            AiSharedRunRecord sharedRun)
        {
            if (!string.Equals(sharedRun.ControlPlaneId, request.ControlPlaneId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(sharedRun.PipelineKey, request.PipelineKey, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(sharedRun.ExecutionContextSnapshot.TenantId, request.TenantId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(sharedRun.ExecutionContextSnapshot.TenantGroupId, request.TenantGroupId, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Determines whether a shared run belongs to the same scale-out backlog requeue scope.
        /// </summary>
        /// <param name="request">The fulfilled scale-out request.</param>
        /// <param name="sharedRun">The shared run.</param>
        /// <returns><see langword="true" /> when the run should be requeued; otherwise, <see langword="false" />.</returns>
        private static bool IsBacklogRequeueCandidateSharedRun(
            AiRuntimeScaleOutRequestRecord request,
            AiSharedRunRecord sharedRun)
        {
            if (sharedRun.Status != AiSharedRunStatus.ScaleOutRequested)
            {
                return false;
            }

            if (!IsUnassignedScaleOutRun(sharedRun))
            {
                return false;
            }

            return IsSameScaleOutScope(
                request,
                sharedRun);
        }

        /// <summary>
        /// Requeues a single shared run when it is still waiting for scale-out.
        /// </summary>
        /// <param name="request">The fulfilled scale-out request.</param>
        /// <param name="sharedRun">The shared run to requeue.</param>
        /// <param name="runtimeInstanceId">The runtime instance id created by scale-out.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><see langword="true" /> when the shared run was requeued or was already queued; otherwise, <see langword="false" />.</returns>
        private async Task<bool> RequeueSingleRunAsync(
            AiRuntimeScaleOutRequestRecord request,
            AiSharedRunRecord sharedRun,
            string? runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            var queueControlPlaneId =
                string.IsNullOrWhiteSpace(request.ControlPlaneId)
                    ? sharedRun.ControlPlaneId
                    : request.ControlPlaneId;

            Console.WriteLine(
                $"[SCALEOUT REQUEUE SINGLE BEGIN] RequestId='{request.RequestId}', SharedRunId='{sharedRun.SharedRunId}', RuntimeInstanceId='{runtimeInstanceId}', QueueControlPlaneId='{queueControlPlaneId}', RequestControlPlaneId='{request.ControlPlaneId}', OriginalSharedRunControlPlaneId='{sharedRun.ControlPlaneId}'.");

            var existingQueueItem =
                await this.sharedQueue
                    .GetAsync(
                        sharedRun.SharedRunId,
                        cancellationToken)
                    .ConfigureAwait(false);

            Console.WriteLine(
                $"[SCALEOUT REQUEUE QUEUE GET] SharedRunId='{sharedRun.SharedRunId}', ExistingQueueItem='{existingQueueItem is not null}', ExistingQueueControlPlaneId='{existingQueueItem?.ControlPlaneId}', ExpectedQueueControlPlaneId='{queueControlPlaneId}'.");

            if (existingQueueItem is not null)
            {
                if (string.Equals(
                    existingQueueItem.ControlPlaneId,
                    queueControlPlaneId,
                    StringComparison.Ordinal))
                {
                    var markedExisting =
                        await this.sharedRunStore
                            .MarkRequeuedAfterScaleOutAsync(
                                sharedRun.SharedRunId,
                                "Scale-out fulfilled; shared run already had a queue item.",
                                existingQueueItem.Metadata,
                                cancellationToken)
                            .ConfigureAwait(false);

                    Console.WriteLine(
                        $"[SCALEOUT SHARED RUN MARKED REQUEUED EXISTING QUEUE] SharedRunId='{sharedRun.SharedRunId}', Marked='{markedExisting is not null}', Status='{markedExisting?.Status}', QueueControlPlaneId='{queueControlPlaneId}'.");

                    return markedExisting is not null;
                }

                Console.WriteLine(
                    $"[SCALEOUT REQUEUE QUEUE EXISTING SCOPE MISMATCH] SharedRunId='{sharedRun.SharedRunId}', ExistingQueueControlPlaneId='{existingQueueItem.ControlPlaneId}', ExpectedQueueControlPlaneId='{queueControlPlaneId}'.");

                return false;
            }

            var now =
                DateTimeOffset.UtcNow;

            var metadata =
                new Dictionary<string, string>(
                    sharedRun.Metadata,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["scaleOutRequestId"] = request.RequestId,
                    ["scaleOutRequeued"] = "true",
                    ["scaleOutRequestControlPlaneId"] = request.ControlPlaneId ?? string.Empty,
                    ["scaleOutOriginalSharedRunControlPlaneId"] = sharedRun.ControlPlaneId ?? string.Empty,
                    ["scaleOutQueueControlPlaneId"] = queueControlPlaneId ?? string.Empty,
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = sharedRun.ExecutionContextSnapshot.TenantId ?? string.Empty,
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = sharedRun.ExecutionContextSnapshot.TenantGroupId ?? string.Empty,
                    ["pipelineKey"] = sharedRun.PipelineKey ?? string.Empty
                };

            if (!string.IsNullOrWhiteSpace(runtimeInstanceId))
            {
                metadata["scaleOutRuntimeInstanceId"] = runtimeInstanceId;
            }

            try
            {
                await this.sharedQueue
                    .EnqueueAsync(
                        new AiSharedQueueItem
                        {
                            SharedRunId = sharedRun.SharedRunId,
                            ControlPlaneId = queueControlPlaneId,
                            Status = AiSharedQueueItemStatus.Pending,
                            ExecutionContextSnapshot = sharedRun.ExecutionContextSnapshot,
                            PipelineKey = sharedRun.PipelineKey,
                            Priority = 0,
                            EnqueuedAtUtc = now,
                            UpdatedAtUtc = now,
                            Reason = "Scale-out fulfilled; shared run requeued for dispatch.",
                            Metadata = metadata
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                Console.WriteLine(
                    $"[SCALEOUT REQUEUE ENQUEUED] SharedRunId='{sharedRun.SharedRunId}', QueueControlPlaneId='{queueControlPlaneId}', RequestControlPlaneId='{request.ControlPlaneId}', OriginalSharedRunControlPlaneId='{sharedRun.ControlPlaneId}', PipelineKey='{sharedRun.PipelineKey}', TenantId='{sharedRun.ExecutionContextSnapshot.TenantId}', TenantGroupId='{sharedRun.ExecutionContextSnapshot.TenantGroupId}'.");

                var markedRequeued =
                    await this.sharedRunStore
                        .MarkRequeuedAfterScaleOutAsync(
                            sharedRun.SharedRunId,
                            "Scale-out fulfilled; shared run requeued for dispatch.",
                            metadata,
                            cancellationToken)
                        .ConfigureAwait(false);

                Console.WriteLine(
                    $"[SCALEOUT SHARED RUN MARKED REQUEUED] SharedRunId='{sharedRun.SharedRunId}', Marked='{markedRequeued is not null}', Status='{markedRequeued?.Status}', QueueControlPlaneId='{queueControlPlaneId}'.");

                return markedRequeued is not null;
            }
            catch (InvalidOperationException exception)
                when (IsDuplicateQueueItemException(
                    exception,
                    sharedRun.SharedRunId))
            {
                Console.WriteLine(
                    $"[SCALEOUT REQUEUE DUPLICATE] SharedRunId='{sharedRun.SharedRunId}', QueueControlPlaneId='{queueControlPlaneId}', RequestControlPlaneId='{request.ControlPlaneId}', OriginalSharedRunControlPlaneId='{sharedRun.ControlPlaneId}'.");

                var markedDuplicate =
                    await this.sharedRunStore
                        .MarkRequeuedAfterScaleOutAsync(
                            sharedRun.SharedRunId,
                            "Scale-out fulfilled; duplicate queue item treated as already requeued.",
                            metadata,
                            cancellationToken)
                        .ConfigureAwait(false);

                Console.WriteLine(
                    $"[SCALEOUT SHARED RUN MARKED REQUEUED DUPLICATE] SharedRunId='{sharedRun.SharedRunId}', Marked='{markedDuplicate is not null}', Status='{markedDuplicate?.Status}', QueueControlPlaneId='{queueControlPlaneId}'.");

                return markedDuplicate is not null;
            }
        }

        /// <summary>
        /// Determines whether an exception represents an idempotent duplicate shared queue item.
        /// </summary>
        /// <param name="exception">The exception to inspect.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <returns><see langword="true" /> when the exception is a duplicate queue item error; otherwise, <see langword="false" />.</returns>
        private static bool IsDuplicateQueueItemException(
            InvalidOperationException exception,
            string sharedRunId)
        {
            return exception.Message.Contains(
                $"Shared queue item '{sharedRunId}' already exists.",
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Determines whether an exact linked shared run belongs to the same tenant and pipeline scope.
        /// </summary>
        /// <param name="request">The fulfilled scale-out request.</param>
        /// <param name="sharedRun">The linked shared run.</param>
        /// <returns><see langword="true" /> when the run matches the tenant and pipeline scope; otherwise, <see langword="false" />.</returns>
        private static bool IsSameLinkedRunScope(
            AiRuntimeScaleOutRequestRecord request,
            AiSharedRunRecord sharedRun)
        {
            if (!string.Equals(sharedRun.PipelineKey, request.PipelineKey, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(sharedRun.ExecutionContextSnapshot.TenantId, request.TenantId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(sharedRun.ExecutionContextSnapshot.TenantGroupId, request.TenantGroupId, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }
    }
}