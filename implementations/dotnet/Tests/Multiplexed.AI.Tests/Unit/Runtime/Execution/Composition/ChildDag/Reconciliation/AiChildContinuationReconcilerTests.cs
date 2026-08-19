using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Completion;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Continuation;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Reconciliation;
using Multiplexed.AI.Stores.Memory;
using Multiplexed.AI.Tests.Fixtures;
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
            Assert.Single(
                controller.Requests
                    .Select(request => request.RequestedSharedRunId)
                    .Distinct(StringComparer.Ordinal));
            Assert.All(
                controller.Requests,
                request => Assert.Equal(
                    $"child-continuation-{relation.ChildInvocationKey}",
                    request.RequestedSharedRunId));
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
            Assert.Equal(
                $"child-park-repair-{relation.ChildInvocationKey}",
                request.RequestedSharedRunId);
            Assert.Equal(
                $"child-park-repair:{relation.ChildInvocationKey}",
                request.RunRequest!.ExternalWaitContinuation!.ContinuationId);
        }

        [Fact]
        public async Task ReconcileAsync_Should_Ignore_Continuation_Candidates_Owned_By_Another_ControlPlane()
        {
            var executionStore = new MemoryAiExecutionStore();
            await executionStore.CreateAsync(
                ChildDagCompositionTestData.CreateParentRecord(),
                ChildDagCompositionTestData.CreateParentState(AiStepExecutionStatus.WaitingForExternal));

            var currentRelation = ChildDagCompositionTestData.CreateRelation(
                AiChildExecutionRelationStatus.Completed,
                AiChildContinuationStatus.Pending);
            currentRelation.ParentContinuationScheduledAtUtc = null;
            currentRelation.ParentResumedAtUtc = null;

            var staleRelation = ChildDagCompositionTestData.CreateRelation(
                AiChildExecutionRelationStatus.Completed,
                AiChildContinuationStatus.Pending,
                invocationGeneration: 1,
                controlPlaneId: "control-plane-from-previous-test");
            staleRelation.ParentContinuationScheduledAtUtc = null;
            staleRelation.ParentResumedAtUtc = null;

            var relationStore = new InMemoryAiChildExecutionRelationStore(currentRelation, staleRelation);
            var controller = new CapturingSharedRuntimeController();
            var reconciler = CreateReconciler(executionStore, relationStore, controller);

            var result = await reconciler.ReconcileAsync(batchSize: 10);

            Assert.Equal(1, result.ContinuationCandidateCount);
            Assert.Single(controller.Requests);

            var staleAuthoritative = await relationStore.GetAsync(staleRelation.ToInvocationIdentity());
            Assert.NotNull(staleAuthoritative);
            Assert.Equal(AiChildContinuationStatus.Pending, staleAuthoritative!.ContinuationStatus);
        }

        private static AiChildContinuationReconciler CreateReconciler(
            MemoryAiExecutionStore executionStore,
            InMemoryAiChildExecutionRelationStore relationStore,
            CapturingSharedRuntimeController controller)
        {
            var engineServices = new TestAiDagExecutionEngineServices(executionStore);
            var scheduler = new AiChildContinuationScheduler(controller, new InMemoryAiSharedQueue());
            var controlPlaneIdResolver = new StaticAiControlPlaneIdResolver(
                ChildDagCompositionTestData.ControlPlaneId);
            var continuationCoordinator = new AiChildContinuationCoordinator(
                relationStore,
                controlPlaneIdResolver,
                engineServices,
                scheduler);
            var completionCoordinator = new AiChildExecutionCompletionCoordinator(
                relationStore,
                engineServices,
                ChildDagCompositionTestData.CreateSnapshotService());

            return new AiChildContinuationReconciler(
                relationStore,
                controlPlaneIdResolver,
                completionCoordinator,
                continuationCoordinator,
                engineServices);
        }
    }
}
