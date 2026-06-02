using Microsoft.VisualStudio.TestPlatform.Utilities;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios
{
    /// <summary>
    /// Contains end-to-end shared run MCP scenarios.
    /// </summary>
    [Collection(McpCollection.Name)]
    public sealed class SharedRunScenarioTests
    {
        private readonly McpTestClient mcp;
        private readonly ITestOutputHelper output;

        public SharedRunScenarioTests(
            McpServerFixture fixture,
            ITestOutputHelper output)
        {
            mcp = fixture.Mcp;
            this.output = output;
        }

        /// <summary>
        /// Verifies that a shared run with a 50-step pipeline can be submitted through MCP and listed.
        /// </summary>
        [Fact]
        public async Task Submit_Run_With_50_Step_Pipeline_Then_List_Shared_Runs_Should_Return_Submitted_Run()
        {
            var requestedSharedRunId =
                $"mcp-test-run-{Guid.NewGuid():N}";

            var pipelineName =
                $"mcp-test-pipeline-{Guid.NewGuid():N}";

            var submitRequest = new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = requestedSharedRunId,
                PipelineKey = pipelineName,
                TenantId = "test-tenant",
                CorrelationId = $"mcp-test-correlation-{Guid.NewGuid():N}",
                RequestedBy = "mcp-integration-test",
                Source = "mcp-test",
                RunRequest = McpTestPipelineFactory.CreateRunRequest(
                    pipelineName: pipelineName,
                    stepCount: 50,
                    input: new
                    {
                        source = "mcp-integration-test",
                        scenario = "submit-run-then-list",
                        stepCount = 50
                    },
                    enableRetention: false,
                    flakyStepInterval: 9)
            };

            var submitResult = await mcp.SubmitRunAsync(
                submitRequest);

            Assert.True(
                submitResult.Success,
                submitResult.FailureReason ?? submitResult.Message);

            Assert.Equal(
                requestedSharedRunId,
                submitResult.SharedRunId);

            var listRequest = new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.ListRuns,
                RequestedBy = "mcp-integration-test",
                Source = "mcp-test"
            };

            var listResult = await mcp.ListSharedRunsAsync(
                listRequest);

            Assert.True(
                listResult.Success,
                listResult.FailureReason ?? listResult.Message);

            Assert.NotNull(
                listResult.Runs);

            Assert.Contains(
                listResult.Runs,
                run => run.SharedRunId == requestedSharedRunId);

            McpScenarioOutput.WriteSharedRunSummary(
                output,
                nameof(Submit_Run_With_50_Step_Pipeline_Then_List_Shared_Runs_Should_Return_Submitted_Run),
                pipelineName,
                requestedSharedRunId,
                submitResult,
                listResult);
        }

        /// <summary>
        /// Verifies that submitted shared runs are placed into the shared queue
        /// and that the drain operation dispatches available runs.
        /// </summary>
        [Fact]
        public async Task Submit_Four_Runs_Then_Drain_Should_Dispatch_Available_Runs()
        {
            var pipelineName =
                $"mcp-test-pipeline-{Guid.NewGuid():N}";

            var submitRequest = new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                PipelineKey = pipelineName,
                TenantId = "test-tenant",
                RequestedBy = "mcp-integration-test",
                Source = "mcp-test",
                RunRequest = McpTestPipelineFactory.CreateRunRequest(
                    pipelineName,
                    stepCount: 20,
                    flakyStepInterval: 5)
            };

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                    submitRequest,
                    count: 4);

            Assert.Equal(
                4,
                submitResults.Count);

            Assert.All(
                submitResults,
                result => Assert.True(
                    result.Success,
                    result.FailureReason ?? result.Message));

            var beforeDrain =
                await mcp.ListSharedRunsAsync(
                    new AiSharedRuntimeControllerRequest
                    {
                        Operation = AiSharedRuntimeControllerOperation.ListRuns,
                        RequestedBy = "mcp-integration-test",
                        Source = "mcp-test"
                    });

            Assert.True(
                beforeDrain.Success,
                beforeDrain.FailureReason ?? beforeDrain.Message);

            Assert.Equal(
                4,
                beforeDrain.Runs.Count(run =>
                    string.Equals(
                        run.PipelineKey,
                        pipelineName,
                        StringComparison.Ordinal)));

            var drainResult =
                await mcp.DrainQueueAsync(
                    new AiSharedQueuePumpRequest
                    {
                        RuntimeInstanceId = "mcp-instance",
                        WorkerId = "mcp-worker"
                    });

            Assert.NotNull(
                drainResult);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            Assert.True(
                drainResult.SuccessfulDispatchCount > 0,
                $"Expected at least one successful dispatch. " +
                $"Attempted={drainResult.AttemptedDispatchCount}, " +
                $"Failed={drainResult.FailedDispatchCount}");

            var afterDrain =
                await mcp.ListSharedRunsAsync(
                    new AiSharedRuntimeControllerRequest
                    {
                        Operation = AiSharedRuntimeControllerOperation.ListRuns,
                        RequestedBy = "mcp-integration-test",
                        Source = "mcp-test"
                    });

            Assert.True(
                afterDrain.Success,
                afterDrain.FailureReason ?? afterDrain.Message);

            var matchingRuns =
                afterDrain.Runs
                    .Where(run =>
                        string.Equals(
                            run.PipelineKey,
                            pipelineName,
                            StringComparison.Ordinal))
                    .ToArray();

            Assert.Equal(
                4,
                matchingRuns.Length);

            Assert.Contains(
                matchingRuns,
                run =>
                    !string.IsNullOrWhiteSpace(run.AssignedRuntimeInstanceId) ||
                    !string.IsNullOrWhiteSpace(run.LocalRunId) ||
                    !string.IsNullOrWhiteSpace(run.ExecutionId));

            Assert.True(
                matchingRuns.Count(run =>
                    !string.IsNullOrWhiteSpace(run.LocalRunId) ||
                    !string.IsNullOrWhiteSpace(run.ExecutionId) ||
                    !string.IsNullOrWhiteSpace(run.AssignedRuntimeInstanceId)) >=
                drainResult.SuccessfulDispatchCount);

            McpScenarioOutput.WriteDrainSummary(
                output,
                nameof(Submit_Four_Runs_Then_Drain_Should_Dispatch_Available_Runs),
                pipelineName,
                submitResults,
                beforeDrain,
                drainResult,
                afterDrain);
        }

        [Fact]
        public async Task Submit_Four_Runs_Then_Drain_Should_Eventually_Expose_Runtime_Run_Status()
        {
            var pipelineName =
                $"mcp-test-pipeline-{Guid.NewGuid():N}";

            var submitRequest = new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                PipelineKey = pipelineName,
                TenantId = "test-tenant",
                RequestedBy = "mcp-integration-test",
                Source = "mcp-test",
                RunRequest = McpTestPipelineFactory.CreateRunRequest(
                    pipelineName,
                    stepCount: 20,
                    flakyStepInterval: 5)
            };

            var submitResults = await mcp.SubmitManyRunsAsync(
                submitRequest,
                count: 4);

            Assert.Equal(4, submitResults.Count);

            var drainResult = await mcp.DrainQueueAsync(
                new AiSharedQueuePumpRequest
                {
                    RuntimeInstanceId = "mcp-instance",
                    WorkerId = "mcp-worker",
                    MaxDispatches = 4,
                    RequestedBy = "mcp-integration-test",
                    Source = "mcp-test"
                });

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns = await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                mcp,
                pipelineName,
                expectedCount: 4,
                timeout: TimeSpan.FromSeconds(20));

            foreach (var run in dispatchedRuns)
            {
                Assert.False(string.IsNullOrWhiteSpace(run.LocalRunId));
                Assert.False(string.IsNullOrWhiteSpace(run.AssignedRuntimeInstanceId));
            }

            var finalStatuses = new List<AiRuntimeQueueControlPlaneResult>();

            foreach (var run in dispatchedRuns)
            {
                var status = await mcp.GetRuntimeQueueRunStatusAsync(
                    new AiRuntimeQueueControlPlaneRequest
                    {
                        Operation = AiRuntimeQueueControlPlaneOperation.GetRunStatus,
                        RuntimeInstanceId = run.AssignedRuntimeInstanceId,
                        RunId = run.LocalRunId,
                        RequestedBy = "mcp-integration-test",
                        Source = "mcp-test"
                    });

                finalStatuses.Add(status);
            }

            Assert.All(
                finalStatuses,
                result => Assert.True(
                    result.Success,
                    result.FailureReason ?? result.Message));

            McpScenarioOutput.WriteRuntimeRunStatusSummary(
                output,
                nameof(Submit_Four_Runs_Then_Drain_Should_Eventually_Expose_Runtime_Run_Status),
                pipelineName,
                dispatchedRuns,
                finalStatuses);
        }
    }
}