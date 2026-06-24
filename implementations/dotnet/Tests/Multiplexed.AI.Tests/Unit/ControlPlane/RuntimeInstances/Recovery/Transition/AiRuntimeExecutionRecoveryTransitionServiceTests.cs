using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery.Transition;

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
            var service = new AiRuntimeExecutionRecoveryTransitionService();

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
            var service = new AiRuntimeExecutionRecoveryTransitionService();

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
            var service = new AiRuntimeExecutionRecoveryTransitionService();

            var result = await service.ApplyAsync(new AiRuntimeExecutionRecoveryTransitionRequest
            {
                Ownership = CreateOwnership(
                    resolved: true,
                    canRecover: true),
                Reason = "test-dry-run",
                DryRun = true
            });

            Assert.True(result.Accepted);
            Assert.False(result.Changed);
            Assert.Equal("dry-run-requeue-shared-run", result.Action);
            Assert.Equal("test-dry-run", result.Reason);
            Assert.Equal("shared-run-1", result.SharedRunId);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);
            Assert.Equal("run-1", result.LocalRunId);
            Assert.Equal("execution-1", result.ExecutionId);
        }

        /// <summary>
        /// Verifies that non-dry-run mutation is not implemented yet.
        /// </summary>
        [Fact]
        public async Task ApplyAsync_Should_Reject_Mutation_When_Not_Implemented()
        {
            var service = new AiRuntimeExecutionRecoveryTransitionService();

            var result = await service.ApplyAsync(new AiRuntimeExecutionRecoveryTransitionRequest
            {
                Ownership = CreateOwnership(
                    resolved: true,
                    canRecover: true),
                DryRun = false
            });

            Assert.False(result.Accepted);
            Assert.False(result.Changed);
            Assert.Equal("none", result.Action);
            Assert.Equal("recovery-transition-mutation-not-implemented", result.Reason);
        }

        /// <summary>
        /// Creates an ownership resolution result.
        /// </summary>
        /// <param name="resolved">Whether ownership is resolved.</param>
        /// <param name="canRecover">Whether ownership is recoverable.</param>
        /// <returns>The ownership resolution result.</returns>
        private static AiSharedRunOwnershipResolutionResult CreateOwnership(
            bool resolved,
            bool canRecover)
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
                ClaimToken = resolved ? "claim-token-1" : null,
                CanRecover = canRecover,
                Reason = resolved ? "shared-run-ownership-resolved" : "shared-run-ownership-not-found"
            };
        }
    }
}