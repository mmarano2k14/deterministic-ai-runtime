using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Recovery.Transition
{
    /// <summary>
    /// Unit tests for <see cref="AiRuntimeExecutionRecoveryTransitionService"/>.
    /// </summary>
    public sealed class AiRuntimeExecutionRecoveryTransitionServiceTests
    {
        /// <summary>
        /// Verifies that unresolved ownership is rejected.
        /// </summary>
        [Fact]
        public async Task ApplyAsync_Should_Reject_When_Ownership_Is_Not_Resolved()
        {
            var sharedQueue = new InMemoryAiSharedQueue();
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();
            var service = new AiRuntimeExecutionRecoveryTransitionService(
                sharedQueue,
                runExecutionIndex);

            var result = await service.ApplyAsync(new AiRuntimeExecutionRecoveryTransitionRequest
            {
                Ownership = CreateOwnership(
                    resolved: false,
                    canRecover: false),
                DryRun = true
            });

            Assert.False(result.Accepted);
            Assert.False(result.Changed);
            Assert.Equal("none", result.Action);
            Assert.Equal("ownership-not-resolved", result.Reason);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);
            Assert.Equal("run-1", result.LocalRunId);
            Assert.Equal("execution-1", result.ExecutionId);
        }

        /// <summary>
        /// Verifies that non-recoverable ownership is rejected.
        /// </summary>
        [Fact]
        public async Task ApplyAsync_Should_Reject_When_Ownership_Is_Not_Recoverable()
        {
            var sharedQueue = new InMemoryAiSharedQueue();
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();
            var service = new AiRuntimeExecutionRecoveryTransitionService(
                sharedQueue,
                runExecutionIndex);

            var result = await service.ApplyAsync(new AiRuntimeExecutionRecoveryTransitionRequest
            {
                Ownership = CreateOwnership(
                    resolved: true,
                    canRecover: false),
                DryRun = true
            });

            Assert.False(result.Accepted);
            Assert.False(result.Changed);
            Assert.Equal("none", result.Action);
            Assert.Equal("ownership-not-recoverable", result.Reason);
            Assert.Equal("shared-run-1", result.SharedRunId);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);
            Assert.Equal("run-1", result.LocalRunId);
            Assert.Equal("execution-1", result.ExecutionId);
        }

        /// <summary>
        /// Verifies that recoverable ownership is accepted during dry-run without mutation.
        /// </summary>
        [Fact]
        public async Task ApplyAsync_Should_Accept_Recoverable_Ownership_When_DryRun()
        {
            var sharedQueue = new InMemoryAiSharedQueue();
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();
            var service = new AiRuntimeExecutionRecoveryTransitionService(
                sharedQueue,
                runExecutionIndex);

            var result = await service.ApplyAsync(new AiRuntimeExecutionRecoveryTransitionRequest
            {
                Ownership = CreateOwnership(
                    resolved: true,
                    canRecover: true),
                Reason = "test-dry-run",
                DryRun = true
            });

            var indexEntry = await runExecutionIndex.GetAsync("run-1");

            Assert.True(result.Accepted);
            Assert.False(result.Changed);
            Assert.Equal("dry-run-requeue-shared-run", result.Action);
            Assert.Equal("test-dry-run", result.Reason);
            Assert.Equal("shared-run-1", result.SharedRunId);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);
            Assert.Equal("run-1", result.LocalRunId);
            Assert.Equal("execution-1", result.ExecutionId);
            Assert.Null(indexEntry);
        }

        /// <summary>
        /// Verifies that recoverable ownership requeues the dispatched shared queue item when mutation is enabled.
        /// </summary>
        [Fact]
        public async Task ApplyAsync_Should_Requeue_Dispatched_Shared_Queue_Item_When_Not_DryRun()
        {
            var sharedQueue = new InMemoryAiSharedQueue();
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();
            var service = new AiRuntimeExecutionRecoveryTransitionService(
                sharedQueue,
                runExecutionIndex);

            await sharedQueue.EnqueueAsync(new AiSharedQueueItem
            {
                SharedRunId = "shared-run-1",
                Status = AiSharedQueueItemStatus.Pending,
                ExecutionContextSnapshot = CreateExecutionContextSnapshot(),
                PipelineKey = "transition-test",
                Priority = 0,
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["test"] = "true"
                }
            });

            await runExecutionIndex.RegisterQueuedAsync(new AiRuntimeRunExecutionIndexEntry
            {
                RunId = "run-1",
                ExecutionId = "execution-1",
                RuntimeInstanceId = "runtime-1",
                Status = "queued",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExecutionContextSnapshot = CreateExecutionContextSnapshot(),
                Metadata = new Dictionary<string, string>
                {
                    ["test"] = "true"
                }
            });

            await runExecutionIndex.MarkStartedAsync(
                "run-1",
                "execution-1");

            var claimed = await sharedQueue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = "runtime-1",
                WorkerId = "worker-1",
                PipelineKey = "transition-test",
                ClaimTtl = TimeSpan.FromMinutes(5),
                Reason = "test-claim"
            });

            Assert.NotNull(claimed);
            Assert.False(string.IsNullOrWhiteSpace(claimed!.ClaimToken));

            await sharedQueue.MarkDispatchedAsync(
                "shared-run-1",
                claimed.ClaimToken!,
                reason: "test-dispatch");

            var result = await service.ApplyAsync(new AiRuntimeExecutionRecoveryTransitionRequest
            {
                Ownership = CreateOwnership(
                    resolved: true,
                    canRecover: true,
                    claimToken: claimed.ClaimToken),
                Reason = "test-recovery-requeue",
                DryRun = false
            });

            var item = await sharedQueue.GetAsync("shared-run-1");
            var indexEntry = await runExecutionIndex.GetAsync("run-1");
            var unfinishedRuns = await runExecutionIndex.ListUnfinishedByRuntimeInstanceAsync("runtime-1");

            Assert.True(result.Accepted);
            Assert.True(result.Changed);
            Assert.Equal("requeue-shared-run", result.Action);
            Assert.Equal("test-recovery-requeue", result.Reason);
            Assert.Equal("shared-run-1", result.SharedRunId);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);
            Assert.Equal("run-1", result.LocalRunId);
            Assert.Equal("execution-1", result.ExecutionId);

            Assert.NotNull(item);
            Assert.Equal(AiSharedQueueItemStatus.Pending, item!.Status);
            Assert.Null(item.ClaimToken);
            Assert.Null(item.ClaimedByRuntimeInstanceId);
            Assert.Null(item.ClaimedByWorkerId);
            Assert.Equal("test-recovery-requeue", item.Reason);

            Assert.NotNull(indexEntry);
            Assert.Equal("run-1", indexEntry!.RunId);
            Assert.Equal("execution-1", indexEntry.ExecutionId);
            Assert.Equal("runtime-1", indexEntry.RuntimeInstanceId);
            Assert.Equal("requeued-for-recovery", indexEntry.Status);
            Assert.Equal("test-recovery-requeue", indexEntry.FailureReason);
            Assert.NotNull(indexEntry.CompletedAtUtc);

            Assert.Empty(unfinishedRuns);
        }

        /// <summary>
        /// Creates an ownership resolution result.
        /// </summary>
        /// <param name="resolved">Whether ownership is resolved.</param>
        /// <param name="canRecover">Whether ownership is recoverable.</param>
        /// <param name="claimToken">The optional claim token.</param>
        /// <returns>The ownership resolution result.</returns>
        private static AiSharedRunOwnershipResolutionResult CreateOwnership(
            bool resolved,
            bool canRecover,
            string? claimToken = "claim-token-1")
        {
            return new AiSharedRunOwnershipResolutionResult
            {
                Resolved = resolved,
                SharedRunId = resolved ? "shared-run-1" : null,
                RuntimeInstanceId = "runtime-1",
                LocalRunId = "run-1",
                ExecutionId = "execution-1",
                TenantId = "tenant-1",
                TenantGroupId = "tenant-group-1",
                QueueStatus = resolved ? AiSharedQueueItemStatus.Dispatched : null,
                SharedRunStatus = resolved ? AiSharedRunStatus.Dispatched : null,
                ClaimToken = resolved ? claimToken : null,
                CanRecover = canRecover,
                Reason = resolved ? "shared-run-ownership-resolved" : "shared-run-ownership-not-found"
            };
        }

        /// <summary>
        /// Creates an execution context snapshot.
        /// </summary>
        /// <returns>The execution context snapshot.</returns>
        private static ExecutionContextSnapshot CreateExecutionContextSnapshot()
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = "ctx-tenant-1",
                Project = "transition-tests",
                UserId = "test-user",
                TenantId = "tenant-1",
                TenantGroupId = "tenant-group-1",
                CurrentNamespace = "default",
                Namespaces = new List<NamespaceEntry>(),
                InFlightCount = 0,
                TtlSeconds = 300,
                CreatedAtUtc = DateTime.Now
            };
        }
    }
}