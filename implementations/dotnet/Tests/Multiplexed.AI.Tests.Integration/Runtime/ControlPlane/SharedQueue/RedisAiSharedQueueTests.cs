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


        /// <summary>
        /// Verifies that durable recovery priority makes an in-flight resume claimable
        /// before local queued recovery work, regardless of requeue order.
        /// </summary>
        [Fact]
        public async Task RequeueDispatchedAsync_Should_Claim_InFlight_Recovery_Before_Local_Queued_Work()
        {
            var queue = CreateQueue();
            var now = DateTimeOffset.UtcNow;

            await queue.EnqueueAsync(
                CreateItem(
                    "local-queued-recovery",
                    enqueuedAtUtc: now));
            await queue.EnqueueAsync(
                CreateItem(
                    "in-flight-recovery",
                    enqueuedAtUtc: now.AddSeconds(1)));

            var localQueuedClaim = await queue.ClaimNextAsync(
                new AiSharedQueueClaimRequest
                {
                    RuntimeInstanceId = "runtime-1",
                    WorkerId = "worker-1"
                });
            Assert.NotNull(localQueuedClaim);
            Assert.Equal("local-queued-recovery", localQueuedClaim!.SharedRunId);
            await queue.MarkDispatchedAsync(
                localQueuedClaim.SharedRunId,
                localQueuedClaim.ClaimToken!);

            var inFlightClaim = await queue.ClaimNextAsync(
                new AiSharedQueueClaimRequest
                {
                    RuntimeInstanceId = "runtime-1",
                    WorkerId = "worker-1"
                });
            Assert.NotNull(inFlightClaim);
            Assert.Equal("in-flight-recovery", inFlightClaim!.SharedRunId);
            await queue.MarkDispatchedAsync(
                inFlightClaim.SharedRunId,
                inFlightClaim.ClaimToken!);

            await queue.RequeueDispatchedAsync(
                localQueuedClaim.SharedRunId,
                localQueuedClaim.ClaimToken!,
                reason: "local-queued-recovery");
            var prioritizedResume = await queue.RequeueDispatchedAsync(
                inFlightClaim.SharedRunId,
                inFlightClaim.ClaimToken!,
                reason: "in-flight-recovery",
                metadata: new Dictionary<string, string>
                {
                    ["queue.priority"] = "-100"
                });

            Assert.NotNull(prioritizedResume);
            Assert.Equal(-100, prioritizedResume!.Priority);

            var next = await queue.ClaimNextAsync(
                new AiSharedQueueClaimRequest
                {
                    RuntimeInstanceId = "runtime-2",
                    WorkerId = "worker-2"
                });

            Assert.NotNull(next);
            Assert.Equal("in-flight-recovery", next!.SharedRunId);
            Assert.Equal(-100, next.Priority);
        }

        /// <summary>
        /// Verifies that a dispatch retry preserves the original priority ordering.
        /// </summary>
        [Fact]
        public async Task RequeueAsync_Should_Preserve_Priority_Across_Dispatch_Retry()
        {
            var queue = CreateQueue();
            var now = DateTimeOffset.UtcNow;

            await queue.EnqueueAsync(
                CreateItem(
                    "normal-run",
                    priority: 0,
                    enqueuedAtUtc: now));
            await queue.EnqueueAsync(
                CreateItem(
                    "priority-run",
                    priority: -100,
                    enqueuedAtUtc: now.AddSeconds(1)));

            var claimed = await queue.ClaimNextAsync(
                new AiSharedQueueClaimRequest
                {
                    RuntimeInstanceId = "runtime-1",
                    WorkerId = "worker-1"
                });

            Assert.NotNull(claimed);
            Assert.Equal("priority-run", claimed!.SharedRunId);

            var requeued = await queue.RequeueAsync(
                claimed.SharedRunId,
                claimed.ClaimToken!,
                reason: "dispatch-retry");

            Assert.NotNull(requeued);
            Assert.Equal(-100, requeued!.Priority);

            var claimedAgain = await queue.ClaimNextAsync(
                new AiSharedQueueClaimRequest
                {
                    RuntimeInstanceId = "runtime-2",
                    WorkerId = "worker-2"
                });

            Assert.NotNull(claimedAgain);
            Assert.Equal("priority-run", claimedAgain!.SharedRunId);
            Assert.Equal(-100, claimedAgain.Priority);
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

        /// <summary>
        /// Verifies that dispatched shared queue items remain discoverable for diagnostics and recovery scans.
        /// </summary>
        [Fact]
        public async Task MarkDispatchedAsync_Should_Keep_Item_Discoverable_When_Including_Terminal_Items()
        {
            var queue = CreateQueue();

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

        /// <summary>
        /// Verifies that a dispatched Redis shared queue item can be requeued during controlled recovery.
        /// </summary>
        [Fact]
        public async Task RequeueDispatchedAsync_Should_Requeue_Dispatched_Item_And_Clear_Claim()
        {
            var queue = CreateQueue();

            await queue.EnqueueAsync(CreateItem(
                "shared-run-requeue-dispatched-1",
                pipelineKey: "test-pipeline",
                metadata: new Dictionary<string, string>
                {
                    ["test"] = "true"
                }));

            var claimed = await queue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = "runtime-1",
                WorkerId = "worker-1",
                PipelineKey = "test-pipeline",
                ClaimTtl = TimeSpan.FromMinutes(5),
                Reason = "test-claim"
            });

            Assert.NotNull(claimed);
            Assert.False(string.IsNullOrWhiteSpace(claimed!.ClaimToken));

            var dispatched = await queue.MarkDispatchedAsync(
                "shared-run-requeue-dispatched-1",
                claimed.ClaimToken!,
                reason: "test-dispatch");

            Assert.NotNull(dispatched);
            Assert.Equal(AiSharedQueueItemStatus.Dispatched, dispatched!.Status);
            Assert.Equal("runtime-1", dispatched.ClaimedByRuntimeInstanceId);
            Assert.Equal("worker-1", dispatched.ClaimedByWorkerId);
            Assert.Equal(claimed.ClaimToken, dispatched.ClaimToken);

            var requeued = await queue.RequeueDispatchedAsync(
                "shared-run-requeue-dispatched-1",
                claimed.ClaimToken!,
                reason: "test-recovery-requeue");

            Assert.NotNull(requeued);
            Assert.Equal(AiSharedQueueItemStatus.Pending, requeued!.Status);
            Assert.Null(requeued.ClaimedByRuntimeInstanceId);
            Assert.Null(requeued.ClaimedByWorkerId);
            Assert.Null(requeued.ClaimToken);
            Assert.Null(requeued.ClaimedAtUtc);
            Assert.Null(requeued.ClaimExpiresAtUtc);
            Assert.Equal("test-recovery-requeue", requeued.Reason);

            var activeItem = Assert.Single(await queue.ListAsync());
            Assert.Equal("shared-run-requeue-dispatched-1", activeItem.SharedRunId);
            Assert.Equal(AiSharedQueueItemStatus.Pending, activeItem.Status);

            var allItems = await queue.ListAsync(includeTerminal: true);
            var allItem = Assert.Single(allItems);
            Assert.Equal("shared-run-requeue-dispatched-1", allItem.SharedRunId);
            Assert.Equal(AiSharedQueueItemStatus.Pending, allItem.Status);
        }

        /// <summary>
        /// Verifies that Redis recovery requeue rejects an invalid claim token.
        /// </summary>
        [Fact]
        public async Task RequeueDispatchedAsync_Should_Return_Null_When_ClaimToken_Does_Not_Match()
        {
            var queue = CreateQueue();

            await queue.EnqueueAsync(CreateItem(
                "shared-run-requeue-dispatched-invalid-token",
                pipelineKey: "test-pipeline",
                metadata: new Dictionary<string, string>
                {
                    ["test"] = "true"
                }));

            var claimed = await queue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = "runtime-1",
                WorkerId = "worker-1",
                PipelineKey = "test-pipeline",
                ClaimTtl = TimeSpan.FromMinutes(5),
                Reason = "test-claim"
            });

            Assert.NotNull(claimed);
            Assert.False(string.IsNullOrWhiteSpace(claimed!.ClaimToken));

            await queue.MarkDispatchedAsync(
                "shared-run-requeue-dispatched-invalid-token",
                claimed.ClaimToken!,
                reason: "test-dispatch");

            var requeued = await queue.RequeueDispatchedAsync(
                "shared-run-requeue-dispatched-invalid-token",
                "wrong-token",
                reason: "test-recovery-requeue");

            Assert.Null(requeued);

            var item = await queue.GetAsync("shared-run-requeue-dispatched-invalid-token");

            Assert.NotNull(item);
            Assert.Equal(AiSharedQueueItemStatus.Dispatched, item!.Status);
            Assert.Equal(claimed.ClaimToken, item.ClaimToken);
        }

        /// <summary>
        /// Verifies that Redis recovery requeue rejects non-dispatched queue items.
        /// </summary>
        [Fact]
        public async Task RequeueDispatchedAsync_Should_Return_Null_When_Item_Is_Not_Dispatched()
        {
            var queue = CreateQueue();

            await queue.EnqueueAsync(CreateItem(
                "shared-run-requeue-dispatched-not-dispatched",
                pipelineKey: "test-pipeline",
                metadata: new Dictionary<string, string>
                {
                    ["test"] = "true"
                }));

            var claimed = await queue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = "runtime-1",
                WorkerId = "worker-1",
                PipelineKey = "test-pipeline",
                ClaimTtl = TimeSpan.FromMinutes(5),
                Reason = "test-claim"
            });

            Assert.NotNull(claimed);
            Assert.False(string.IsNullOrWhiteSpace(claimed!.ClaimToken));

            var requeued = await queue.RequeueDispatchedAsync(
                "shared-run-requeue-dispatched-not-dispatched",
                claimed.ClaimToken!,
                reason: "test-recovery-requeue");

            Assert.Null(requeued);

            var item = await queue.GetAsync("shared-run-requeue-dispatched-not-dispatched");

            Assert.NotNull(item);
            Assert.Equal(AiSharedQueueItemStatus.Claimed, item!.Status);
            Assert.Equal(claimed.ClaimToken, item.ClaimToken);
            Assert.Equal("runtime-1", item.ClaimedByRuntimeInstanceId);
            Assert.Equal("worker-1", item.ClaimedByWorkerId);
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