using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Continuation;
using Multiplexed.AI.Stores.Memory;
using Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Continuation
{
    /// <summary>
    /// Validates durable continuation CAS and parent-state convergence independently of physical delivery count.
    /// </summary>
    public sealed class AiChildContinuationCoordinatorTests
    {
        [Fact]
        public async Task EnqueueContinuationAsync_Should_Converge_Concurrent_Schedulers_On_One_Durable_Scheduled_State()
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
            var coordinator = CreateCoordinator(executionStore, relationStore, controller);

            var attempts = Enumerable.Range(0, 8)
                .Select(_ => coordinator.EnqueueContinuationAsync(relation.ToInvocationIdentity()))
                .ToArray();

            var results = await Task.WhenAll(attempts);
            var authoritative = await relationStore.GetAsync(relation.ToInvocationIdentity());

            Assert.NotNull(authoritative);
            Assert.Equal(AiChildContinuationStatus.Scheduled, authoritative!.ContinuationStatus);
            Assert.NotNull(authoritative.ParentContinuationScheduledAtUtc);
            Assert.NotNull(authoritative.ParentContinuationScheduledStepVersion);
            Assert.Null(authoritative.ParentResumedAtUtc);
            Assert.All(results, result => Assert.Equal(AiChildContinuationStatus.Scheduled, result.ContinuationStatus));
            Assert.NotEmpty(controller.Requests);
            Assert.All(
                controller.Requests,
                request => Assert.True(
                    request.RequestedSharedRunId?.StartsWith(
                        $"child-continuation-{relation.ChildInvocationKey}-",
                        StringComparison.Ordinal) == true));
            Assert.Equal(
                controller.Requests.Count,
                controller.Requests
                    .Select(request => request.RequestedSharedRunId)
                    .Distinct(StringComparer.Ordinal)
                    .Count());
            Assert.Single(
                controller.Requests
                    .Select(request => request.RunRequest!.ExternalWaitContinuation!.ContinuationId)
                    .Distinct(StringComparer.Ordinal));
        }

        [Fact]
        public async Task ReconcileScheduledAsync_Should_Mark_Resumed_After_Parent_Durable_Progress()
        {
            var executionStore = new MemoryAiExecutionStore();
            var relation = ChildDagCompositionTestData.CreateRelation(
                AiChildExecutionRelationStatus.Completed,
                AiChildContinuationStatus.Scheduled);
            await executionStore.CreateAsync(
                ChildDagCompositionTestData.CreateParentRecord(AiExecutionStatus.Running),
                ChildDagCompositionTestData.CreateParentState(
                    AiStepExecutionStatus.Ready,
                    version: relation.ParentContinuationScheduledStepVersion!.Value + 1));
            var relationStore = new InMemoryAiChildExecutionRelationStore(relation);
            var controller = new CapturingSharedRuntimeController();
            var coordinator = CreateCoordinator(executionStore, relationStore, controller);

            var resumed = await coordinator.ReconcileScheduledAsync(relation);

            Assert.Equal(AiChildContinuationStatus.Resumed, resumed.ContinuationStatus);
            Assert.NotNull(resumed.ParentResumedAtUtc);
            Assert.Empty(controller.Requests);
        }

        [Fact]
        public async Task ReconcileScheduledAsync_Should_Mark_Resumed_When_Continuation_Completes_Parent_Before_Poller_Observation()
        {
            var executionStore = new MemoryAiExecutionStore();
            var relation = ChildDagCompositionTestData.CreateRelation(
                AiChildExecutionRelationStatus.Completed,
                AiChildContinuationStatus.Scheduled);
            await executionStore.CreateAsync(
                ChildDagCompositionTestData.CreateParentRecord(AiExecutionStatus.Completed),
                ChildDagCompositionTestData.CreateParentState(
                    AiStepExecutionStatus.Completed,
                    version: relation.ParentContinuationScheduledStepVersion!.Value + 2));
            var relationStore = new InMemoryAiChildExecutionRelationStore(relation);
            var controller = new CapturingSharedRuntimeController();
            var coordinator = CreateCoordinator(executionStore, relationStore, controller);

            var resumed = await coordinator.ReconcileScheduledAsync(relation);

            Assert.Equal(AiChildContinuationStatus.Resumed, resumed.ContinuationStatus);
            Assert.NotNull(resumed.ParentResumedAtUtc);
            Assert.Empty(controller.Requests);
        }

        [Fact]
        public async Task EnqueueContinuationAsync_Should_Handle_Child_Completion_Before_Parent_Park_Without_Lost_Wakeup()
        {
            var executionStore = new MemoryAiExecutionStore();
            await executionStore.CreateAsync(
                ChildDagCompositionTestData.CreateParentRecord(AiExecutionStatus.Running),
                ChildDagCompositionTestData.CreateParentState(
                    AiStepExecutionStatus.Running,
                    version: 7));

            var relation = ChildDagCompositionTestData.CreateRelation(
                AiChildExecutionRelationStatus.Completed,
                AiChildContinuationStatus.Pending);
            relation.ParentContinuationScheduledAtUtc = null;
            relation.ParentResumedAtUtc = null;
            var relationStore = new InMemoryAiChildExecutionRelationStore(relation);
            var controller = new CapturingSharedRuntimeController();
            var coordinator = CreateCoordinator(executionStore, relationStore, controller);

            var scheduled = await coordinator.EnqueueContinuationAsync(relation.ToInvocationIdentity());

            Assert.Equal(AiChildContinuationStatus.Scheduled, scheduled.ContinuationStatus);
            Assert.NotNull(scheduled.ParentContinuationScheduledAtUtc);
            Assert.Null(scheduled.ParentResumedAtUtc);
            Assert.Empty(controller.Requests);

            Assert.Equal(7, scheduled.ParentContinuationScheduledStepVersion);

            var progressedState = ChildDagCompositionTestData.CreateParentState(
                AiStepExecutionStatus.Completed,
                version: scheduled.ParentContinuationScheduledStepVersion!.Value + 1);
            await executionStore.SaveStateAsync(ChildDagCompositionTestData.ParentExecutionId, progressedState);

            var resumed = await coordinator.ReconcileScheduledAsync(scheduled);

            Assert.Equal(AiChildContinuationStatus.Resumed, resumed.ContinuationStatus);
            Assert.NotNull(resumed.ParentResumedAtUtc);
            Assert.Empty(controller.Requests);
        }

        private static AiChildContinuationCoordinator CreateCoordinator(
            MemoryAiExecutionStore executionStore,
            InMemoryAiChildExecutionRelationStore relationStore,
            CapturingSharedRuntimeController controller)
        {
            var engineServices = new TestAiDagExecutionEngineServices(executionStore);
            var scheduler = new AiChildContinuationScheduler(controller);
            return new AiChildContinuationCoordinator(relationStore, engineServices, scheduler);
        }
    }
}
