using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Shared queue fake that records the last queue item.
    /// </summary>
    public sealed class NoopSharedQueue : IAiSharedQueue
    {
        /// <summary>
        /// Gets the last queue item enqueued by the controller.
        /// </summary>
        public AiSharedQueueItem? LastItem { get; private set; }

        /// <inheritdoc />
        public Task<AiSharedQueueItem> EnqueueAsync(
            AiSharedQueueItem item,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(item);

            this.LastItem = item;

            return Task.FromResult(item);
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> GetAsync(
            string sharedRunId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            cancellationToken.ThrowIfCancellationRequested();

            if (this.IsLastItem(sharedRunId))
            {
                return Task.FromResult<AiSharedQueueItem?>(
                    this.LastItem);
            }

            return Task.FromResult<AiSharedQueueItem?>(
                null);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiSharedQueueItem>> ListAsync(
            bool includeTerminal = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (this.LastItem is null)
            {
                return Task.FromResult<IReadOnlyList<AiSharedQueueItem>>(
                    Array.Empty<AiSharedQueueItem>());
            }

            return Task.FromResult<IReadOnlyList<AiSharedQueueItem>>(
                new[]
                {
                    this.LastItem
                });
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> ClaimNextAsync(
            AiSharedQueueClaimRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<AiSharedQueueItem?>(
                null);
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> ClaimAsync(
            string sharedRunId,
            AiSharedQueueClaimRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            if (this.IsLastItem(sharedRunId))
            {
                return Task.FromResult<AiSharedQueueItem?>(
                    this.LastItem);
            }

            return Task.FromResult<AiSharedQueueItem?>(
                null);
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

            if (this.IsLastItem(sharedRunId))
            {
                return Task.FromResult<AiSharedQueueItem?>(
                    this.LastItem);
            }

            return Task.FromResult<AiSharedQueueItem?>(
                null);
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

            if (this.IsLastItem(sharedRunId))
            {
                return Task.FromResult<AiSharedQueueItem?>(
                    this.LastItem);
            }

            return Task.FromResult<AiSharedQueueItem?>(
                null);
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> CancelAsync(
            string sharedRunId,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            cancellationToken.ThrowIfCancellationRequested();

            if (this.IsLastItem(sharedRunId))
            {
                return Task.FromResult<AiSharedQueueItem?>(
                    this.LastItem);
            }

            return Task.FromResult<AiSharedQueueItem?>(
                null);
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> RequeueDispatchedAsync(
            string sharedRunId,
            string claimToken,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            return this.RequeueDispatchedAsync(
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

            if (!this.IsLastItem(sharedRunId))
            {
                return Task.FromResult<AiSharedQueueItem?>(
                    null);
            }

            var existingItem =
                this.LastItem!;

            this.LastItem =
                new AiSharedQueueItem
                {
                    SharedRunId =
                        existingItem.SharedRunId,

                    ControlPlaneId =
                        existingItem.ControlPlaneId,

                    Status =
                        existingItem.Status,

                    ExecutionContextSnapshot =
                        existingItem.ExecutionContextSnapshot,

                    PipelineKey =
                        existingItem.PipelineKey,

                    Priority =
                        existingItem.Priority,

                    ClaimedByRuntimeInstanceId =
                        existingItem.ClaimedByRuntimeInstanceId,

                    ClaimedByWorkerId =
                        existingItem.ClaimedByWorkerId,

                    ClaimToken =
                        existingItem.ClaimToken,

                    EnqueuedAtUtc =
                        existingItem.EnqueuedAtUtc,

                    UpdatedAtUtc =
                        DateTimeOffset.UtcNow,

                    ClaimedAtUtc =
                        existingItem.ClaimedAtUtc,

                    ClaimExpiresAtUtc =
                        existingItem.ClaimExpiresAtUtc,

                    Reason =
                        reason,

                    Metadata =
                        metadata is not null &&
                        metadata.Count > 0
                            ? MergeMetadata(
                                existingItem.Metadata,
                                metadata)
                            : existingItem.Metadata
                };

            return Task.FromResult<AiSharedQueueItem?>(
                this.LastItem);
        }

        private bool IsLastItem(
            string sharedRunId)
        {
            return this.LastItem is not null &&
                   string.Equals(
                       this.LastItem.SharedRunId,
                       sharedRunId,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyDictionary<string, string> MergeMetadata(
            IReadOnlyDictionary<string, string> existingMetadata,
            IReadOnlyDictionary<string, string> metadata)
        {
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

                merged[pair.Key] =
                    pair.Value ??
                    string.Empty;
            }

            return merged;
        }
    }
}