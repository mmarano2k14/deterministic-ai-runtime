using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Completion;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Continuation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Reconciliation;
using Multiplexed.AI.Stores.Memory;
using Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Reconciliation
{
    /// <summary>
    /// Validates that durable polling closes lost-wakeup and park-consistency windows without transient signals.
    /// </summary>
    public sealed class AiChildContinuationReconcilerTests
    {
        [Fact]
        public async Task ReconcileAsync_Should_Schedule_Completed_Pending_Continuation_Without_FastPath_Signal()
        {
            var executionStore = new MemoryAiExecutionStore();
            await executionStore.CreateAsync(
                ChildDagCompositionTestData.CreateParentRecord(),
                ChildDagCompositionTestData.CreateParentState(AiStepExecutionStatus.WaitingForExternal));

            var relation = ChildDagCompositionTestData.CreateRelation(
                AiChildExecutionRelationStatus.Completed,
                AiChildContinuationStatus.Pending);
            relation.ParentContinuationScheduledAtUtc = null;
            relation.ParentResumedAtUtc = null;
            var relationStore = new InMemoryAiChildExecutionRelationStore(relation);
            var controller = new CapturingSharedRuntimeController();
            var reconciler = CreateReconciler(executionStore, relationStore, controller);

            var result = await reconciler.ReconcileAsync(batchSize: 10);
            var authoritative = await relationStore.GetAsync(relation.ToInvocationIdentity());

            Assert.Equal(1, result.ContinuationCandidateCount);
            Assert.NotNull(authoritative);
            Assert.Equal(AiChildContinuationStatus.Scheduled, authoritative!.ContinuationStatus);
            Assert.NotNull(authoritative.ParentContinuationScheduledAtUtc);
            Assert.Single(controller.Requests);
        }

        [Fact]
        public async Task ReconcileAsync_Should_Reenqueue_Scheduled_Continuation_After_Crash_Before_Parent_Progress()
        {
            var executionStore = new MemoryAiExecutionStore();
            await executionStore.CreateAsync(
                ChildDagCompositionTestData.CreateParentRecord(),
                ChildDagCompositionTestData.CreateParentState(AiStepExecutionStatus.WaitingForExternal));

            var relation = ChildDagCompositionTestData.CreateRelation(
                AiChildExecutionRelationStatus.Completed,
                AiChildContinuationStatus.Scheduled);
            var relationStore = new InMemoryAiChildExecutionRelationStore(relation);
            var controller = new CapturingSharedRuntimeController();
            var reconciler = CreateReconciler(executionStore, relationStore, controller);

            await reconciler.ReconcileAsync(batchSize: 10);
            await reconciler.ReconcileAsync(batchSize: 10);

            Assert.Equal(2, controller.Requests.Count);
            Assert.Equal(
                2,
                controller.Requests
                    .Select(request => request.RequestedSharedRunId)
                    .Distinct(StringComparer.Ordinal)
                    .Count());
            Assert.All(
                controller.Requests,
                request => Assert.True(
                    request.RequestedSharedRunId?.StartsWith(
                        $"child-continuation-{relation.ChildInvocationKey}-",
                        StringComparison.Ordinal) == true));
            Assert.Single(
                controller.Requests
                    .Select(request => request.RunRequest!.ExternalWaitContinuation!.ContinuationId)
                    .Distinct(StringComparer.Ordinal));

            var authoritative = await relationStore.GetAsync(relation.ToInvocationIdentity());
            Assert.Equal(AiChildContinuationStatus.Scheduled, authoritative!.ContinuationStatus);
            Assert.Null(authoritative.ParentResumedAtUtc);
        }

        [Fact]
        public async Task ReconcileAsync_Should_Redrive_Known_Park_Inconsistency_After_Claim_Derived_Grace()
        {
            var executionStore = new MemoryAiExecutionStore();
            await executionStore.CreateAsync(
                ChildDagCompositionTestData.CreateParentRecord(),
                ChildDagCompositionTestData.CreateParentState(
                    AiStepExecutionStatus.WaitingForExternal,
                    claimTimeoutSeconds: 4));

            var relation = ChildDagCompositionTestData.CreateRelation(
                AiChildExecutionRelationStatus.ChildAllocated,
                childAllocatedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));
            var relationStore = new InMemoryAiChildExecutionRelationStore(relation);
            var controller = new CapturingSharedRuntimeController();
            var reconciler = CreateReconciler(executionStore, relationStore, controller);

            var result = await reconciler.ReconcileAsync(batchSize: 10);

            Assert.Equal(1, result.ParkConsistencyCandidateCount);
            Assert.Equal(1, result.ParkRepairEnqueueCount);
            var request = Assert.Single(controller.Requests);
            Assert.True(
                request.RequestedSharedRunId?.StartsWith(
                    $"child-park-repair-{relation.ChildInvocationKey}-",
                    StringComparison.Ordinal) == true);
            Assert.Equal(
                $"child-park-repair:{relation.ChildInvocationKey}",
                request.RunRequest!.ExternalWaitContinuation!.ContinuationId);
        }

        private static AiChildContinuationReconciler CreateReconciler(
            MemoryAiExecutionStore executionStore,
            InMemoryAiChildExecutionRelationStore relationStore,
            CapturingSharedRuntimeController controller)
        {
            var engineServices = new TestAiDagExecutionEngineServices(executionStore);
            var scheduler = new AiChildContinuationScheduler(controller);
            var continuationCoordinator = new AiChildContinuationCoordinator(
                relationStore,
                engineServices,
                scheduler);
            var completionCoordinator = new AiChildExecutionCompletionCoordinator(
                relationStore,
                engineServices,
                ChildDagCompositionTestData.CreateSnapshotService());

            return new AiChildContinuationReconciler(
                relationStore,
                completionCoordinator,
                continuationCoordinator,
                engineServices);
        }
    }
}
