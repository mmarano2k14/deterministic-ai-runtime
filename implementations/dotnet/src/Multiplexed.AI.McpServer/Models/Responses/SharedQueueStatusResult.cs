using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using System;
using System.Collections.Generic;
using System.Text;

namespace Multiplexed.AI.McpServer.Models.Responses
{
    public sealed class SharedQueueStatusResult
    {
        public required int TotalCount { get; init; }

        public required int PendingCount { get; init; }

        public required int ClaimedCount { get; init; }

        public required int DispatchedCount { get; init; }

        public required int CompletedCount { get; init; }

        public required int FailedCount { get; init; }

        public required int CancelledCount { get; init; }

        public  DateTimeOffset? OldestPendingAtUtc { get; init; }

        public  DateTimeOffset? NewestPendingAtUtc { get; init; }

        public required bool IncludeTerminal { get; init; }

        public static SharedQueueStatusResult FromItems(
            IReadOnlyList<AiSharedQueueItem> items,
            bool includeTerminal)
        {
            ArgumentNullException.ThrowIfNull(items);

            var pendingItems = items
                .Where(item => item.Status == AiSharedQueueItemStatus.Pending)
                .ToArray();

            return new SharedQueueStatusResult
            {
                TotalCount = items.Count,
                PendingCount = items.Count(item => item.Status == AiSharedQueueItemStatus.Pending),
                ClaimedCount = items.Count(item => item.Status == AiSharedQueueItemStatus.Claimed),
                DispatchedCount = items.Count(item => item.Status == AiSharedQueueItemStatus.Dispatched),
                CompletedCount = items.Count(item => item.Status == AiSharedQueueItemStatus.Completed),
                FailedCount = items.Count(item => item.Status == AiSharedQueueItemStatus.Failed),
                CancelledCount = items.Count(item => item.Status == AiSharedQueueItemStatus.Cancelled),
                OldestPendingAtUtc = pendingItems.Length == 0
                    ? null
                    : pendingItems.Min(item => item.EnqueuedAtUtc),
                NewestPendingAtUtc = pendingItems.Length == 0
                    ? null
                    : pendingItems.Max(item => item.EnqueuedAtUtc),
                IncludeTerminal = includeTerminal
            };
        }
    }
}
