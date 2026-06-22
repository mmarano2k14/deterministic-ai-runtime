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
            this.sharedRunStore =
                sharedRunStore
                ?? throw new ArgumentNullException(nameof(sharedRunStore));

            this.sharedQueue =
                sharedQueue
                ?? throw new ArgumentNullException(nameof(sharedQueue));
        }

        /// <inheritdoc />
        public async Task RequeueAsync(
            AiRuntimeScaleOutRequestRecord request,
            string? runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            cancellationToken.ThrowIfCancellationRequested();

            var candidateSharedRuns =
                await this.GetCandidateSharedRunsAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);

            foreach (var sharedRun in candidateSharedRuns)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await this.RequeueSingleRunAsync(
                        request,
                        sharedRun,
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Gets shared runs that are still waiting for scale-out in the same tenant and pipeline scope.
        /// </summary>
        /// <param name="request">The fulfilled scale-out request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The shared runs to requeue.</returns>
        private async Task<IReadOnlyList<AiSharedRunRecord>> GetCandidateSharedRunsAsync(
            AiRuntimeScaleOutRequestRecord request,
            CancellationToken cancellationToken)
        {
            var allRuns =
                await this.sharedRunStore
                    .ListAsync(
                        includeCancelled: false,
                        includeCompleted: false,
                        includeFailed: false,
                        cancellationToken)
                    .ConfigureAwait(false);

            return allRuns
                .Where(sharedRun => IsRequeueCandidateSharedRun(request, sharedRun))
                .OrderBy(sharedRun => sharedRun.SubmittedAtUtc)
                .ThenBy(sharedRun => sharedRun.SharedRunId, StringComparer.Ordinal)
                .Take(MaxBacklogRequeueCount)
                .ToArray();
        }

        /// <summary>
        /// Determines whether a shared run belongs to the same scale-out requeue scope.
        /// </summary>
        /// <param name="request">The fulfilled scale-out request.</param>
        /// <param name="sharedRun">The shared run.</param>
        /// <returns><see langword="true" /> when the run should be requeued; otherwise, <see langword="false" />.</returns>
        private static bool IsRequeueCandidateSharedRun(
            AiRuntimeScaleOutRequestRecord request,
            AiSharedRunRecord sharedRun)
        {
            if (sharedRun.Status != AiSharedRunStatus.ScaleOutRequested)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(sharedRun.AssignedRuntimeInstanceId) ||
                !string.IsNullOrWhiteSpace(sharedRun.LocalRunId) ||
                !string.IsNullOrWhiteSpace(sharedRun.ExecutionId))
            {
                return false;
            }

            if (!string.Equals(
                    sharedRun.ControlPlaneId,
                    request.ControlPlaneId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(
                    sharedRun.PipelineKey,
                    request.PipelineKey,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(
                    sharedRun.ExecutionContextSnapshot.TenantId,
                    request.TenantId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(
                    sharedRun.ExecutionContextSnapshot.TenantGroupId,
                    request.TenantGroupId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Requeues a single shared run when it is still waiting for scale-out.
        /// </summary>
        /// <param name="request">The fulfilled scale-out request.</param>
        /// <param name="sharedRun">The shared run to requeue.</param>
        /// <param name="runtimeInstanceId">The runtime instance id created by scale-out.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task RequeueSingleRunAsync(
            AiRuntimeScaleOutRequestRecord request,
            AiSharedRunRecord sharedRun,
            string? runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            var existingQueueItem =
                await this.sharedQueue
                    .GetAsync(
                        sharedRun.SharedRunId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (existingQueueItem is not null)
            {
                return;
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
                            ControlPlaneId = sharedRun.ControlPlaneId,
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
            }
            catch (InvalidOperationException exception)
                when (IsDuplicateQueueItemException(
                    exception,
                    sharedRun.SharedRunId))
            {
                return;
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
    }
}