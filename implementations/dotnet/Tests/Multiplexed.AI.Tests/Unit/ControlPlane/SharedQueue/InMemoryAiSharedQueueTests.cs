using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using Multiplexed.AI.Tests.Fixtures;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedQueue
{
    public sealed class InMemoryAiSharedQueueTests
    {
        [Fact]
        public async Task EnqueueAsync_Should_Add_Pending_Item()
        {
            var queue = new InMemoryAiSharedQueue();

            var item = CreateItem("shared-run-1");

            var enqueued = await queue.EnqueueAsync(item);

            Assert.Equal("shared-run-1", enqueued.SharedRunId);
            Assert.Equal(AiSharedQueueItemStatus.Pending, enqueued.Status);
        }

        [Fact]
        public async Task EnqueueAsync_Should_Reject_Duplicate_SharedRunId()
        {
            var queue = new InMemoryAiSharedQueue();

            await queue.EnqueueAsync(CreateItem("shared-run-1"));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                queue.EnqueueAsync(CreateItem("shared-run-1")));
        }

        [Fact]
        public async Task GetAsync_Should_Return_Item_When_Known()
        {
            var queue = new InMemoryAiSharedQueue();

            await queue.EnqueueAsync(CreateItem("shared-run-1"));

            var item = await queue.GetAsync("shared-run-1");

            Assert.NotNull(item);
            Assert.Equal("shared-run-1", item!.SharedRunId);
        }

        [Fact]
        public async Task GetAsync_Should_Return_Null_When_Unknown()
        {
            var queue = new InMemoryAiSharedQueue();

            var item = await queue.GetAsync("missing-run");

            Assert.Null(item);
        }

        [Fact]
        public async Task ListAsync_Should_Return_Items_Ordered_By_Priority_Then_EnqueuedAt_Then_Id()
        {
            var queue = new InMemoryAiSharedQueue();
            var now = DateTimeOffset.UtcNow;

            await queue.EnqueueAsync(
                CreateItem("shared-run-c", priority: 10, enqueuedAtUtc: now));

            await queue.EnqueueAsync(
                CreateItem("shared-run-b", priority: 1, enqueuedAtUtc: now.AddMinutes(1)));

            await queue.EnqueueAsync(
                CreateItem("shared-run-a", priority: 1, enqueuedAtUtc: now));

            var items = await queue.ListAsync();

            Assert.Equal(3, items.Count);
            Assert.Equal("shared-run-a", items[0].SharedRunId);
            Assert.Equal("shared-run-b", items[1].SharedRunId);
            Assert.Equal("shared-run-c", items[2].SharedRunId);
        }

        [Fact]
        public async Task ClaimNextAsync_Should_Claim_First_Pending_Item()
        {
            var queue = new InMemoryAiSharedQueue();

            await queue.EnqueueAsync(CreateItem("shared-run-1"));

            var claimed = await queue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = "runtime-1",
                WorkerId = "worker-1",
                ClaimTtl = TimeSpan.FromSeconds(30),
                Reason = "claim for dispatch"
            });

            Assert.NotNull(claimed);
            Assert.Equal("shared-run-1", claimed!.SharedRunId);
            Assert.Equal(AiSharedQueueItemStatus.Claimed, claimed.Status);
            Assert.Equal("runtime-1", claimed.ClaimedByRuntimeInstanceId);
            Assert.Equal("worker-1", claimed.ClaimedByWorkerId);
            Assert.False(string.IsNullOrWhiteSpace(claimed.ClaimToken));
            Assert.NotNull(claimed.ClaimedAtUtc);
            Assert.NotNull(claimed.ClaimExpiresAtUtc);
        }

        [Fact]
        public async Task ClaimNextAsync_Should_Return_Null_When_No_Pending_Item()
        {
            var queue = new InMemoryAiSharedQueue();

            var claimed = await queue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = "runtime-1"
            });

            Assert.Null(claimed);
        }

        [Fact]
        public async Task ClaimNextAsync_Should_Respect_Tenant_Filter()
        {
            var queue = new InMemoryAiSharedQueue();

            await queue.EnqueueAsync(
                CreateItem("shared-run-1", tenantId: "tenant-a"));

            await queue.EnqueueAsync(
                CreateItem("shared-run-2", tenantId: "tenant-b"));

            var claimed = await queue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = "runtime-1",
                TenantId = "tenant-b"
            });

            Assert.NotNull(claimed);
            Assert.Equal("shared-run-2", claimed!.SharedRunId);
            Assert.Equal("tenant-b", claimed.ExecutionContextSnapshot.TenantId);
        }

        [Fact]
        public async Task ClaimNextAsync_Should_Respect_Pipeline_Filter()
        {
            var queue = new InMemoryAiSharedQueue();

            await queue.EnqueueAsync(
                CreateItem("shared-run-1", pipelineKey: "pipeline-a"));

            await queue.EnqueueAsync(
                CreateItem("shared-run-2", pipelineKey: "pipeline-b"));

            var claimed = await queue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = "runtime-1",
                PipelineKey = "pipeline-b"
            });

            Assert.NotNull(claimed);
            Assert.Equal("shared-run-2", claimed!.SharedRunId);
            Assert.Equal("pipeline-b", claimed.PipelineKey);
        }

        [Fact]
        public async Task ClaimNextAsync_Should_Not_Claim_Already_Claimed_Item()
        {
            var queue = new InMemoryAiSharedQueue();

            await queue.EnqueueAsync(CreateItem("shared-run-1"));

            var first = await queue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = "runtime-1"
            });

            var second = await queue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = "runtime-2"
            });

            Assert.NotNull(first);
            Assert.Null(second);
        }

        [Fact]
        public async Task MarkDispatchedAsync_Should_Mark_Claimed_Item_As_Dispatched_When_Token_Matches()
        {
            var queue = new InMemoryAiSharedQueue();

            await queue.EnqueueAsync(CreateItem("shared-run-1"));

            var claimed = await queue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = "runtime-1"
            });

            Assert.NotNull(claimed);

            var dispatched = await queue.MarkDispatchedAsync(
                "shared-run-1",
                claimed!.ClaimToken!,
                reason: "sent to runtime queue");

            Assert.NotNull(dispatched);
            Assert.Equal(AiSharedQueueItemStatus.Dispatched, dispatched!.Status);
            Assert.Equal("sent to runtime queue", dispatched.Reason);
        }

        [Fact]
        public async Task MarkDispatchedAsync_Should_Return_Null_When_Token_Does_Not_Match()
        {
            var queue = new InMemoryAiSharedQueue();

            await queue.EnqueueAsync(CreateItem("shared-run-1"));

            await queue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = "runtime-1"
            });

            var dispatched = await queue.MarkDispatchedAsync(
                "shared-run-1",
                "wrong-token");

            Assert.Null(dispatched);
        }

        [Fact]
        public async Task RequeueAsync_Should_Return_Item_To_Pending_When_Token_Matches()
        {
            var queue = new InMemoryAiSharedQueue();

            await queue.EnqueueAsync(CreateItem("shared-run-1"));

            var claimed = await queue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = "runtime-1"
            });

            Assert.NotNull(claimed);

            var requeued = await queue.RequeueAsync(
                "shared-run-1",
                claimed!.ClaimToken!,
                reason: "dispatch failed");

            Assert.NotNull(requeued);
            Assert.Equal(AiSharedQueueItemStatus.Pending, requeued!.Status);
            Assert.Null(requeued.ClaimToken);
            Assert.Null(requeued.ClaimedByRuntimeInstanceId);
            Assert.Equal("dispatch failed", requeued.Reason);
        }

        [Fact]
        public async Task RequeueAsync_Should_Return_Null_When_Token_Does_Not_Match()
        {
            var queue = new InMemoryAiSharedQueue();

            await queue.EnqueueAsync(CreateItem("shared-run-1"));

            await queue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = "runtime-1"
            });

            var requeued = await queue.RequeueAsync(
                "shared-run-1",
                "wrong-token");

            Assert.Null(requeued);
        }

        [Fact]
        public async Task CancelAsync_Should_Cancel_NonTerminal_Item()
        {
            var queue = new InMemoryAiSharedQueue();

            await queue.EnqueueAsync(CreateItem("shared-run-1"));

            var cancelled = await queue.CancelAsync(
                "shared-run-1",
                reason: "operator cancel");

            Assert.NotNull(cancelled);
            Assert.Equal(AiSharedQueueItemStatus.Cancelled, cancelled!.Status);
            Assert.Equal("operator cancel", cancelled.Reason);
        }

        [Fact]
        public async Task CancelAsync_Should_Return_Null_When_Unknown()
        {
            var queue = new InMemoryAiSharedQueue();

            var cancelled = await queue.CancelAsync("missing-run");

            Assert.Null(cancelled);
        }

        [Theory]
        [InlineData(AiSharedQueueItemStatus.Completed)]
        [InlineData(AiSharedQueueItemStatus.Failed)]
        [InlineData(AiSharedQueueItemStatus.Cancelled)]
        [InlineData(AiSharedQueueItemStatus.Dispatched)]
        public async Task CancelAsync_Should_Return_Existing_Item_When_Terminal(
            AiSharedQueueItemStatus terminalStatus)
        {
            var queue = new InMemoryAiSharedQueue();

            await queue.EnqueueAsync(
                CreateItem("shared-run-1", status: terminalStatus, reason: "terminal"));

            var cancelled = await queue.CancelAsync(
                "shared-run-1",
                reason: "new cancel");

            Assert.NotNull(cancelled);
            Assert.Equal(terminalStatus, cancelled!.Status);
            Assert.Equal("terminal", cancelled.Reason);
        }

        [Fact]
        public async Task ListAsync_Should_Exclude_Terminal_By_Default()
        {
            var queue = new InMemoryAiSharedQueue();

            await queue.EnqueueAsync(CreateItem("shared-run-1"));
            await queue.EnqueueAsync(CreateItem("shared-run-2", status: AiSharedQueueItemStatus.Cancelled));
            await queue.EnqueueAsync(CreateItem("shared-run-3", status: AiSharedQueueItemStatus.Dispatched));

            var items = await queue.ListAsync();

            Assert.Single(items);
            Assert.Equal("shared-run-1", items[0].SharedRunId);
        }

        [Fact]
        public async Task ListAsync_Should_Include_Terminal_When_Requested()
        {
            var queue = new InMemoryAiSharedQueue();

            await queue.EnqueueAsync(CreateItem("shared-run-1"));
            await queue.EnqueueAsync(CreateItem("shared-run-2", status: AiSharedQueueItemStatus.Cancelled));
            await queue.EnqueueAsync(CreateItem("shared-run-3", status: AiSharedQueueItemStatus.Dispatched));

            var items = await queue.ListAsync(includeTerminal: true);

            Assert.Equal(3, items.Count);
        }

        /// <summary>
        /// Verifies that dispatched shared queue items remain discoverable for diagnostics and recovery scans.
        /// </summary>
        [Fact]
        public async Task MarkDispatchedAsync_Should_Keep_Item_Discoverable_When_Including_Terminal_Items()
        {
            var queue = new InMemoryAiSharedQueue();

            await queue.EnqueueAsync(CreateItem("shared-run-1"));

            var claimed = await queue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = "runtime-1"
            });

            Assert.NotNull(claimed);

            await queue.MarkDispatchedAsync(
                "shared-run-1",
                claimed!.ClaimToken!,
                reason: "sent to runtime queue");

            var loaded = await queue.GetAsync("shared-run-1");
            var activeItems = await queue.ListAsync();
            var allItems = await queue.ListAsync(includeTerminal: true);

            Assert.NotNull(loaded);
            Assert.Equal(AiSharedQueueItemStatus.Dispatched, loaded!.Status);
            Assert.Equal("runtime-1", loaded.ClaimedByRuntimeInstanceId);
            Assert.Equal("sent to runtime queue", loaded.Reason);
            Assert.DoesNotContain(activeItems, item => item.SharedRunId == "shared-run-1");
            Assert.Contains(allItems, item => item.SharedRunId == "shared-run-1" && item.Status == AiSharedQueueItemStatus.Dispatched);
        }

        private static AiSharedQueueItem CreateItem(
            string sharedRunId,
            AiSharedQueueItemStatus status = AiSharedQueueItemStatus.Pending,
            string? tenantId = null,
            string? pipelineKey = null,
            int priority = 0,
            DateTimeOffset? enqueuedAtUtc = null,
            string? reason = null)
        {
            var now = enqueuedAtUtc ?? DateTimeOffset.UtcNow;

            return new AiSharedQueueItem
            {
                SharedRunId = sharedRunId,
                Status = status,
                ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create(tenantId: tenantId),
                PipelineKey = pipelineKey,
                Priority = priority,
                EnqueuedAtUtc = now,
                UpdatedAtUtc = now,
                Reason = reason
            };
        }
    }
}