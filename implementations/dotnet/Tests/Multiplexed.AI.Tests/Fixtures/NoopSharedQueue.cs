using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using System;
using System.Collections.Generic;
using System.Text;

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

            this.LastItem =
                item
                ?? throw new ArgumentNullException(nameof(item));

            return Task.FromResult(item);
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> GetAsync(
            string sharedRunId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (this.LastItem is not null &&
                string.Equals(
                    this.LastItem.SharedRunId,
                    sharedRunId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<AiSharedQueueItem?>(this.LastItem);
            }

            return Task.FromResult<AiSharedQueueItem?>(null);
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

            return Task.FromResult<AiSharedQueueItem?>(null);
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> MarkDispatchedAsync(
            string sharedRunId,
            string claimToken,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (this.LastItem is not null &&
                string.Equals(
                    this.LastItem.SharedRunId,
                    sharedRunId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<AiSharedQueueItem?>(this.LastItem);
            }

            return Task.FromResult<AiSharedQueueItem?>(null);
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> RequeueAsync(
            string sharedRunId,
            string claimToken,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (this.LastItem is not null &&
                string.Equals(
                    this.LastItem.SharedRunId,
                    sharedRunId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<AiSharedQueueItem?>(this.LastItem);
            }

            return Task.FromResult<AiSharedQueueItem?>(null);
        }

        /// <inheritdoc />
        public Task<AiSharedQueueItem?> CancelAsync(
            string sharedRunId,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (this.LastItem is not null &&
                string.Equals(
                    this.LastItem.SharedRunId,
                    sharedRunId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<AiSharedQueueItem?>(this.LastItem);
            }

            return Task.FromResult<AiSharedQueueItem?>(null);
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
        /// <inheritdoc />
        public Task<AiSharedQueueItem?> RequeueDispatchedAsync(
            string sharedRunId,
            string claimToken,
            string? reason,
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (this.LastItem is not null &&
                string.Equals(
                    this.LastItem.SharedRunId,
                    sharedRunId,
                    StringComparison.OrdinalIgnoreCase))
            {
                this.LastItem = new AiSharedQueueItem
                {
                    SharedRunId = this.LastItem.SharedRunId,
                    ControlPlaneId = this.LastItem.ControlPlaneId,
                    Status = this.LastItem.Status,
                    ExecutionContextSnapshot = this.LastItem.ExecutionContextSnapshot,
                    PipelineKey = this.LastItem.PipelineKey,
                    Priority = this.LastItem.Priority,
                    ClaimedByRuntimeInstanceId = this.LastItem.ClaimedByRuntimeInstanceId,
                    ClaimedByWorkerId = this.LastItem.ClaimedByWorkerId,
                    ClaimToken = this.LastItem.ClaimToken,
                    EnqueuedAtUtc = this.LastItem.EnqueuedAtUtc,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    ClaimedAtUtc = this.LastItem.ClaimedAtUtc,
                    ClaimExpiresAtUtc = this.LastItem.ClaimExpiresAtUtc,
                    Reason = reason,
                    Metadata = metadata is not null && metadata.Count > 0
                        ? MergeMetadata(this.LastItem.Metadata, metadata)
                        : this.LastItem.Metadata
                };

                return Task.FromResult<AiSharedQueueItem?>(this.LastItem);
            }

            return Task.FromResult<AiSharedQueueItem?>(null);
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

                merged[pair.Key] = pair.Value ?? string.Empty;
            }

            return merged;
        }
    }
}
