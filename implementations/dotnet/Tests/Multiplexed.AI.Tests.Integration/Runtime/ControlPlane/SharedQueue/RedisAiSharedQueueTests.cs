using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Redis;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue.Redis;
using Multiplexed.AI.Tests.Fixtures;
using StackExchange.Redis;

namespace Multiplexed.AI.Tests.Integration.Runtime.ControlPlane.SharedQueue
{
    public sealed class RedisAiSharedQueueTests : IAsyncLifetime
    {
        private readonly string _keyPrefix =
            $"test:ai:shared-queue:{Guid.NewGuid():N}";

        private IConnectionMultiplexer? _connection;

        public async Task InitializeAsync()
        {
            _connection = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        }

        public async Task DisposeAsync()
        {
            if (_connection is null)
            {
                return;
            }

            var database = _connection.GetDatabase();

            var server = _connection.GetServer(
                _connection.GetEndPoints().First());

            var keys = server.Keys(
                    database: database.Database,
                    pattern: $"{_keyPrefix}*")
                .ToArray();

            if (keys.Length > 0)
            {
                await database.KeyDeleteAsync(keys);
            }

            await _connection.CloseAsync();
            await _connection.DisposeAsync();
        }

        [Fact]
        public async Task EnqueueAsync_Should_Create_Item()
        {
            var queue = CreateQueue();

            var item = CreateItem("shared-run-1");

            var enqueued = await queue.EnqueueAsync(item);

            Assert.Equal("shared-run-1", enqueued.SharedRunId);
            Assert.Equal(AiSharedQueueItemStatus.Pending, enqueued.Status);

            var loaded = await queue.GetAsync("shared-run-1");

            Assert.NotNull(loaded);
            Assert.Equal("shared-run-1", loaded!.SharedRunId);
            Assert.Equal(AiSharedQueueItemStatus.Pending, loaded.Status);
        }

        [Fact]
        public async Task EnqueueAsync_Should_Reject_Duplicate_Atomically()
        {
            var queue = CreateQueue();

            await queue.EnqueueAsync(CreateItem("shared-run-1"));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                queue.EnqueueAsync(CreateItem("shared-run-1")));
        }

        [Fact]
        public async Task GetAsync_Should_Return_Null_When_Item_Is_Missing()
        {
            var queue = CreateQueue();

            var item = await queue.GetAsync("missing-run");

            Assert.Null(item);
        }

        [Fact]
        public async Task ListAsync_Should_Return_Items_From_All_Index()
        {
            var queue = CreateQueue();
            var now = DateTimeOffset.UtcNow;

            await queue.EnqueueAsync(
                CreateItem("shared-run-b", priority: 1, enqueuedAtUtc: now.AddMinutes(1)));

            await queue.EnqueueAsync(
                CreateItem("shared-run-a", priority: 1, enqueuedAtUtc: now));

            var items = await queue.ListAsync();

            Assert.Equal(2, items.Count);
            Assert.Equal("shared-run-a", items[0].SharedRunId);
            Assert.Equal("shared-run-b", items[1].SharedRunId);
        }

        [Fact]
        public async Task ClaimNextAsync_Should_Claim_First_Pending_Item()
        {
            var queue = CreateQueue();

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
            var queue = CreateQueue();

            var claimed = await queue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = "runtime-1"
            });

            Assert.Null(claimed);
        }

        [Fact]
        public async Task ClaimNextAsync_Should_Not_Claim_Same_Item_Twice()
        {
            var queue = CreateQueue();

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
        public async Task ClaimNextAsync_Should_Respect_Tenant_Filter()
        {
            var queue = CreateQueue();

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
            var queue = CreateQueue();

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
        public async Task MarkDispatchedAsync_Should_Mark_Claimed_Item_As_Dispatched()
        {
            var queue = CreateQueue();

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
            var queue = CreateQueue();

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
            var queue = CreateQueue();

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

            var claimedAgain = await queue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = "runtime-2"
            });

            Assert.NotNull(claimedAgain);
            Assert.Equal("shared-run-1", claimedAgain!.SharedRunId);
        }

        [Fact]
        public async Task RequeueAsync_Should_Return_Null_When_Token_Does_Not_Match()
        {
            var queue = CreateQueue();

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
            var queue = CreateQueue();

            await queue.EnqueueAsync(CreateItem("shared-run-1"));

            var cancelled = await queue.CancelAsync(
                "shared-run-1",
                reason: "operator cancel");

            Assert.NotNull(cancelled);
            Assert.Equal(AiSharedQueueItemStatus.Cancelled, cancelled!.Status);
            Assert.Equal("operator cancel", cancelled.Reason);

            var claimed = await queue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = "runtime-1"
            });

            Assert.Null(claimed);
        }

        [Fact]
        public async Task CancelAsync_Should_Return_Null_When_Item_Is_Missing()
        {
            var queue = CreateQueue();

            var cancelled = await queue.CancelAsync("missing-run");

            Assert.Null(cancelled);
        }

        [Fact]
        public async Task CancelAsync_Should_Return_Existing_Item_When_Terminal()
        {
            var queue = CreateQueue();

            await queue.EnqueueAsync(
                CreateItem("shared-run-1", status: AiSharedQueueItemStatus.Dispatched, reason: "terminal"));

            var cancelled = await queue.CancelAsync(
                "shared-run-1",
                reason: "new cancel");

            Assert.NotNull(cancelled);
            Assert.Equal(AiSharedQueueItemStatus.Dispatched, cancelled!.Status);
            Assert.Equal("terminal", cancelled.Reason);
        }

        [Fact]
        public async Task ListAsync_Should_Exclude_Terminal_By_Default()
        {
            var queue = CreateQueue();

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
            var queue = CreateQueue();

            await queue.EnqueueAsync(CreateItem("shared-run-1"));
            await queue.EnqueueAsync(CreateItem("shared-run-2", status: AiSharedQueueItemStatus.Cancelled));
            await queue.EnqueueAsync(CreateItem("shared-run-3", status: AiSharedQueueItemStatus.Dispatched));

            var items = await queue.ListAsync(includeTerminal: true);

            Assert.Equal(3, items.Count);
        }

        [Fact]
        public async Task EnqueueAsync_Should_Preserve_Metadata()
        {
            var queue = CreateQueue();

            await queue.EnqueueAsync(
                CreateItem(
                    "shared-run-1",
                    metadata: new Dictionary<string, string>
                    {
                        ["tenant"] = "tenant-1",
                        ["priority-label"] = "high"
                    }));

            var loaded = await queue.GetAsync("shared-run-1");

            Assert.NotNull(loaded);
            Assert.Equal("tenant-1", loaded!.Metadata["tenant"]);
            Assert.Equal("high", loaded.Metadata["priority-label"]);
        }

        [Fact]
        public async Task ClaimNextAsync_Should_Allow_Only_One_Concurrent_Claim()
        {
            var queue = CreateQueue();

            await queue.EnqueueAsync(CreateItem("shared-run-1"));

            var tasks = Enumerable.Range(0, 20)
                .Select(index =>
                    Task.Run(() =>
                        queue.ClaimNextAsync(new AiSharedQueueClaimRequest
                        {
                            RuntimeInstanceId = $"runtime-{index}",
                            WorkerId = $"worker-{index}"
                        })))
                .ToArray();

            var results = await Task.WhenAll(tasks);

            var claimed = results
                .Where(result => result is not null)
                .ToArray();

            Assert.Single(claimed);
            Assert.Equal("shared-run-1", claimed[0]!.SharedRunId);
            Assert.Equal(AiSharedQueueItemStatus.Claimed, claimed[0]!.Status);
        }

        private RedisAiSharedQueue CreateQueue()
        {
            if (_connection is null)
            {
                throw new InvalidOperationException("Redis connection was not initialized.");
            }

            return new RedisAiSharedQueue(
                _connection,
                Options.Create(new RedisAiSharedQueueOptions
                {
                    KeyPrefix = _keyPrefix,
                    ListScanLimit = 100
                }), 
                new StaticAiControlPlaneIdResolver("test-control-plane"));
        }

        private static AiSharedQueueItem CreateItem(
            string sharedRunId,
            AiSharedQueueItemStatus status = AiSharedQueueItemStatus.Pending,
            string? tenantId = null,
            string? pipelineKey = null,
            int priority = 0,
            DateTimeOffset? enqueuedAtUtc = null,
            string? reason = null,
            IReadOnlyDictionary<string, string>? metadata = null)
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
                Reason = reason,
                Metadata = metadata ?? new Dictionary<string, string>()
            };
        }
    }
}