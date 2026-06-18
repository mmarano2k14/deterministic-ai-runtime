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

            if (string.IsNullOrWhiteSpace(request.SharedRunId))
            {
                return;
            }

            var sharedRun =
                await this.sharedRunStore
                    .GetAsync(
                        request.SharedRunId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (sharedRun is null)
            {
                return;
            }

            if (sharedRun.Status != AiSharedRunStatus.ScaleOutRequested)
            {
                return;
            }

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
                    ["tenant.id"] = sharedRun.ExecutionContextSnapshot.TenantId ?? string.Empty,
                    ["tenant.group.id"] = sharedRun.ExecutionContextSnapshot.TenantGroupId ?? string.Empty,
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