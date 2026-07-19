using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using System.Collections.Concurrent;
using System.Globalization;

namespace Multiplexed.AI.Runtime.ControlPlane.ShareQueue
{
    /// <summary>
    /// In-memory implementation of the shared/global queue.
    /// </summary>
    /// <remarks>
    /// This implementation is intended for unit tests, local demos, and single-process
    /// development scenarios.
    ///
    /// Distributed deployments should use a Redis-backed implementation with atomic
    /// enqueue, claim, dispatch, requeue, and cancel transitions.
    /// </remarks>
    public sealed class InMemoryAiSharedQueue : IAiSharedQueue
    {
        private const string QueuePriorityMetadataKey = "queue.priority";

        private readonly ConcurrentDictionary<string, AiSharedQueueItem> _items =
            new(StringComparer.Ordinal);

        /// <inheritdoc />
        public Task<AiSharedQueueItem> EnqueueAsync(
            AiSharedQueueItem item,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.SharedRunId);

            cancellationToken.ThrowIfCancellationRequested();

            if (!_items.TryAdd(item.SharedRunId, item))
            {
                throw new InvalidOperationException(
                    $"Shared queue item '{item.SharedRunId}' already exists.");
            }

            return Task.FromResult(item);
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> GetAsync(
            string sharedRunId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);

            cancellationToken.ThrowIfCancellationRequested();

            _items.TryGetValue(sharedRunId, out var item);

            return Task.FromResult(item);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiSharedQueueItem>> ListAsync(
            bool includeTerminal = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var items = _items.Values
                .Where(item => includeTerminal || !IsTerminal(item.Status))
                .OrderBy(item => item.Priority)
                .ThenBy(item => item.EnqueuedAtUtc)
                .ThenBy(item => item.SharedRunId, StringComparer.Ordinal)
                .ToArray();

            return Task.FromResult<IReadOnlyList<AiSharedQueueItem>>(items);
        }


        /// <inheritdoc />
        public Task<AiSharedQueueItem?> ClaimAsync(
            string sharedRunId,
            AiSharedQueueClaimRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                request.RuntimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            while (true)
            {
                if (!_items.TryGetValue(
                        sharedRunId,
                        out var existing))
                {
                    return Task.FromResult<AiSharedQueueItem?>(
                        null);
                }

                if (existing.Status !=
                    AiSharedQueueItemStatus.Pending)
                {
                    return Task.FromResult<AiSharedQueueItem?>(
                        null);
                }

                if (!MatchesTenant(
                        existing,
                        request.TenantId))
                {
                    return Task.FromResult<AiSharedQueueItem?>(
                        null);
                }

                if (!MatchesPipeline(
                        existing,
                        request.PipelineKey))
                {
                    return Task.FromResult<AiSharedQueueItem?>(
                        null);
                }

                if (!string.IsNullOrWhiteSpace(
                        request.ControlPlaneId) &&
                    !string.Equals(
                        existing.ControlPlaneId,
                        request.ControlPlaneId,
                        StringComparison.Ordinal))
                {
                    return Task.FromResult<AiSharedQueueItem?>(
                        null);
                }

                var now =
                    DateTimeOffset.UtcNow;

                var claimTtl =
                    request.ClaimTtl <= TimeSpan.Zero
                        ? TimeSpan.FromSeconds(30)
                        : request.ClaimTtl;

                var claimed =
                    new AiSharedQueueItem
                    {
                        SharedRunId =
                            existing.SharedRunId,

                        ControlPlaneId =
                            existing.ControlPlaneId,

                        Status =
                            AiSharedQueueItemStatus.Claimed,

                        ExecutionContextSnapshot =
                            existing.ExecutionContextSnapshot,

                        PipelineKey =
                            existing.PipelineKey,

                        Priority =
                            existing.Priority,

                        ClaimedByRuntimeInstanceId =
                            request.RuntimeInstanceId,

                        ClaimedByWorkerId =
                            request.WorkerId,

                        ClaimToken =
                            Guid.NewGuid().ToString("N"),

                        EnqueuedAtUtc =
                            existing.EnqueuedAtUtc,

                        UpdatedAtUtc =
                            now,

                        ClaimedAtUtc =
                            now,

                        ClaimExpiresAtUtc =
                            now.Add(claimTtl),

                        Reason =
                            request.Reason,

                        Metadata =
                            existing.Metadata
                    };

                if (_items.TryUpdate(
                        sharedRunId,
                        claimed,
                        existing))
                {
                    return Task.FromResult<AiSharedQueueItem?>(
                        claimed);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> ClaimNextAsync(
            AiSharedQueueClaimRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.RuntimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            while (true)
            {
                var candidate = _items.Values
                    .Where(item => item.Status == AiSharedQueueItemStatus.Pending)
                    .Where(item => MatchesTenant(item, request.TenantId))
                    .Where(item => MatchesPipeline(item, request.PipelineKey))
                    .OrderBy(item => item.Priority)
                    .ThenBy(item => item.EnqueuedAtUtc)
                    .ThenBy(item => item.SharedRunId, StringComparer.Ordinal)
                    .FirstOrDefault();

                if (candidate is null)
                {
                    return Task.FromResult<AiSharedQueueItem?>(null);
                }

                var now = DateTimeOffset.UtcNow;
                var claimToken = Guid.NewGuid().ToString("N");

                var claimed = new AiSharedQueueItem
                {
                    SharedRunId = candidate.SharedRunId,
                    Status = AiSharedQueueItemStatus.Claimed,
                    ExecutionContextSnapshot = candidate.ExecutionContextSnapshot,
                    PipelineKey = candidate.PipelineKey,
                    Priority = candidate.Priority,
                    ClaimedByRuntimeInstanceId = request.RuntimeInstanceId,
                    ClaimedByWorkerId = request.WorkerId,
                    ClaimToken = claimToken,
                    EnqueuedAtUtc = candidate.EnqueuedAtUtc,
                    UpdatedAtUtc = now,
                    ClaimedAtUtc = now,
                    ClaimExpiresAtUtc = now.Add(request.ClaimTtl),
                    Reason = request.Reason,
                    Metadata = candidate.Metadata
                };

                if (_items.TryUpdate(candidate.SharedRunId, claimed, candidate))
                {
                    return Task.FromResult<AiSharedQueueItem?>(claimed);
                }
            }
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> MarkDispatchedAsync(
            string sharedRunId,
            string claimToken,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);

            cancellationToken.ThrowIfCancellationRequested();

            return UpdateClaimedAsync(
                sharedRunId,
                claimToken,
                AiSharedQueueItemStatus.Dispatched,
                reason,
                clearClaim: false,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> RequeueAsync(
            string sharedRunId,
            string claimToken,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);

            cancellationToken.ThrowIfCancellationRequested();

            while (true)
            {
                if (!_items.TryGetValue(sharedRunId, out var existing))
                {
                    return Task.FromResult<AiSharedQueueItem?>(null);
                }

                if (existing.Status != AiSharedQueueItemStatus.Claimed ||
                    !string.Equals(existing.ClaimToken, claimToken, StringComparison.Ordinal))
                {
                    return Task.FromResult<AiSharedQueueItem?>(null);
                }

                var now = DateTimeOffset.UtcNow;

                var updated = new AiSharedQueueItem
                {
                    SharedRunId = existing.SharedRunId,
                    Status = AiSharedQueueItemStatus.Pending,
                    ExecutionContextSnapshot = existing.ExecutionContextSnapshot,
                    PipelineKey = existing.PipelineKey,
                    Priority = existing.Priority,
                    EnqueuedAtUtc = existing.EnqueuedAtUtc,
                    UpdatedAtUtc = now,
                    Reason = reason,
                    Metadata = existing.Metadata
                };

                if (_items.TryUpdate(sharedRunId, updated, existing))
                {
                    return Task.FromResult<AiSharedQueueItem?>(updated);
                }
            }
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> RequeueDispatchedAsync(
            string sharedRunId,
            string claimToken,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            return RequeueDispatchedAsync(
                sharedRunId,
                claimToken,
                reason,
                metadata: null,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> RequeueDispatchedAsync(
            string sharedRunId,
            string claimToken,
            string? reason,
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);

            cancellationToken.ThrowIfCancellationRequested();

            while (true)
            {
                if (!_items.TryGetValue(sharedRunId, out var existing))
                {
                    return Task.FromResult<AiSharedQueueItem?>(null);
                }

                if (existing.Status != AiSharedQueueItemStatus.Dispatched ||
                    !string.Equals(existing.ClaimToken, claimToken, StringComparison.Ordinal))
                {
                    return Task.FromResult<AiSharedQueueItem?>(null);
                }

                var now = DateTimeOffset.UtcNow;
                var mergedMetadata = MergeMetadata(
                    existing.Metadata,
                    metadata);
                var priority = ResolvePriority(
                    existing.Priority,
                    metadata);

                var updated = new AiSharedQueueItem
                {
                    SharedRunId = existing.SharedRunId,
                    Status = AiSharedQueueItemStatus.Pending,
                    ExecutionContextSnapshot = existing.ExecutionContextSnapshot,
                    PipelineKey = existing.PipelineKey,
                    Priority = priority,
                    EnqueuedAtUtc = existing.EnqueuedAtUtc,
                    UpdatedAtUtc = now,
                    Reason = reason,
                    Metadata = mergedMetadata
                };

                if (_items.TryUpdate(sharedRunId, updated, existing))
                {
                    return Task.FromResult<AiSharedQueueItem?>(updated);
                }
            }
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> CancelAsync(
            string sharedRunId,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);

            cancellationToken.ThrowIfCancellationRequested();

            while (true)
            {
                if (!_items.TryGetValue(sharedRunId, out var existing))
                {
                    return Task.FromResult<AiSharedQueueItem?>(null);
                }

                if (IsTerminal(existing.Status))
                {
                    return Task.FromResult<AiSharedQueueItem?>(existing);
                }

                var updated = new AiSharedQueueItem
                {
                    SharedRunId = existing.SharedRunId,
                    Status = AiSharedQueueItemStatus.Cancelled,
                    ExecutionContextSnapshot = existing.ExecutionContextSnapshot,
                    PipelineKey = existing.PipelineKey,
                    Priority = existing.Priority,
                    ClaimedByRuntimeInstanceId = existing.ClaimedByRuntimeInstanceId,
                    ClaimedByWorkerId = existing.ClaimedByWorkerId,
                    ClaimToken = existing.ClaimToken,
                    EnqueuedAtUtc = existing.EnqueuedAtUtc,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    ClaimedAtUtc = existing.ClaimedAtUtc,
                    ClaimExpiresAtUtc = existing.ClaimExpiresAtUtc,
                    Reason = reason ?? "Shared queue item cancelled.",
                    Metadata = existing.Metadata
                };

                if (_items.TryUpdate(sharedRunId, updated, existing))
                {
                    return Task.FromResult<AiSharedQueueItem?>(updated);
                }
            }
        }

        /// <summary>
        /// Updates a claimed item when the caller owns the claim token.
        /// </summary>
        /// <param name="sharedRunId">The shared run id.</param>
        /// <param name="claimToken">The expected claim token.</param>
        /// <param name="status">The target status.</param>
        /// <param name="reason">The optional reason.</param>
        /// <param name="clearClaim">Whether claim information should be cleared.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The updated queue item, or <c>null</c>.</returns>
        private Task<AiSharedQueueItem?> UpdateClaimedAsync(
            string sharedRunId,
            string claimToken,
            AiSharedQueueItemStatus status,
            string? reason,
            bool clearClaim,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!_items.TryGetValue(sharedRunId, out var existing))
                {
                    return Task.FromResult<AiSharedQueueItem?>(null);
                }

                if (existing.Status != AiSharedQueueItemStatus.Claimed ||
                    !string.Equals(existing.ClaimToken, claimToken, StringComparison.Ordinal))
                {
                    return Task.FromResult<AiSharedQueueItem?>(null);
                }

                var now = DateTimeOffset.UtcNow;

                var updated = new AiSharedQueueItem
                {
                    SharedRunId = existing.SharedRunId,
                    Status = status,
                    ExecutionContextSnapshot = existing.ExecutionContextSnapshot,
                    PipelineKey = existing.PipelineKey,
                    Priority = existing.Priority,
                    ClaimedByRuntimeInstanceId = clearClaim ? null : existing.ClaimedByRuntimeInstanceId,
                    ClaimedByWorkerId = clearClaim ? null : existing.ClaimedByWorkerId,
                    ClaimToken = clearClaim ? null : existing.ClaimToken,
                    EnqueuedAtUtc = existing.EnqueuedAtUtc,
                    UpdatedAtUtc = now,
                    ClaimedAtUtc = clearClaim ? null : existing.ClaimedAtUtc,
                    ClaimExpiresAtUtc = clearClaim ? null : existing.ClaimExpiresAtUtc,
                    Reason = reason,
                    Metadata = existing.Metadata
                };

                if (_items.TryUpdate(sharedRunId, updated, existing))
                {
                    return Task.FromResult<AiSharedQueueItem?>(updated);
                }
            }
        }

        /// <summary>
        /// Resolves an optional durable queue-priority override from item metadata.
        /// </summary>
        /// <param name="existingPriority">The current queue priority.</param>
        /// <param name="metadata">The optional transition metadata.</param>
        /// <returns>The effective queue priority.</returns>
        private static int ResolvePriority(
            int existingPriority,
            IReadOnlyDictionary<string, string>? metadata)
        {
            if (metadata is null ||
                metadata.Count == 0)
            {
                return existingPriority;
            }

            foreach (var pair in metadata)
            {
                if (!string.Equals(
                        pair.Key,
                        QueuePriorityMetadataKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return int.TryParse(
                    pair.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var priority)
                        ? priority
                        : existingPriority;
            }

            return existingPriority;
        }

        /// <summary>
        /// Merges optional metadata into existing queue item metadata.
        /// </summary>
        /// <param name="existingMetadata">The existing metadata.</param>
        /// <param name="metadata">The optional metadata to merge.</param>
        /// <returns>The merged metadata.</returns>
        private static IReadOnlyDictionary<string, string> MergeMetadata(
            IReadOnlyDictionary<string, string> existingMetadata,
            IReadOnlyDictionary<string, string>? metadata)
        {
            if (metadata is null ||
                metadata.Count == 0)
            {
                return existingMetadata;
            }

            var merged =
                new Dictionary<string, string>(
                    existingMetadata,
                    StringComparer.OrdinalIgnoreCase);

            foreach (var pair in metadata)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    continue;
                }

                merged[pair.Key] = pair.Value ?? string.Empty;
            }

            return merged;
        }

        /// <summary>
        /// Determines whether a queue item status is terminal.
        /// </summary>
        /// <param name="status">The queue item status.</param>
        /// <returns><c>true</c> when terminal; otherwise, <c>false</c>.</returns>
        private static bool IsTerminal(
            AiSharedQueueItemStatus status)
        {
            return status is
                AiSharedQueueItemStatus.Completed or
                AiSharedQueueItemStatus.Failed or
                AiSharedQueueItemStatus.Cancelled or
                AiSharedQueueItemStatus.Dispatched;
        }

        /// <summary>
        /// Determines whether an item matches the requested tenant filter.
        /// </summary>
        /// <param name="item">The queue item.</param>
        /// <param name="tenantId">The optional tenant filter.</param>
        /// <returns><c>true</c> when matching; otherwise, <c>false</c>.</returns>
        private static bool MatchesTenant(
            AiSharedQueueItem item,
            string? tenantId)
        {
            return string.IsNullOrWhiteSpace(tenantId) ||
                   string.Equals(item.ExecutionContextSnapshot.TenantId, tenantId, StringComparison.Ordinal);
        }

        /// <summary>
        /// Determines whether an item matches the requested pipeline filter.
        /// </summary>
        /// <param name="item">The queue item.</param>
        /// <param name="pipelineKey">The optional pipeline filter.</param>
        /// <returns><c>true</c> when matching; otherwise, <c>false</c>.</returns>
        private static bool MatchesPipeline(
            AiSharedQueueItem item,
            string? pipelineKey)
        {
            return string.IsNullOrWhiteSpace(pipelineKey) ||
                   string.Equals(item.PipelineKey, pipelineKey, StringComparison.Ordinal);
        }
    }
}