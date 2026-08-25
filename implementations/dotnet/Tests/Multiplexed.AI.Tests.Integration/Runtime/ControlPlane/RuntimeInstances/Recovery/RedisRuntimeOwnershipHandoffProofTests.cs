using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Redis;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue.Redis;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Tests.Fixtures;
using StackExchange.Redis;
using Xunit;

namespace Multiplexed.AI.Tests.Integration.Runtime.ControlPlane.RuntimeInstances.Recovery
{
    /// <summary>
    /// Proves the valid-runtime-ownership interval directly against the production
    /// Redis queue and shared-run compare-and-set state machines.
    /// </summary>
    public sealed class RedisRuntimeOwnershipHandoffProofTests : IAsyncLifetime
    {
        private readonly string keyScope =
            $"test:ai:runtime-ownership:{Guid.NewGuid():N}";

        private readonly string controlPlaneId =
            $"test-control-plane-{Guid.NewGuid():N}";

        private IConnectionMultiplexer? connection;

        public async Task InitializeAsync()
        {
            this.connection =
                await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        }

        public async Task DisposeAsync()
        {
            if (this.connection is null)
            {
                return;
            }

            var database =
                this.connection.GetDatabase();

            var server =
                this.connection.GetServer(
                    this.connection.GetEndPoints().First());

            var keys =
                server.Keys(
                        database: database.Database,
                        pattern: $"{this.keyScope}*")
                    .ToArray();

            if (keys.Length > 0)
            {
                await database.KeyDeleteAsync(keys);
            }

            await this.connection.CloseAsync();
            await this.connection.DisposeAsync();
        }

        [Fact]
        public async Task
            Recovery_Handoff_Should_Never_Expose_Two_Valid_Durable_Runtime_Owners()
        {
            var queue = CreateQueue();
            var sharedRunStore = CreateSharedRunStore();

            var sharedRunId =
                $"shared-run-{Guid.NewGuid():N}";

            const string executionId = "execution-1";
            const string failedRuntimeInstanceId = "runtime-old";
            const string failedLocalRunId = "local-old";
            const string replacementRuntimeInstanceId = "runtime-new";
            const string replacementLocalRunId = "local-new";

            await sharedRunStore.CreateAsync(
                CreateSharedRun(
                    sharedRunId,
                    AiSharedRunStatus.AssignedToInstance));

            await queue.EnqueueAsync(
                CreateQueueItem(sharedRunId));

            var oldClaim =
                await queue.ClaimAsync(
                    sharedRunId,
                    CreateClaimRequest(
                        "dispatcher-old",
                        "worker-old"));

            Assert.NotNull(oldClaim);
            Assert.False(
                string.IsNullOrWhiteSpace(oldClaim!.ClaimToken));

            var oldDurableOwner =
                await sharedRunStore.MarkDispatchedAsync(
                    sharedRunId,
                    failedRuntimeInstanceId,
                    failedLocalRunId,
                    executionId,
                    "initial durable owner");

            Assert.NotNull(oldDurableOwner);
            Assert.Equal(
                failedRuntimeInstanceId,
                oldDurableOwner!.AssignedRuntimeInstanceId);
            Assert.Equal(
                failedLocalRunId,
                oldDurableOwner.LocalRunId);
            Assert.Equal(executionId, oldDurableOwner.ExecutionId);

            var oldQueueOwner =
                await queue.MarkDispatchedAsync(
                    sharedRunId,
                    oldClaim.ClaimToken!,
                    "initial queue dispatch");

            Assert.NotNull(oldQueueOwner);
            Assert.Equal(
                AiSharedQueueItemStatus.Dispatched,
                oldQueueOwner!.Status);
            Assert.Equal(
                oldClaim.ClaimToken,
                oldQueueOwner.ClaimToken);

            // While the old dispatch is valid, no replacement queue claim exists.
            var blockedReplacementClaims =
                await Task.WhenAll(
                    queue.ClaimAsync(
                        sharedRunId,
                        CreateClaimRequest(
                            "dispatcher-new-a",
                            "worker-new-a")),
                    queue.ClaimAsync(
                        sharedRunId,
                        CreateClaimRequest(
                            "dispatcher-new-b",
                            "worker-new-b")));

            Assert.All(
                blockedReplacementClaims,
                claimed => Assert.Null(claimed));

            // A wrong token cannot release the current queue dispatch owner.
            var wrongTokenRelease =
                await queue.RequeueDispatchedAsync(
                    sharedRunId,
                    "wrong-token",
                    "stale recovery release");

            Assert.Null(wrongTokenRelease);

            var afterWrongToken =
                await queue.GetAsync(sharedRunId);

            Assert.NotNull(afterWrongToken);
            Assert.Equal(
                AiSharedQueueItemStatus.Dispatched,
                afterWrongToken!.Status);
            Assert.Equal(
                oldClaim.ClaimToken,
                afterWrongToken.ClaimToken);

            // A stale runtime/local CAS cannot clear the durable runtime owner.
            var wrongDurableRelease =
                await sharedRunStore
                    .MarkRequeuedAfterScaleOutIfCurrentAsync(
                        sharedRunId,
                        expectedAssignedRuntimeInstanceId:
                            "runtime-not-owner",
                        expectedLocalRunId:
                            "local-not-owner",
                        reason:
                            "stale durable recovery release");

            Assert.NotNull(wrongDurableRelease);
            Assert.Equal(
                AiSharedRunStatus.Dispatched,
                wrongDurableRelease!.Status);
            Assert.Equal(
                failedRuntimeInstanceId,
                wrongDurableRelease.AssignedRuntimeInstanceId);
            Assert.Equal(
                failedLocalRunId,
                wrongDurableRelease.LocalRunId);
            Assert.Equal(
                executionId,
                wrongDurableRelease.ExecutionId);

            // The exact token atomically releases queue ownership to Pending.
            var releasedQueue =
                await queue.RequeueDispatchedAsync(
                    sharedRunId,
                    oldClaim.ClaimToken!,
                    "exact recovery release");

            Assert.NotNull(releasedQueue);
            Assert.Equal(
                AiSharedQueueItemStatus.Pending,
                releasedQueue!.Status);
            Assert.Null(releasedQueue.ClaimToken);
            Assert.Null(
                releasedQueue.ClaimedByRuntimeInstanceId);
            Assert.Null(releasedQueue.ClaimedByWorkerId);

            // A replacement queue claim may now exist before durable SharedRun
            // ownership has been cleared. That claim alone is not a valid runtime owner.
            var replacementClaims =
                await Task.WhenAll(
                    queue.ClaimAsync(
                        sharedRunId,
                        CreateClaimRequest(
                            "dispatcher-new-a",
                            "worker-new-a")),
                    queue.ClaimAsync(
                        sharedRunId,
                        CreateClaimRequest(
                            "dispatcher-new-b",
                            "worker-new-b")));

            var replacementClaim =
                Assert.Single(
                    replacementClaims.Where(
                        claimed => claimed is not null))!;

            Assert.NotEqual(
                oldClaim.ClaimToken,
                replacementClaim.ClaimToken);

            var durableOwnerBeforeCasRelease =
                await sharedRunStore.GetAsync(sharedRunId);

            Assert.NotNull(durableOwnerBeforeCasRelease);
            Assert.Equal(
                AiSharedRunStatus.Dispatched,
                durableOwnerBeforeCasRelease!.Status);
            Assert.Equal(
                failedRuntimeInstanceId,
                durableOwnerBeforeCasRelease.AssignedRuntimeInstanceId);
            Assert.Equal(
                failedLocalRunId,
                durableOwnerBeforeCasRelease.LocalRunId);

            // Even with a replacement queue claim, a new durable owner cannot replace
            // the still-valid old durable owner.
            var prematureReplacement =
                await sharedRunStore.MarkDispatchedAsync(
                    sharedRunId,
                    replacementRuntimeInstanceId,
                    replacementLocalRunId,
                    executionId,
                    "premature replacement attempt");

            Assert.NotNull(prematureReplacement);
            Assert.Equal(
                failedRuntimeInstanceId,
                prematureReplacement!.AssignedRuntimeInstanceId);
            Assert.Equal(
                failedLocalRunId,
                prematureReplacement.LocalRunId);

            // Exact old owner CAS atomically removes the durable runtime owner.
            var releasedDurableOwner =
                await sharedRunStore
                    .MarkRequeuedAfterScaleOutIfCurrentAsync(
                        sharedRunId,
                        failedRuntimeInstanceId,
                        failedLocalRunId,
                        "exact durable recovery release");

            Assert.NotNull(releasedDurableOwner);
            Assert.Equal(
                AiSharedRunStatus.QueuedGlobally,
                releasedDurableOwner!.Status);
            Assert.Null(
                releasedDurableOwner.AssignedRuntimeInstanceId);
            Assert.Null(releasedDurableOwner.LocalRunId);

            // Execution identity survives the ownership gap.
            Assert.Equal(
                executionId,
                releasedDurableOwner.ExecutionId);

            // Only after old durable ownership is cleared can the replacement become
            // the one valid durable runtime owner.
            var replacementDurableOwner =
                await sharedRunStore.MarkDispatchedAsync(
                    sharedRunId,
                    replacementRuntimeInstanceId,
                    replacementLocalRunId,
                    executionId,
                    "replacement durable owner");

            Assert.NotNull(replacementDurableOwner);
            Assert.Equal(
                AiSharedRunStatus.Dispatched,
                replacementDurableOwner!.Status);
            Assert.Equal(
                replacementRuntimeInstanceId,
                replacementDurableOwner.AssignedRuntimeInstanceId);
            Assert.Equal(
                replacementLocalRunId,
                replacementDurableOwner.LocalRunId);
            Assert.Equal(
                executionId,
                replacementDurableOwner.ExecutionId);

            var replacementQueueOwner =
                await queue.MarkDispatchedAsync(
                    sharedRunId,
                    replacementClaim.ClaimToken!,
                    "replacement queue dispatch");

            Assert.NotNull(replacementQueueOwner);
            Assert.Equal(
                AiSharedQueueItemStatus.Dispatched,
                replacementQueueOwner!.Status);
            Assert.Equal(
                replacementClaim.ClaimToken,
                replacementQueueOwner.ClaimToken);

            // Delayed old-owner recovery cannot erase or reclaim the replacement.
            var staleDurableReleaseAfterReplacement =
                await sharedRunStore
                    .MarkRequeuedAfterScaleOutIfCurrentAsync(
                        sharedRunId,
                        failedRuntimeInstanceId,
                        failedLocalRunId,
                        "delayed stale durable release");

            Assert.NotNull(staleDurableReleaseAfterReplacement);
            Assert.Equal(
                AiSharedRunStatus.Dispatched,
                staleDurableReleaseAfterReplacement!.Status);
            Assert.Equal(
                replacementRuntimeInstanceId,
                staleDurableReleaseAfterReplacement.AssignedRuntimeInstanceId);
            Assert.Equal(
                replacementLocalRunId,
                staleDurableReleaseAfterReplacement.LocalRunId);
            Assert.Equal(
                executionId,
                staleDurableReleaseAfterReplacement.ExecutionId);

            var staleQueueReleaseAfterReplacement =
                await queue.RequeueDispatchedAsync(
                    sharedRunId,
                    oldClaim.ClaimToken!,
                    "delayed stale queue release");

            Assert.Null(staleQueueReleaseAfterReplacement);

            var finalQueue =
                await queue.GetAsync(sharedRunId);
            var finalSharedRun =
                await sharedRunStore.GetAsync(sharedRunId);

            Assert.NotNull(finalQueue);
            Assert.NotNull(finalSharedRun);

            Assert.Equal(
                AiSharedQueueItemStatus.Dispatched,
                finalQueue!.Status);
            Assert.Equal(
                replacementClaim.ClaimToken,
                finalQueue.ClaimToken);

            Assert.Equal(
                AiSharedRunStatus.Dispatched,
                finalSharedRun!.Status);
            Assert.Equal(
                replacementRuntimeInstanceId,
                finalSharedRun.AssignedRuntimeInstanceId);
            Assert.Equal(
                replacementLocalRunId,
                finalSharedRun.LocalRunId);
            Assert.Equal(executionId, finalSharedRun.ExecutionId);
        }

        private RedisAiSharedQueue CreateQueue()
        {
            if (this.connection is null)
            {
                throw new InvalidOperationException(
                    "Redis connection was not initialized.");
            }

            return new RedisAiSharedQueue(
                this.connection,
                Options.Create(
                    new RedisAiSharedQueueOptions
                    {
                        KeyPrefix =
                            $"{this.keyScope}:queue",
                        ListScanLimit = 100
                    }),
                new StaticAiControlPlaneIdResolver(
                    this.controlPlaneId));
        }

        private RedisAiSharedRunStore CreateSharedRunStore()
        {
            if (this.connection is null)
            {
                throw new InvalidOperationException(
                    "Redis connection was not initialized.");
            }

            return new RedisAiSharedRunStore(
                this.connection,
                Options.Create(
                    new RedisAiSharedRunStoreOptions
                    {
                        KeyPrefix =
                            $"{this.keyScope}:shared-runs",
                        ListScanLimit = 100
                    }),
                new StaticAiControlPlaneIdResolver(
                    this.controlPlaneId));
        }

        private AiSharedQueueClaimRequest CreateClaimRequest(
            string dispatcherId,
            string workerId)
        {
            return new AiSharedQueueClaimRequest
            {
                ControlPlaneId = this.controlPlaneId,
                RuntimeInstanceId = dispatcherId,
                WorkerId = workerId,
                TenantId = "tenant-ownership-proof",
                PipelineKey = "ownership-proof-pipeline",
                ClaimTtl = TimeSpan.FromMinutes(5),
                Reason = "runtime ownership proof claim"
            };
        }

        private AiSharedQueueItem CreateQueueItem(
            string sharedRunId)
        {
            var now =
                DateTimeOffset.UtcNow;

            return new AiSharedQueueItem
            {
                SharedRunId = sharedRunId,
                ControlPlaneId = this.controlPlaneId,
                Status = AiSharedQueueItemStatus.Pending,
                ExecutionContextSnapshot =
                    AiExecutionContextSnapshotTestFactory.Create(
                        tenantId:
                            "tenant-ownership-proof"),
                PipelineKey = "ownership-proof-pipeline",
                Priority = 0,
                EnqueuedAtUtc = now,
                UpdatedAtUtc = now,
                Reason = "runtime ownership proof enqueue",
                Metadata =
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["controlPlaneId"] =
                            this.controlPlaneId
                    }
            };
        }

        private AiSharedRunRecord CreateSharedRun(
            string sharedRunId,
            AiSharedRunStatus status)
        {
            var now =
                DateTimeOffset.UtcNow;

            return new AiSharedRunRecord
            {
                SharedRunId = sharedRunId,
                ControlPlaneId = this.controlPlaneId,
                Status = status,
                RunRequest =
                    new AiRuntimePipelineRunRequest
                    {
                        PipelineName =
                            "ownership-proof-pipeline"
                    },
                ExecutionContextSnapshot =
                    AiExecutionContextSnapshotTestFactory.Create(
                        tenantId:
                            "tenant-ownership-proof"),
                SubmittedAtUtc = now,
                UpdatedAtUtc = now,
                Metadata =
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["controlPlaneId"] =
                            this.controlPlaneId
                    }
            };
        }
    }
}
