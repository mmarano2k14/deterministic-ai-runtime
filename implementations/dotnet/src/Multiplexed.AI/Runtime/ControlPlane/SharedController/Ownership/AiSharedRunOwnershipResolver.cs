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
                string.IsNullOrWhiteSpace(request.ExecutionId) &&
                string.IsNullOrWhiteSpace(request.SharedRunId))
            {
                return CreateUnresolved(
                    request,
                    "missing-shared-run-id-local-run-id-and-execution-id");
            }

            var directResolution =
                await this.TryResolveBySharedRunIdAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (directResolution is not null)
            {
                return directResolution;
            }

            var queueItems = await this.sharedQueue
                .ListAsync(includeTerminal: true, cancellationToken)
                .ConfigureAwait(false);

            foreach (var queueItem in queueItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!MatchesQueueOwnership(queueItem, request))
                {
                    continue;
                }

                var sharedRun = await this.sharedRunStore
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
                string.IsNullOrWhiteSpace(request.SharedRunId)
                    ? "shared-run-ownership-not-found"
                    : "shared-run-ownership-not-found-after-direct-shared-run-id-lookup");
        }

        /// <summary>
        /// Attempts to resolve ownership directly by shared run identifier.
        /// </summary>
        /// <param name="request">The ownership resolution request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The resolved ownership result, or null when direct resolution cannot be used.</returns>
        private async Task<AiSharedRunOwnershipResolutionResult?> TryResolveBySharedRunIdAsync(
            AiSharedRunOwnershipResolutionRequest request,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.SharedRunId))
            {
                return null;
            }

            var queueItem = await this.sharedQueue
                .GetAsync(request.SharedRunId, cancellationToken)
                .ConfigureAwait(false);

            var sharedRun = await this.sharedRunStore
                .GetAsync(request.SharedRunId, cancellationToken)
                .ConfigureAwait(false);

            if (queueItem is null &&
                sharedRun is null)
            {
                return CreateUnresolved(
                    request,
                    "direct-shared-run-id-queue-and-shared-run-missing");
            }

            if (queueItem is null)
            {
                return CreateUnresolved(
                    request,
                    "direct-shared-run-id-queue-item-missing");
            }

            if (!MatchesQueueOwnership(queueItem, request))
            {
                return CreateUnresolved(
                    request,
                    "direct-shared-run-id-queue-tenant-mismatch");
            }

            if (sharedRun is null)
            {
                return CreateResolvedFromQueueOnly(
                    request,
                    queueItem,
                    "direct-shared-run-id-queue-ownership-resolved-shared-run-missing");
            }

            if (!MatchesSharedRunOwnership(sharedRun, request))
            {
                return CreateUnresolved(
                    request,
                    "direct-shared-run-id-shared-run-ownership-mismatch");
            }

            return CreateResolved(
                request,
                queueItem,
                sharedRun);
        }

        /// <summary>
        /// Determines whether a shared queue item is a candidate for the requested ownership.
        /// </summary>
        /// <remarks>
        /// For local runtime dispatch, the shared queue claim can be owned directly by the runtime instance.
        /// For remote HTTP/process-host dispatch, the shared queue claim may be owned by the control-plane
        /// dispatcher or pump while the target runtime ownership is stored on the shared run record.
        ///
        /// Therefore this method only rejects tenant mismatches. Runtime/local execution ownership is
        /// validated later against the shared run store.
        /// </remarks>
        /// <param name="item">The shared queue item.</param>
        /// <param name="request">The ownership resolution request.</param>
        /// <returns><c>true</c> when the queue item can be inspected; otherwise, <c>false</c>.</returns>
        private static bool MatchesQueueOwnership(
            AiSharedQueueItem item,
            AiSharedRunOwnershipResolutionRequest request)
        {
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
        /// <remarks>
        /// HTTP/process-host dispatch can make shared-run assignment visible before the final
        /// runtime execution id has been propagated back to the shared run store.
        ///
        /// In that case, the runtime execution index is the durable source for the in-flight
        /// execution id, while the shared run store still proves runtime/local-run ownership.
        /// Therefore an empty shared-run execution id does not prevent ownership resolution.
        /// </remarks>
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

            if (!MatchesExecutionOwnership(
                    record.ExecutionId,
                    request.ExecutionId))
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
        /// Determines whether the shared-run execution id matches the runtime execution request.
        /// </summary>
        /// <remarks>
        /// A missing shared-run execution id is accepted because the runtime execution index
        /// can already hold the execution id for an in-flight local runtime execution.
        /// </remarks>
        /// <param name="sharedRunExecutionId">The execution id stored on the shared run.</param>
        /// <param name="requestedExecutionId">The execution id from the runtime execution index.</param>
        /// <returns><c>true</c> when execution ownership can be matched; otherwise, <c>false</c>.</returns>
        private static bool MatchesExecutionOwnership(
            string? sharedRunExecutionId,
            string? requestedExecutionId)
        {
            if (string.IsNullOrWhiteSpace(requestedExecutionId))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(sharedRunExecutionId))
            {
                return true;
            }

            return string.Equals(
                sharedRunExecutionId,
                requestedExecutionId,
                StringComparison.Ordinal);
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
            var executionId =
                string.IsNullOrWhiteSpace(sharedRun.ExecutionId)
                    ? request.ExecutionId
                    : sharedRun.ExecutionId;

            var localRunId =
                string.IsNullOrWhiteSpace(sharedRun.LocalRunId)
                    ? request.LocalRunId
                    : sharedRun.LocalRunId;

            var canRecover =
                IsRecoverable(
                    queueItem.Status,
                    sharedRun.Status);

            return new AiSharedRunOwnershipResolutionResult
            {
                Resolved = true,
                SharedRunId = sharedRun.SharedRunId,
                RuntimeInstanceId = request.RuntimeInstanceId,
                LocalRunId = localRunId,
                ExecutionId = executionId,
                TenantId = sharedRun.ExecutionContextSnapshot.TenantId,
                TenantGroupId = sharedRun.ExecutionContextSnapshot.TenantGroupId,
                QueueStatus = queueItem.Status,
                SharedRunStatus = sharedRun.Status,
                ClaimToken = queueItem.ClaimToken,
                CanRecover = canRecover,
                Reason = canRecover
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
                SharedRunId = request.SharedRunId,
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