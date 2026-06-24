using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Ownership
{
    /// <summary>
    /// Default shared run ownership resolver.
    /// </summary>
    /// <remarks>
    /// This resolver is read-only.
    /// It scans terminal and non-terminal shared queue items and uses the shared run store
    /// to validate assigned runtime ownership, local run id, execution id, and tenant metadata.
    ///
    /// It does not requeue, recover, mutate ownership, or change runtime execution state.
    /// </remarks>
    public sealed class AiSharedRunOwnershipResolver : IAiSharedRunOwnershipResolver
    {
        private readonly IAiSharedQueue sharedQueue;
        private readonly IAiSharedRunStore sharedRunStore;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiSharedRunOwnershipResolver"/> class.
        /// </summary>
        /// <param name="sharedQueue">The shared queue.</param>
        /// <param name="sharedRunStore">The shared run store.</param>
        public AiSharedRunOwnershipResolver(
            IAiSharedQueue sharedQueue,
            IAiSharedRunStore sharedRunStore)
        {
            ArgumentNullException.ThrowIfNull(sharedQueue);
            ArgumentNullException.ThrowIfNull(sharedRunStore);

            this.sharedQueue = sharedQueue;
            this.sharedRunStore = sharedRunStore;
        }

        /// <inheritdoc />
        public async Task<AiSharedRunOwnershipResolutionResult> ResolveAsync(
            AiSharedRunOwnershipResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.RuntimeInstanceId);

            if (string.IsNullOrWhiteSpace(request.LocalRunId) &&
                string.IsNullOrWhiteSpace(request.ExecutionId))
            {
                return CreateUnresolved(
                    request,
                    "missing-local-run-id-and-execution-id");
            }

            var queueItems = await sharedQueue
                .ListAsync(includeTerminal: true, cancellationToken)
                .ConfigureAwait(false);

            foreach (var queueItem in queueItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!MatchesQueueOwnership(queueItem, request))
                {
                    continue;
                }

                var sharedRun = await sharedRunStore
                    .GetAsync(queueItem.SharedRunId, cancellationToken)
                    .ConfigureAwait(false);

                if (sharedRun is null)
                {
                    return CreateResolvedFromQueueOnly(
                        request,
                        queueItem,
                        "queue-ownership-resolved-shared-run-missing");
                }

                if (!MatchesSharedRunOwnership(sharedRun, request))
                {
                    continue;
                }

                return CreateResolved(
                    request,
                    queueItem,
                    sharedRun);
            }

            return CreateUnresolved(
                request,
                "shared-run-ownership-not-found");
        }

        /// <summary>
        /// Determines whether a shared queue item matches the requested runtime ownership.
        /// </summary>
        /// <param name="item">The shared queue item.</param>
        /// <param name="request">The ownership resolution request.</param>
        /// <returns><c>true</c> when the queue item matches; otherwise, <c>false</c>.</returns>
        private static bool MatchesQueueOwnership(
            AiSharedQueueItem item,
            AiSharedRunOwnershipResolutionRequest request)
        {
            if (!string.Equals(
                    item.ClaimedByRuntimeInstanceId,
                    request.RuntimeInstanceId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (!MatchesTenant(
                    item.ExecutionContextSnapshot.TenantId,
                    request.TenantId))
            {
                return false;
            }

            if (!MatchesTenant(
                    item.ExecutionContextSnapshot.TenantGroupId,
                    request.TenantGroupId))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Determines whether a shared run record matches the requested runtime ownership.
        /// </summary>
        /// <param name="record">The shared run record.</param>
        /// <param name="request">The ownership resolution request.</param>
        /// <returns><c>true</c> when the shared run record matches; otherwise, <c>false</c>.</returns>
        private static bool MatchesSharedRunOwnership(
            AiSharedRunRecord record,
            AiSharedRunOwnershipResolutionRequest request)
        {
            if (!string.Equals(
                    record.AssignedRuntimeInstanceId,
                    request.RuntimeInstanceId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(request.LocalRunId) &&
                !string.Equals(
                    record.LocalRunId,
                    request.LocalRunId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(request.ExecutionId) &&
                !string.Equals(
                    record.ExecutionId,
                    request.ExecutionId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (!MatchesTenant(
                    record.ExecutionContextSnapshot.TenantId,
                    request.TenantId))
            {
                return false;
            }

            if (!MatchesTenant(
                    record.ExecutionContextSnapshot.TenantGroupId,
                    request.TenantGroupId))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Determines whether an optional tenant value matches.
        /// </summary>
        /// <param name="actual">The actual tenant value.</param>
        /// <param name="expected">The expected tenant value.</param>
        /// <returns><c>true</c> when matching; otherwise, <c>false</c>.</returns>
        private static bool MatchesTenant(
            string? actual,
            string? expected)
        {
            return string.IsNullOrWhiteSpace(expected) ||
                   string.Equals(actual, expected, StringComparison.Ordinal);
        }

        /// <summary>
        /// Creates a resolved ownership result from both queue and shared run state.
        /// </summary>
        /// <param name="request">The ownership resolution request.</param>
        /// <param name="queueItem">The shared queue item.</param>
        /// <param name="sharedRun">The shared run record.</param>
        /// <returns>The ownership resolution result.</returns>
        private static AiSharedRunOwnershipResolutionResult CreateResolved(
            AiSharedRunOwnershipResolutionRequest request,
            AiSharedQueueItem queueItem,
            AiSharedRunRecord sharedRun)
        {
            return new AiSharedRunOwnershipResolutionResult
            {
                Resolved = true,
                SharedRunId = sharedRun.SharedRunId,
                RuntimeInstanceId = request.RuntimeInstanceId,
                LocalRunId = sharedRun.LocalRunId ?? request.LocalRunId,
                ExecutionId = sharedRun.ExecutionId ?? request.ExecutionId,
                TenantId = sharedRun.ExecutionContextSnapshot.TenantId,
                TenantGroupId = sharedRun.ExecutionContextSnapshot.TenantGroupId,
                QueueStatus = queueItem.Status,
                SharedRunStatus = sharedRun.Status,
                ClaimToken = queueItem.ClaimToken,
                CanRecover = IsRecoverable(queueItem.Status, sharedRun.Status),
                Reason = IsRecoverable(queueItem.Status, sharedRun.Status)
                    ? "shared-run-ownership-resolved"
                    : "shared-run-ownership-resolved-not-recoverable"
            };
        }

        /// <summary>
        /// Creates a partial resolved ownership result when queue state is available but shared run state is missing.
        /// </summary>
        /// <param name="request">The ownership resolution request.</param>
        /// <param name="queueItem">The shared queue item.</param>
        /// <param name="reason">The resolution reason.</param>
        /// <returns>The ownership resolution result.</returns>
        private static AiSharedRunOwnershipResolutionResult CreateResolvedFromQueueOnly(
            AiSharedRunOwnershipResolutionRequest request,
            AiSharedQueueItem queueItem,
            string reason)
        {
            return new AiSharedRunOwnershipResolutionResult
            {
                Resolved = true,
                SharedRunId = queueItem.SharedRunId,
                RuntimeInstanceId = request.RuntimeInstanceId,
                LocalRunId = request.LocalRunId,
                ExecutionId = request.ExecutionId,
                TenantId = queueItem.ExecutionContextSnapshot.TenantId,
                TenantGroupId = queueItem.ExecutionContextSnapshot.TenantGroupId,
                QueueStatus = queueItem.Status,
                ClaimToken = queueItem.ClaimToken,
                CanRecover = false,
                Reason = reason
            };
        }

        /// <summary>
        /// Creates an unresolved ownership result.
        /// </summary>
        /// <param name="request">The ownership resolution request.</param>
        /// <param name="reason">The unresolved reason.</param>
        /// <returns>The ownership resolution result.</returns>
        private static AiSharedRunOwnershipResolutionResult CreateUnresolved(
            AiSharedRunOwnershipResolutionRequest request,
            string reason)
        {
            return new AiSharedRunOwnershipResolutionResult
            {
                Resolved = false,
                RuntimeInstanceId = request.RuntimeInstanceId,
                LocalRunId = request.LocalRunId,
                ExecutionId = request.ExecutionId,
                TenantId = request.TenantId,
                TenantGroupId = request.TenantGroupId,
                CanRecover = false,
                Reason = reason
            };
        }

        /// <summary>
        /// Determines whether the resolved ownership state is recoverable.
        /// </summary>
        /// <param name="queueStatus">The shared queue item status.</param>
        /// <param name="sharedRunStatus">The shared run status.</param>
        /// <returns><c>true</c> when recoverable; otherwise, <c>false</c>.</returns>
        private static bool IsRecoverable(
            AiSharedQueueItemStatus queueStatus,
            AiSharedRunStatus sharedRunStatus)
        {
            return queueStatus == AiSharedQueueItemStatus.Dispatched &&
                   sharedRunStatus == AiSharedRunStatus.Dispatched;
        }
    }
}