using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Allocation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Generation;
using Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Generation
{
    /// <summary>
    /// Validates sticky same-generation semantics and explicit crash-safe child invocation generation advancement.
    /// </summary>
    public sealed class AiChildInvocationGenerationCoordinatorTests
    {
        [Fact]
        public async Task Ordinary_Reentry_Should_Keep_Failed_Generation_Sticky()
        {
            var failed = ChildDagCompositionTestData.CreateRelation(
                AiChildExecutionRelationStatus.Completed,
                AiChildContinuationStatus.Resumed,
                childFailureReason: "child policy denied execution");
            var store = new InMemoryAiChildExecutionRelationStore(failed);
            var allocator = new AiChildExecutionAllocator(
                store,
                ChildDagCompositionTestData.CreateSnapshotService());

            var reentered = await allocator.AllocateAsync(failed.ToInvocationIdentity());

            Assert.Equal(0, reentered.InvocationGeneration);
            Assert.Equal(failed.ChildExecutionId, reentered.ChildExecutionId);
            Assert.Equal(AiChildExecutionRelationStatus.Completed, reentered.Status);
            Assert.Equal("child policy denied execution", reentered.ChildFailureReason);
            Assert.Null(reentered.NextInvocationGeneration);
            Assert.Equal(1, store.Count);
        }

        [Fact]
        public async Task PrepareNextGenerationAsync_Should_Converge_Concurrent_Retry_Decisions_On_One_New_Relation()
        {
            var failed = ChildDagCompositionTestData.CreateRelation(
                AiChildExecutionRelationStatus.Completed,
                AiChildContinuationStatus.Resumed,
                childFailureReason: "child execution failed");
            var store = new InMemoryAiChildExecutionRelationStore(failed);
            var coordinator = new AiChildInvocationGenerationCoordinator(store);

            var results = await Task.WhenAll(
                Enumerable.Range(0, 8)
                    .Select(_ => coordinator.PrepareNextGenerationAsync(
                        failed.ToInvocationIdentity(),
                        "explicit child retry approved")));

            var next = results[0];
            Assert.All(results, item => Assert.Equal(next.ChildInvocationKey, item.ChildInvocationKey));
            Assert.All(results, item => Assert.Equal(1, item.InvocationGeneration));
            Assert.All(results, item => Assert.Equal(AiChildExecutionRelationStatus.DelegationPolicyPending, item.Status));
            Assert.NotEqual(failed.ChildInvocationKey, next.ChildInvocationKey);
            Assert.Equal(failed.CanonicalLogicalInvocationKey, next.CanonicalLogicalInvocationKey);
            Assert.Equal(failed.ControlPlaneId, next.ControlPlaneId);
            Assert.Equal(2, store.Count);

            var current = await store.GetAsync(failed.ToInvocationIdentity());
            Assert.NotNull(current);
            Assert.Equal(1, current!.NextInvocationGeneration);
            Assert.NotNull(current.NextInvocationGenerationDecidedAtUtc);
            Assert.Equal("explicit child retry approved", current.NextInvocationGenerationDecisionReason);

            Assert.Null(next.ChildExecutionId);
            Assert.Null(next.ChildAllocatedAtUtc);
            Assert.Null(next.DelegationPolicyDecisionSnapshot);
            Assert.Equal(AiChildContinuationStatus.None, next.ContinuationStatus);
            Assert.Equal(failed.FrozenChildDagDefinition.ContentHash, next.FrozenChildDagDefinition.ContentHash);
            Assert.Equal(failed.FrozenInvocationInput.ContentHash, next.FrozenInvocationInput.ContentHash);
            Assert.Equal(failed.DelegationPolicyBindingSnapshot.ContentHash, next.DelegationPolicyBindingSnapshot.ContentHash);
        }

        [Fact]
        public async Task PrepareNextGenerationAsync_Should_Recreate_Same_Relation_After_Decision_To_Relation_Crash_Window()
        {
            var failed = ChildDagCompositionTestData.CreateRelation(
                AiChildExecutionRelationStatus.Completed,
                AiChildContinuationStatus.Resumed,
                childFailureReason: "child execution failed");
            failed.NextInvocationGeneration = 1;
            failed.NextInvocationGenerationDecidedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-5);
            failed.NextInvocationGenerationDecisionReason = "durable retry decision";

            var store = new InMemoryAiChildExecutionRelationStore(failed);
            var coordinator = new AiChildInvocationGenerationCoordinator(store);

            var recovered = await coordinator.PrepareNextGenerationAsync(
                failed.ToInvocationIdentity(),
                "replayed caller reason is not authoritative");

            Assert.Equal(1, recovered.InvocationGeneration);
            Assert.Equal(AiChildExecutionRelationStatus.DelegationPolicyPending, recovered.Status);
            Assert.Equal(2, store.Count);

            var current = await store.GetAsync(failed.ToInvocationIdentity());
            Assert.NotNull(current);
            Assert.Equal("durable retry decision", current!.NextInvocationGenerationDecisionReason);
        }

        [Fact]
        public async Task PrepareNextGenerationAsync_Should_Reject_Successful_Completed_Child()
        {
            var successful = ChildDagCompositionTestData.CreateRelation(
                AiChildExecutionRelationStatus.Completed,
                AiChildContinuationStatus.Resumed);
            var store = new InMemoryAiChildExecutionRelationStore(successful);
            var coordinator = new AiChildInvocationGenerationCoordinator(store);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.PrepareNextGenerationAsync(
                    successful.ToInvocationIdentity(),
                    "retry successful child"));

            Assert.Equal(1, store.Count);
        }

        [Fact]
        public async Task PrepareNextGenerationAsync_Should_Reject_Failed_Child_Until_Parent_Continuation_Is_Resumed()
        {
            var failed = ChildDagCompositionTestData.CreateRelation(
                AiChildExecutionRelationStatus.Completed,
                AiChildContinuationStatus.Scheduled,
                childFailureReason: "child execution failed");
            var store = new InMemoryAiChildExecutionRelationStore(failed);
            var coordinator = new AiChildInvocationGenerationCoordinator(store);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.PrepareNextGenerationAsync(
                    failed.ToInvocationIdentity(),
                    "retry before parent resume"));

            Assert.Equal(1, store.Count);
        }

        [Fact]
        public async Task PrepareNextGenerationAsync_Should_Allow_Explicit_Retry_After_Durable_Delegation_Denial()
        {
            var denied = ChildDagCompositionTestData.CreateRelation(
                AiChildExecutionRelationStatus.DelegationDenied);
            var store = new InMemoryAiChildExecutionRelationStore(denied);
            var coordinator = new AiChildInvocationGenerationCoordinator(store);

            var next = await coordinator.PrepareNextGenerationAsync(
                denied.ToInvocationIdentity(),
                "explicitly retry delegation");

            Assert.Equal(1, next.InvocationGeneration);
            Assert.Equal(AiChildExecutionRelationStatus.DelegationPolicyPending, next.Status);
            Assert.Null(next.ChildExecutionId);
            Assert.Equal(2, store.Count);
        }
    }
}
