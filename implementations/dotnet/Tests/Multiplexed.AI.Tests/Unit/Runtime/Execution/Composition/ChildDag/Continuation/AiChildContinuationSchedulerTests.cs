using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Observability.Events;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Continuation;
using Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Continuation
{
    /// <summary>
    /// Validates narrow normal continuation scheduling through the existing shared/global queue.
    /// </summary>
    public sealed class AiChildContinuationSchedulerTests
    {
        [Fact]
        public async Task EnqueueContinuationAsync_Should_Reuse_Stable_Logical_And_Shared_Run_Identity_Without_Recovery_Metadata()
        {
            var controller = new CapturingSharedRuntimeController();
            var observer = new CapturingAiControlPlaneObserver();
            var scheduler = new AiChildContinuationScheduler(
                controller,
                new InMemoryAiSharedQueue(),
                observer);
            var relation = CreateCompletedScheduledRelation();
            var parentRecord = CreateParentRecord();

            var first = await scheduler.EnqueueContinuationAsync(relation, parentRecord);
            var second = await scheduler.EnqueueContinuationAsync(relation, parentRecord);

            Assert.Equal(first.SharedRunId, second.SharedRunId);
            Assert.Equal(2, controller.Requests.Count);
            Assert.All(
                controller.Requests,
                captured => Assert.Equal(
                    "child-continuation-child-invocation-1",
                    captured.RequestedSharedRunId));
            Assert.Single(
                controller.Requests
                    .Select(captured => captured.RequestedSharedRunId)
                    .Distinct(StringComparer.Ordinal));

            var request = controller.Requests[1];
            Assert.Equal(AiSharedRuntimeSubmitMode.QueueFirst, request.SubmitModeOverride);
            Assert.Equal("tenant-1", request.TenantId);
            Assert.NotNull(request.RunRequest);

            var runRequest = request.RunRequest!;
            Assert.Null(runRequest.RequestedExecutionId);
            Assert.Null(runRequest.PipelineDefinitionSnapshot);
            Assert.Null(runRequest.PipelineDefinition);
            Assert.Null(runRequest.PipelineJson);
            Assert.Null(runRequest.Input);
            Assert.NotNull(runRequest.ExternalWaitContinuation);
            Assert.Equal("parent-execution-1", runRequest.ExternalWaitContinuation!.ExecutionId);
            Assert.Equal("research-call-site", runRequest.ExternalWaitContinuation.StepName);
            Assert.Equal(
                "child-continuation:child-invocation-1",
                runRequest.ExternalWaitContinuation.ContinuationId);
            Assert.False(runRequest.Metadata.ContainsKey("recovery.mode"));
            Assert.Equal("true", runRequest.Metadata["external.wait.continuation"]);

            Assert.Equal(2, observer.Events.Count);
            Assert.All(
                observer.Events,
                controlPlaneEvent => Assert.Equal(
                    AiEngineEvents.ChildDag.ContinuationDelivered,
                    controlPlaneEvent.SemanticEventType));
            Assert.Equal(
                2,
                observer.Events
                    .Select(controlPlaneEvent => controlPlaneEvent.EventId)
                    .Distinct(StringComparer.Ordinal)
                    .Count());
        }

        private static AiChildExecutionRelation CreateCompletedScheduledRelation()
        {
            return new AiChildExecutionRelation
            {
                TenantId = "tenant-1",
                ControlPlaneId = "control-plane-continuation-tests",
                ParentExecutionId = "parent-execution-1",
                ParentCallSiteId = "research-call-site",
                ChildDagId = "child-analysis",
                ChildDagDefinitionVersion = "v1",
                FrozenChildDagDefinition = Snapshot(),
                CanonicalLogicalInvocationKey = "portfolio-42|MSFT|analysis",
                ChildInvocationKey = "child-invocation-1",
                InvocationGeneration = 0,
                FrozenInvocationInput = Snapshot(),
                DelegationPolicyBindingSnapshot = Snapshot(),
                DelegationPolicyDecisionSnapshot = Snapshot(),
                Status = AiChildExecutionRelationStatus.Completed,
                ChildExecutionId = "child-execution-1",
                ChildResult = Snapshot(),
                ContinuationStatus = AiChildContinuationStatus.Scheduled,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                DelegationEvaluatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                ChildAllocatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                WaitingAtUtc = DateTimeOffset.UtcNow.AddSeconds(-30),
                CompletedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-10),
                ParentContinuationScheduledAtUtc = DateTimeOffset.UtcNow.AddSeconds(-5),
                ParentContinuationScheduledStepVersion = 10
            };
        }

        private static AiExecutionRecord CreateParentRecord()
        {
            return new AiExecutionRecord
            {
                ExecutionId = "parent-execution-1",
                PipelineName = "parent-pipeline",
                ExecutionMode = AiExecutionMode.Dag,
                Status = AiExecutionStatus.Waiting,
                ExecutionContextSnapshot = new ExecutionContextSnapshot
                {
                    ContextKey = "parent-context",
                    Project = "tests",
                    UserId = "user-1",
                    TenantId = "tenant-1",
                    TenantGroupId = "tenant-group-1",
                    CurrentNamespace = "default",
                    Namespaces = [],
                    TtlSeconds = 300
                }
            };
        }

        private static AiStoredPayload Snapshot()
        {
            return AiStoredPayload.Inline(
                "{}",
                contentType: "application/json",
                contentHash: "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a");
        }

    }
}
