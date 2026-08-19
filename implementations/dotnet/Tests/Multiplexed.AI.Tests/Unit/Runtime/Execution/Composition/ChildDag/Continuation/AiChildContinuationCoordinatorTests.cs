using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Completion;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Continuation;
using Multiplexed.AI.Stores.Memory;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue;

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
                request => Assert.Equal(
                    $"child-continuation-{relation.ChildInvocationKey}",
                    request.RequestedSharedRunId));
            Assert.Single(
                controller.Requests
                    .Select(request => request.RequestedSharedRunId)
                    .Distinct(StringComparer.Ordinal));
            Assert.Single(
                controller.Requests
                    .Select(request => request.RunRequest!.ExternalWaitContinuation!.ContinuationId)
                    .Distinct(StringComparer.Ordinal));
        }

        [Fact]
        public async Task ReconcileScheduledAsync_Should_Restore_Durable_Parent_Rbac_Context_When_Background_Reconciler_Has_No_Ambient_Context()
        {
            var executionStore = new MemoryAiExecutionStore();
            var parentRecord = ChildDagCompositionTestData.CreateParentRecord(AiExecutionStatus.Waiting);
            await executionStore.CreateAsync(
                parentRecord,
                ChildDagCompositionTestData.CreateParentState(AiStepExecutionStatus.WaitingForExternal));

            var relation = ChildDagCompositionTestData.CreateRelation(
                AiChildExecutionRelationStatus.Completed,
                AiChildContinuationStatus.Scheduled);
            var relationStore = new InMemoryAiChildExecutionRelationStore(relation);
            var accessor = new ExecutionContextAccessor();
            accessor.Clear();

            var controller = new AmbientContextCapturingSharedRuntimeController(accessor);
            var engineServices = new TestAiDagExecutionEngineServices(
                executionStore,
                accessor: accessor);
            var coordinator = new AiChildContinuationCoordinator(
                relationStore,
                new StaticAiControlPlaneIdResolver(ChildDagCompositionTestData.ControlPlaneId),
                engineServices,
                new AiChildContinuationScheduler(controller, new InMemoryAiSharedQueue()));

            await coordinator.ReconcileScheduledAsync(relation);

            var observed = Assert.Single(controller.ObservedContexts);
            Assert.NotNull(parentRecord.ExecutionContextSnapshot);
            Assert.Equal(parentRecord.ExecutionContextSnapshot!.ContextKey, observed.ContextKey);
            Assert.Equal(parentRecord.ExecutionContextSnapshot.TenantId, observed.TenantId);
            Assert.Equal(parentRecord.ExecutionContextSnapshot.TenantGroupId, observed.TenantGroupId);
            Assert.Equal(parentRecord.ExecutionContextSnapshot.TtlSeconds, observed.TtlSeconds);
            Assert.Null(accessor.Current);
        }

        [Theory]
        [InlineData(AiStepExecutionStatus.Ready)]
        [InlineData(AiStepExecutionStatus.Running)]
        [InlineData(AiStepExecutionStatus.WaitingForRetry)]
        public async Task ReconcileScheduledAsync_Should_Keep_Scheduled_And_Redrive_After_Acceptance_Until_CallSite_Is_Terminal(
            AiStepExecutionStatus stepStatus)
        {
            var executionStore = new MemoryAiExecutionStore();
            var relation = ChildDagCompositionTestData.CreateRelation(
                AiChildExecutionRelationStatus.Completed,
                AiChildContinuationStatus.Scheduled);
            await executionStore.CreateAsync(
                ChildDagCompositionTestData.CreateParentRecord(AiExecutionStatus.Running),
                ChildDagCompositionTestData.CreateParentState(
                    stepStatus,
                    version: relation.ParentContinuationScheduledStepVersion!.Value + 1));
            var relationStore = new InMemoryAiChildExecutionRelationStore(relation);
            var controller = new CapturingSharedRuntimeController();
            var coordinator = CreateCoordinator(executionStore, relationStore, controller);

            var scheduled = await coordinator.ReconcileScheduledAsync(relation);

            Assert.Equal(AiChildContinuationStatus.Scheduled, scheduled.ContinuationStatus);
            Assert.Null(scheduled.ParentResumedAtUtc);

            var request = Assert.Single(controller.Requests);
            Assert.Equal(
                $"child-continuation-{relation.ChildInvocationKey}",
                request.RequestedSharedRunId);
            Assert.NotNull(request.RunRequest?.ExternalWaitContinuation);
            Assert.Equal(
                relation.ParentExecutionId,
                request.RunRequest!.ExternalWaitContinuation!.ExecutionId);
            Assert.Equal(
                relation.ParentCallSiteId,
                request.RunRequest.ExternalWaitContinuation.StepName);
        }

        [Fact]
        public async Task ReconcileScheduledAsync_Should_Mark_Resumed_When_CallSite_Is_Terminal_After_Scheduling()
        {
            var executionStore = new MemoryAiExecutionStore();
            var relation = ChildDagCompositionTestData.CreateRelation(
                AiChildExecutionRelationStatus.Completed,
                AiChildContinuationStatus.Scheduled);
            await executionStore.CreateAsync(
                ChildDagCompositionTestData.CreateParentRecord(AiExecutionStatus.Running),
                ChildDagCompositionTestData.CreateParentState(
                    AiStepExecutionStatus.Completed,
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
            var parentRecord = ChildDagCompositionTestData.CreateParentRecord(AiExecutionStatus.Completed);
            await executionStore.CreateAsync(
                parentRecord,
                ChildDagCompositionTestData.CreateParentState(
                    AiStepExecutionStatus.Completed,
                    version: relation.ParentContinuationScheduledStepVersion!.Value + 2));
            var relationStore = new InMemoryAiChildExecutionRelationStore(relation);
            var controller = new CapturingSharedRuntimeController();
            var sharedQueue = new InMemoryAiSharedQueue();
            var continuationSharedRunId = $"child-continuation-{relation.ChildInvocationKey}";
            var now = DateTimeOffset.UtcNow;
            await sharedQueue.EnqueueAsync(
                new AiSharedQueueItem
                {
                    SharedRunId = continuationSharedRunId,
                    ControlPlaneId = relation.ControlPlaneId,
                    Status = AiSharedQueueItemStatus.Pending,
                    ExecutionContextSnapshot = parentRecord.ExecutionContextSnapshot!,
                    PipelineKey = parentRecord.PipelineName,
                    EnqueuedAtUtc = now,
                    UpdatedAtUtc = now
                });
            var claimedQueueItem = await sharedQueue.ClaimAsync(
                continuationSharedRunId,
                new AiSharedQueueClaimRequest
                {
                    RuntimeInstanceId = "mcp-control-plane",
                    WorkerId = "continuation-test-pump",
                    TenantId = relation.TenantId,
                    PipelineKey = parentRecord.PipelineName
                });
            Assert.NotNull(claimedQueueItem);
            Assert.Equal(AiSharedQueueItemStatus.Claimed, claimedQueueItem!.Status);
            var coordinator = CreateCoordinator(executionStore, relationStore, controller, sharedQueue);

            var resumed = await coordinator.ReconcileScheduledAsync(relation);

            Assert.Equal(AiChildContinuationStatus.Resumed, resumed.ContinuationStatus);
            Assert.NotNull(resumed.ParentResumedAtUtc);
            Assert.Empty(controller.Requests);
            var cancelledQueueItem = await sharedQueue.GetAsync(continuationSharedRunId);
            Assert.NotNull(cancelledQueueItem);
            Assert.Equal(AiSharedQueueItemStatus.Cancelled, cancelledQueueItem!.Status);
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

        [Fact]
        public async Task Terminal_Parent_Should_Allow_Child_To_Finish_Durably_But_Suppress_Continuation()
        {
            var executionStore = new MemoryAiExecutionStore();
            await executionStore.CreateAsync(
                ChildDagCompositionTestData.CreateParentRecord(AiExecutionStatus.Cancelled),
                ChildDagCompositionTestData.CreateParentState(AiStepExecutionStatus.WaitingForExternal));
            await executionStore.CreateAsync(
                ChildDagCompositionTestData.CreateChildRecord(AiExecutionStatus.Completed),
                ChildDagCompositionTestData.CreateChildState("orphan-result"));

            var relation = ChildDagCompositionTestData.CreateRelation(AiChildExecutionRelationStatus.Waiting);
            var relationStore = new InMemoryAiChildExecutionRelationStore(relation);
            var engineServices = new TestAiDagExecutionEngineServices(executionStore);
            var completionCoordinator = new AiChildExecutionCompletionCoordinator(
                relationStore,
                engineServices,
                ChildDagCompositionTestData.CreateSnapshotService());
            var controller = new CapturingSharedRuntimeController();
            var continuationCoordinator = new AiChildContinuationCoordinator(
                relationStore,
                new StaticAiControlPlaneIdResolver(ChildDagCompositionTestData.ControlPlaneId),
                engineServices,
                new AiChildContinuationScheduler(controller, new InMemoryAiSharedQueue()));

            var completed = await completionCoordinator.CompleteIfTerminalAsync(
                ChildDagCompositionTestData.ChildExecutionId);
            Assert.NotNull(completed);
            Assert.Equal(AiChildExecutionRelationStatus.Completed, completed!.Status);
            Assert.Equal(AiChildContinuationStatus.Pending, completed.ContinuationStatus);
            Assert.NotNull(completed.ChildResult);

            var suppressed = await continuationCoordinator.EnqueueContinuationAsync(
                completed.ToInvocationIdentity());

            Assert.Equal(AiChildContinuationStatus.Suppressed, suppressed.ContinuationStatus);
            Assert.NotNull(suppressed.ChildResult);
            Assert.NotNull(suppressed.ParentContinuationSuppressedAtUtc);
            Assert.Empty(controller.Requests);
        }

        [Fact]
        public async Task EnqueueContinuationAsync_Should_Suppress_Pending_Continuation_When_Parent_Is_Cancelled()
        {
            var executionStore = new MemoryAiExecutionStore();
            await executionStore.CreateAsync(
                ChildDagCompositionTestData.CreateParentRecord(AiExecutionStatus.Cancelled),
                ChildDagCompositionTestData.CreateParentState(AiStepExecutionStatus.WaitingForExternal));

            var relation = ChildDagCompositionTestData.CreateRelation(
                AiChildExecutionRelationStatus.Completed,
                AiChildContinuationStatus.Pending);
            var relationStore = new InMemoryAiChildExecutionRelationStore(relation);
            var controller = new CapturingSharedRuntimeController();
            var coordinator = CreateCoordinator(executionStore, relationStore, controller);

            var suppressed = await coordinator.EnqueueContinuationAsync(relation.ToInvocationIdentity());

            Assert.Equal(AiChildContinuationStatus.Suppressed, suppressed.ContinuationStatus);
            Assert.NotNull(suppressed.ParentContinuationSuppressedAtUtc);
            Assert.Contains("Cancelled", suppressed.ParentContinuationSuppressionReason!, StringComparison.Ordinal);
            Assert.Null(suppressed.ParentResumedAtUtc);
            Assert.Empty(controller.Requests);
            Assert.Empty(await relationStore.ListContinuationCandidatesAsync(10));
        }

        [Fact]
        public async Task ReconcileScheduledAsync_Should_Suppress_When_Parent_Became_Terminal_Without_Continuation_Progress()
        {
            var executionStore = new MemoryAiExecutionStore();
            var relation = ChildDagCompositionTestData.CreateRelation(
                AiChildExecutionRelationStatus.Completed,
                AiChildContinuationStatus.Scheduled);
            await executionStore.CreateAsync(
                ChildDagCompositionTestData.CreateParentRecord(AiExecutionStatus.Failed),
                ChildDagCompositionTestData.CreateParentState(
                    AiStepExecutionStatus.WaitingForExternal,
                    version: relation.ParentContinuationScheduledStepVersion!.Value));
            var relationStore = new InMemoryAiChildExecutionRelationStore(relation);
            var controller = new CapturingSharedRuntimeController();
            var coordinator = CreateCoordinator(executionStore, relationStore, controller);

            var suppressed = await coordinator.ReconcileScheduledAsync(relation);

            Assert.Equal(AiChildContinuationStatus.Suppressed, suppressed.ContinuationStatus);
            Assert.NotNull(suppressed.ParentContinuationSuppressedAtUtc);
            Assert.Contains("Failed", suppressed.ParentContinuationSuppressionReason!, StringComparison.Ordinal);
            Assert.NotNull(suppressed.ParentContinuationScheduledAtUtc);
            Assert.NotNull(suppressed.ParentContinuationScheduledStepVersion);
            Assert.Null(suppressed.ParentResumedAtUtc);
            Assert.Empty(controller.Requests);
        }

        [Fact]
        public async Task EnqueueContinuationAsync_Should_Treat_Failed_Child_As_Normal_Terminal_Outcome_For_Live_Parent()
        {
            var executionStore = new MemoryAiExecutionStore();
            await executionStore.CreateAsync(
                ChildDagCompositionTestData.CreateParentRecord(AiExecutionStatus.Waiting),
                ChildDagCompositionTestData.CreateParentState(AiStepExecutionStatus.WaitingForExternal));

            var relation = ChildDagCompositionTestData.CreateRelation(
                AiChildExecutionRelationStatus.Completed,
                AiChildContinuationStatus.Pending,
                childFailureReason: "child internal policy denied execution");
            var relationStore = new InMemoryAiChildExecutionRelationStore(relation);
            var controller = new CapturingSharedRuntimeController();
            var coordinator = CreateCoordinator(executionStore, relationStore, controller);

            var scheduled = await coordinator.EnqueueContinuationAsync(relation.ToInvocationIdentity());

            Assert.Equal(AiChildContinuationStatus.Scheduled, scheduled.ContinuationStatus);
            Assert.Equal("child internal policy denied execution", scheduled.ChildFailureReason);
            Assert.Null(scheduled.ParentContinuationSuppressedAtUtc);
            Assert.Single(controller.Requests);
        }

        [Fact]
        public async Task ReconcileScheduledAsync_Should_Reject_Relation_Owned_By_Another_ControlPlane()
        {
            var executionStore = new MemoryAiExecutionStore();
            await executionStore.CreateAsync(
                ChildDagCompositionTestData.CreateParentRecord(),
                ChildDagCompositionTestData.CreateParentState(AiStepExecutionStatus.WaitingForExternal));

            var relation = ChildDagCompositionTestData.CreateRelation(
                AiChildExecutionRelationStatus.Completed,
                AiChildContinuationStatus.Scheduled,
                controlPlaneId: "control-plane-from-previous-test");
            var relationStore = new InMemoryAiChildExecutionRelationStore(relation);
            var controller = new CapturingSharedRuntimeController();
            var coordinator = CreateCoordinator(executionStore, relationStore, controller);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.ReconcileScheduledAsync(relation));

            Assert.Contains("cannot be reconciled", exception.Message, StringComparison.Ordinal);
            Assert.Empty(controller.Requests);
        }

        private sealed class AmbientContextCapturingSharedRuntimeController : IAiSharedRuntimeController
        {
            private readonly IExecutionContextAccessor accessor;

            public AmbientContextCapturingSharedRuntimeController(IExecutionContextAccessor accessor)
            {
                this.accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
            }

            public List<Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext> ObservedContexts { get; } = [];

            public Task<AiSharedRuntimeControllerResult> ExecuteAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default) =>
                SubmitRunAsync(request, cancellationToken);

            public Task<AiSharedRuntimeControllerResult> SubmitRunAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var current = this.accessor.Current
                    ?? throw new InvalidOperationException("Expected a restored background RBAC execution context.");
                this.ObservedContexts.Add(current);

                var now = DateTimeOffset.UtcNow;
                var run = new AiSharedRunRecord
                {
                    SharedRunId = request.RequestedSharedRunId!,
                    Status = AiSharedRunStatus.QueuedGlobally,
                    RunRequest = request.RunRequest!,
                    ExecutionContextSnapshot = request.RunRequest!.ExecutionContextSnapshot!,
                    PipelineKey = request.PipelineKey,
                    SubmittedAtUtc = now,
                    UpdatedAtUtc = now,
                    Metadata = request.Metadata
                };

                return Task.FromResult(
                    new AiSharedRuntimeControllerResult
                    {
                        Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                        Success = true,
                        SharedRunId = run.SharedRunId,
                        Run = run,
                        StartedAtUtc = now,
                        CompletedAtUtc = now
                    });
            }

            public Task<AiSharedRuntimeControllerResult> GetRunAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task<AiSharedRuntimeControllerResult> ListRunsAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task<AiSharedRuntimeControllerResult> CancelRunAsync(
                AiSharedRuntimeControllerRequest request,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }

        private static AiChildContinuationCoordinator CreateCoordinator(
            MemoryAiExecutionStore executionStore,
            InMemoryAiChildExecutionRelationStore relationStore,
            CapturingSharedRuntimeController controller,
            InMemoryAiSharedQueue? sharedQueue = null)
        {
            var engineServices = new TestAiDagExecutionEngineServices(executionStore);
            var scheduler = new AiChildContinuationScheduler(controller, sharedQueue ?? new InMemoryAiSharedQueue());
            return new AiChildContinuationCoordinator(
                relationStore,
                new StaticAiControlPlaneIdResolver(ChildDagCompositionTestData.ControlPlaneId),
                engineServices,
                scheduler);
        }
    }
}
