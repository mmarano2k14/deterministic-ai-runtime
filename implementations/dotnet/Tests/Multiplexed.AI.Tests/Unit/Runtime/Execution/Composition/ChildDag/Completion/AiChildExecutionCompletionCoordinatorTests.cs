using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Completion;
using Multiplexed.AI.Stores.Memory;
using Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Completion
{
    /// <summary>
    /// Validates authoritative child completion projection and duplicate-result conflict handling.
    /// </summary>
    public sealed class AiChildExecutionCompletionCoordinatorTests
    {
        [Fact]
        public async Task CompleteIfTerminalAsync_Should_Commit_Result_And_Pending_Continuation_Exactly_Once()
        {
            var executionStore = new MemoryAiExecutionStore();
            await executionStore.CreateAsync(
                ChildDagCompositionTestData.CreateChildRecord(),
                ChildDagCompositionTestData.CreateChildState("approved"));

            var relation = ChildDagCompositionTestData.CreateRelation(AiChildExecutionRelationStatus.Waiting);
            var relationStore = new InMemoryAiChildExecutionRelationStore(relation);
            var coordinator = new AiChildExecutionCompletionCoordinator(
                relationStore,
                new TestAiDagExecutionEngineServices(executionStore),
                ChildDagCompositionTestData.CreateSnapshotService());

            var first = await coordinator.CompleteIfTerminalAsync(ChildDagCompositionTestData.ChildExecutionId);
            var second = await coordinator.CompleteIfTerminalAsync(ChildDagCompositionTestData.ChildExecutionId);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(AiChildExecutionRelationStatus.Completed, first!.Status);
            Assert.Equal(AiChildContinuationStatus.Pending, first.ContinuationStatus);
            Assert.NotNull(first.ChildResult);
            Assert.False(string.IsNullOrWhiteSpace(first.ChildResult!.ContentHash));
            Assert.NotNull(first.CompletedAtUtc);
            Assert.Null(first.ParentContinuationScheduledAtUtc);
            Assert.Null(first.ParentContinuationScheduledStepVersion);
            Assert.Null(first.ParentResumedAtUtc);
            Assert.Equal(first.ChildResult.ContentHash, second!.ChildResult!.ContentHash);
            Assert.Equal(first.CompletedAtUtc, second.CompletedAtUtc);
        }

        [Fact]
        public async Task CompleteIfTerminalAsync_Should_Reject_Conflicting_Duplicate_Result_Digest()
        {
            var executionStore = new MemoryAiExecutionStore();
            await executionStore.CreateAsync(
                ChildDagCompositionTestData.CreateChildRecord(),
                ChildDagCompositionTestData.CreateChildState("first-result"));

            var relation = ChildDagCompositionTestData.CreateRelation(AiChildExecutionRelationStatus.Waiting);
            var relationStore = new InMemoryAiChildExecutionRelationStore(relation);
            var coordinator = new AiChildExecutionCompletionCoordinator(
                relationStore,
                new TestAiDagExecutionEngineServices(executionStore),
                ChildDagCompositionTestData.CreateSnapshotService());

            var first = await coordinator.CompleteIfTerminalAsync(ChildDagCompositionTestData.ChildExecutionId);
            Assert.NotNull(first);

            await executionStore.SaveStateAsync(
                ChildDagCompositionTestData.ChildExecutionId,
                ChildDagCompositionTestData.CreateChildState("conflicting-result"));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.CompleteIfTerminalAsync(ChildDagCompositionTestData.ChildExecutionId));

            Assert.Contains("Conflicting duplicate completion", exception.Message, StringComparison.Ordinal);
            var authoritative = await relationStore.GetAsync(relation.ToInvocationIdentity());
            Assert.Equal(first!.ChildResult!.ContentHash, authoritative!.ChildResult!.ContentHash);
        }
    }
}
