using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.ProcessHostPool;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.SharedTests
{
    public sealed class ProcessHostPoolContinuationConsumeBoundaryPolicyTests
    {
        [Fact]
        public void IsExactRunningContinuationIndex_Should_Reject_Queued_Attempt()
        {
            var index = CreateIndex(
                status: AiRuntimeRunExecutionIndexStatuses.Queued,
                executionId: null,
                runtimeInstanceId: "runtime-1");

            Assert.False(
                ProcessHostPoolContinuationConsumeBoundaryPolicy
                    .IsExactRunningContinuationIndex(
                        index,
                        "execution-parent",
                        "runtime-1"));
        }

        [Fact]
        public void IsExactRunningContinuationIndex_Should_Reject_Wrong_Execution()
        {
            var index = CreateIndex(
                status: AiRuntimeRunExecutionIndexStatuses.Running,
                executionId: "execution-other",
                runtimeInstanceId: "runtime-1");

            Assert.False(
                ProcessHostPoolContinuationConsumeBoundaryPolicy
                    .IsExactRunningContinuationIndex(
                        index,
                        "execution-parent",
                        "runtime-1"));
        }

        [Fact]
        public void IsExactRunningContinuationIndex_Should_Reject_Wrong_Runtime()
        {
            var index = CreateIndex(
                status: AiRuntimeRunExecutionIndexStatuses.Running,
                executionId: "execution-parent",
                runtimeInstanceId: "runtime-2");

            Assert.False(
                ProcessHostPoolContinuationConsumeBoundaryPolicy
                    .IsExactRunningContinuationIndex(
                        index,
                        "execution-parent",
                        "runtime-1"));
        }

        [Fact]
        public void IsExactRunningContinuationIndex_Should_Accept_Exact_PostResume_Attempt()
        {
            var index = CreateIndex(
                status: AiRuntimeRunExecutionIndexStatuses.Running,
                executionId: "execution-parent",
                runtimeInstanceId: "runtime-1");

            Assert.True(
                ProcessHostPoolContinuationConsumeBoundaryPolicy
                    .IsExactRunningContinuationIndex(
                        index,
                        "execution-parent",
                        "runtime-1"));
        }

        [Fact]
        public void IsSemanticBoundaryPreserved_Should_Require_PostSchedule_Version()
        {
            Assert.False(
                ProcessHostPoolContinuationConsumeBoundaryPolicy
                    .IsSemanticBoundaryPreserved(
                        relationCompleted: true,
                        continuationScheduled: true,
                        parentTerminal: false,
                        scheduledStepVersion: 2,
                        callSiteVersion: 2,
                        callSiteStatus: AiStepExecutionStatus.Ready));

            Assert.True(
                ProcessHostPoolContinuationConsumeBoundaryPolicy
                    .IsSemanticBoundaryPreserved(
                        relationCompleted: true,
                        continuationScheduled: true,
                        parentTerminal: false,
                        scheduledStepVersion: 2,
                        callSiteVersion: 3,
                        callSiteStatus: AiStepExecutionStatus.Ready));
        }

        [Theory]
        [InlineData(AiStepExecutionStatus.Ready)]
        [InlineData(AiStepExecutionStatus.Running)]
        [InlineData(AiStepExecutionStatus.WaitingForRetry)]
        [InlineData(AiStepExecutionStatus.Completed)]
        [InlineData(AiStepExecutionStatus.Failed)]
        public void IsSemanticBoundaryPreserved_Should_Accept_Recoverable_Scheduled_Consume_States(
            AiStepExecutionStatus status)
        {
            Assert.True(
                ProcessHostPoolContinuationConsumeBoundaryPolicy
                    .IsSemanticBoundaryPreserved(
                        relationCompleted: true,
                        continuationScheduled: true,
                        parentTerminal: false,
                        scheduledStepVersion: 2,
                        callSiteVersion: 3,
                        callSiteStatus: status));
        }

        [Fact]
        public void IsSemanticBoundaryPreserved_Should_Reject_Terminal_CallSite_Without_PostSchedule_Progress()
        {
            Assert.False(
                ProcessHostPoolContinuationConsumeBoundaryPolicy
                    .IsSemanticBoundaryPreserved(
                        relationCompleted: true,
                        continuationScheduled: true,
                        parentTerminal: false,
                        scheduledStepVersion: 2,
                        callSiteVersion: 2,
                        callSiteStatus: AiStepExecutionStatus.Completed));
        }

        [Fact]
        public void IsSemanticBoundaryPreserved_Should_Reject_Terminal_CallSite_When_Continuation_Is_No_Longer_Scheduled()
        {
            Assert.False(
                ProcessHostPoolContinuationConsumeBoundaryPolicy
                    .IsSemanticBoundaryPreserved(
                        relationCompleted: true,
                        continuationScheduled: false,
                        parentTerminal: false,
                        scheduledStepVersion: 2,
                        callSiteVersion: 5,
                        callSiteStatus: AiStepExecutionStatus.Completed));
        }

        [Fact]
        public void IsSemanticBoundaryPreserved_Should_Reject_Terminal_Parent()
        {
            Assert.False(
                ProcessHostPoolContinuationConsumeBoundaryPolicy
                    .IsSemanticBoundaryPreserved(
                        relationCompleted: true,
                        continuationScheduled: true,
                        parentTerminal: true,
                        scheduledStepVersion: 2,
                        callSiteVersion: 3,
                        callSiteStatus: AiStepExecutionStatus.Completed));
        }

        private static AiRuntimeRunExecutionIndexEntry CreateIndex(
            string status,
            string? executionId,
            string runtimeInstanceId)
        {
            return new AiRuntimeRunExecutionIndexEntry
            {
                RunId = "local-run-1",
                ExecutionId = executionId,
                RuntimeInstanceId = runtimeInstanceId,
                Status = status,
                ExecutionContextSnapshot = new ExecutionContextSnapshot
                {
                    ContextKey = "context-1",
                    Project = "project-1",
                    UserId = "user-1",
                    TenantId = "tenant-1",
                    TenantGroupId = "tenant-group-1",
                    CurrentNamespace = "default",
                    Namespaces = new List<NamespaceEntry>()
                },
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = null
            };
        }
    }
}
