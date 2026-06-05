using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Http;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios
{
    /// <summary>
    /// Contains MCP scenarios that validate dispatch through the HTTP runtime instance provider.
    /// </summary>
    [Collection(McpHttpRuntimeCollection.Name)]
    public sealed class HttpRuntimeProviderScenarioTests
    {
        private readonly McpTestClient mcp;
        private readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpRuntimeProviderScenarioTests"/> class.
        /// </summary>
        /// <param name="fixture">The HTTP runtime fixture.</param>
        /// <param name="output">The test output helper.</param>
        public HttpRuntimeProviderScenarioTests(
            McpHttpRuntimeFixture fixture,
            ITestOutputHelper output)
        {
            ArgumentNullException.ThrowIfNull(fixture);

            mcp =
                fixture.Mcp;

            this.output =
                output;
        }

        /// <summary>
        /// Verifies that one shared run can be submitted through MCP and dispatched to an HTTP runtime instance.
        /// </summary>
        [Fact]
        public async Task Submit_One_Run_Then_Drain_Should_Dispatch_Through_HttpProvider()
        {
            var pipelineName =
                $"mcp-http-test-pipeline-{Guid.NewGuid():N}";

            var submitRequest =
                new AiSharedRuntimeControllerRequest
                {
                    Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                    PipelineKey = pipelineName,
                    TenantId = "test-tenant",
                    RequestedBy = "mcp-http-integration-test",
                    Source = "mcp-http-test",
                    RunRequest = McpTestPipelineFactory.CreateRunRequest(
                        pipelineName,
                        stepCount: 20,
                        flakyStepInterval: 0)
                };

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                    submitRequest,
                    count: 1);

            Assert.Single(
                submitResults);

            Assert.True(
                submitResults[0].Success,
                submitResults[0].FailureReason ?? submitResults[0].Message);

            var drainResult =
                await mcp.DrainQueueAsync(
                    new AiSharedQueuePumpRequest
                    {
                        RuntimeInstanceId = RuntimeInstanceHttpTestHost.RuntimeInstanceId,
                        WorkerId = "mcp-http-worker",
                        MaxDispatches = 1,
                        RequestedBy = "mcp-http-integration-test",
                        Source = "mcp-http-test"
                    });

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                    mcp,
                    pipelineName,
                    expectedCount: 1,
                    timeout: TimeSpan.FromMinutes(1));

            var dispatchedRun =
                dispatchedRuns.Single();

            Assert.Equal(
                RuntimeInstanceHttpTestHost.RuntimeInstanceId,
                dispatchedRun.AssignedRuntimeInstanceId);

            Assert.False(
                string.IsNullOrWhiteSpace(dispatchedRun.LocalRunId));

            output.WriteLine(
                $"HTTP provider dispatch succeeded. RuntimeInstanceId='{dispatchedRun.AssignedRuntimeInstanceId}', LocalRunId='{dispatchedRun.LocalRunId}'.");
        }
    }
}