using Multiplexed.Abstractions.AI.ControlPlane.Execution;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Activity;
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
        private const string RequestedBy = "mcp-http-integration-test";
        private const string Source = "mcp-http-test";
        private const string TenantId = "test-tenant";
        private const string WorkerId = "mcp-http-worker";

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
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 20,
                    flakyStepInterval: 0);

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
                await DrainHttpRuntimeAsync(
                    maxDispatches: 1);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                    mcp,
                    pipelineName,
                    expectedCount: 1,
                    timeout: TimeSpan.FromSeconds(20));

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

        /// <summary>
        /// Verifies that four shared runs can be dispatched through the HTTP runtime provider.
        /// </summary>
        [Fact]
        public async Task Submit_Four_Runs_Then_Drain_Should_Dispatch_All_Through_HttpProvider()
        {
            var pipelineName =
                $"mcp-http-test-pipeline-{Guid.NewGuid():N}";

            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 20,
                    flakyStepInterval: 0);

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
                await ListAllSharedRunsAsync();

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
                await DrainHttpRuntimeAsync(
                    maxDispatches: 4);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                    mcp,
                    pipelineName,
                    expectedCount: 4,
                    timeout: TimeSpan.FromMinutes(1));

            Assert.Equal(
                4,
                dispatchedRuns.Count);

            Assert.All(
                dispatchedRuns,
                run =>
                {
                    Assert.Equal(
                        RuntimeInstanceHttpTestHost.RuntimeInstanceId,
                        run.AssignedRuntimeInstanceId);

                    Assert.False(
                        string.IsNullOrWhiteSpace(run.LocalRunId));
                });

            var afterDrain =
                await ListAllSharedRunsAsync();

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

            Assert.All(
                matchingRuns,
                run =>
                {
                    Assert.Equal(
                        RuntimeInstanceHttpTestHost.RuntimeInstanceId,
                        run.AssignedRuntimeInstanceId);

                    Assert.False(
                        string.IsNullOrWhiteSpace(run.LocalRunId));
                });

            output.WriteLine(
                $"HTTP provider dispatched four runs successfully. PipelineKey='{pipelineName}', RuntimeInstanceId='{RuntimeInstanceHttpTestHost.RuntimeInstanceId}'.");
        }

        /// <summary>
        /// Verifies that a dispatched HTTP runtime run eventually exposes a runtime run status.
        /// </summary>
        [Fact]
        public async Task Submit_One_Run_Then_Drain_Should_Eventually_Expose_Runtime_Run_Status()
        {
            var pipelineName =
                $"mcp-http-test-pipeline-{Guid.NewGuid():N}";

            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 20,
                    flakyStepInterval: 0);

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
                await DrainHttpRuntimeAsync(
                    maxDispatches: 1);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                    mcp,
                    pipelineName,
                    expectedCount: 1,
                    timeout: TimeSpan.FromSeconds(20));

            var run =
                dispatchedRuns.Single();

            Assert.Equal(
                RuntimeInstanceHttpTestHost.RuntimeInstanceId,
                run.AssignedRuntimeInstanceId);

            Assert.False(
                string.IsNullOrWhiteSpace(run.LocalRunId));

            var finalStatuses =
                await McpTestWaitHelpers.WaitForTerminalRuntimeRunStatusesAsync(
                    mcp,
                    dispatchedRuns,
                    timeout: TimeSpan.FromSeconds(20));

            var finalStatus =
                finalStatuses.Single();

            Assert.True(
                finalStatus.Success,
                finalStatus.FailureReason ?? finalStatus.Message);

            Assert.Equal(
                "completed",
                finalStatus.RunState?.Status);

            Assert.False(
                string.IsNullOrWhiteSpace(finalStatus.ExecutionId ?? finalStatus.RunState?.ExecutionId),
                $"RunId='{run.LocalRunId}' failed to expose an ExecutionId. Status='{finalStatus.RunState?.Status}', FailureReason='{finalStatus.FailureReason ?? finalStatus.RunState?.FailureReason}', Message='{finalStatus.Message}'.");

            output.WriteLine(
                $"HTTP runtime run completed. RuntimeInstanceId='{run.AssignedRuntimeInstanceId}', LocalRunId='{run.LocalRunId}', ExecutionId='{finalStatus.ExecutionId ?? finalStatus.RunState?.ExecutionId}'.");
        }

        /// <summary>
        /// Verifies that a larger pipeline can be dispatched through the HTTP provider and complete.
        /// </summary>
        [Fact]
        public async Task Submit_One_Run_With_100_Step_Pipeline_Then_Drain_Should_Complete_Through_HttpProvider()
        {
            var pipelineName =
                $"mcp-http-test-pipeline-{Guid.NewGuid():N}";

            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 100,
                    flakyStepInterval: 0);

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
                await DrainHttpRuntimeAsync(
                    maxDispatches: 1);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                    mcp,
                    pipelineName,
                    expectedCount: 1,
                    timeout: TimeSpan.FromMinutes(1));

            var finalStatuses =
                await McpTestWaitHelpers.WaitForTerminalRuntimeRunStatusesAsync(
                    mcp,
                    dispatchedRuns,
                    timeout: TimeSpan.FromMinutes(2));

            var finalStatus =
                finalStatuses.Single();

            Assert.True(
                finalStatus.Success,
                finalStatus.FailureReason ?? finalStatus.Message);

            Assert.Equal(
                "completed",
                finalStatus.RunState?.Status);

            Assert.False(
                string.IsNullOrWhiteSpace(finalStatus.ExecutionId ?? finalStatus.RunState?.ExecutionId));

            output.WriteLine(
                $"HTTP provider completed 100-step pipeline. PipelineKey='{pipelineName}', ExecutionId='{finalStatus.ExecutionId ?? finalStatus.RunState?.ExecutionId}'.");
        }

        /// <summary>
        /// Verifies that submitted HTTP-provider runs appear in shared queue activity.
        /// </summary>
        [Fact]
        public async Task Submit_Five_Runs_Should_Show_Shared_Queue_Activity_For_HttpProvider()
        {
            var pipelineName =
                $"mcp-http-test-pipeline-{Guid.NewGuid():N}";

            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 20,
                    flakyStepInterval: 0);

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                    submitRequest,
                    count: 5);

            Assert.Equal(
                5,
                submitResults.Count);

            Assert.All(
                submitResults,
                result => Assert.True(
                    result.Success,
                    result.FailureReason ?? result.Message));

            var queueItems =
                await mcp.ListSharedQueueAsync(
                    includeTerminal: true);

            var queueStatus =
                await mcp.GetSharedQueueStatusAsync(
                    includeTerminal: true);

            var activity =
                await mcp.GetSharedQueueActivityAsync(
                    new AiSharedQueueActivityRequest
                    {
                        PipelineKey = pipelineName,
                        TenantId = TenantId,
                        MaxResults = 20,
                        IncludeCompleted = true,
                        IncludeFailed = true,
                        IncludeCancelled = true
                    });

            var scenarioActivity =
                activity.Runs
                    .Where(run =>
                        string.Equals(
                            run.PipelineKey,
                            pipelineName,
                            StringComparison.Ordinal))
                    .ToArray();

            Assert.Equal(
                5,
                scenarioActivity.Length);

            Assert.NotNull(
                queueItems);

            Assert.NotNull(
                queueStatus);

            output.WriteLine(
                $"HTTP provider shared queue activity returned {scenarioActivity.Length} runs for PipelineKey='{pipelineName}'.");
        }

        /// <summary>
        /// Verifies that a run can be submitted, dispatched, and completed through the HTTP provider without explicitly draining the shared queue.
        /// </summary>
        [Fact]
        public async Task Submit_One_Run_Without_Manual_Drain_Should_Dispatch_And_Complete_Through_HttpProvider()
        {
            var pipelineName =
                $"mcp-http-test-pipeline-{Guid.NewGuid():N}";

            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 20,
                    flakyStepInterval: 0);

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                    submitRequest,
                    count: 1);

            Assert.Single(
                submitResults);

            Assert.True(
                submitResults[0].Success,
                submitResults[0].FailureReason ?? submitResults[0].Message);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                    mcp,
                    pipelineName,
                    expectedCount: 1,
                    timeout: TimeSpan.FromSeconds(20));

            var dispatchedRun =
                dispatchedRuns.Single();

            Assert.Equal(
                RuntimeInstanceHttpTestHost.RuntimeInstanceId,
                dispatchedRun.AssignedRuntimeInstanceId);

            Assert.False(
                string.IsNullOrWhiteSpace(dispatchedRun.LocalRunId));

            var finalStatuses =
                await McpTestWaitHelpers.WaitForTerminalRuntimeRunStatusesAsync(
                    mcp,
                    dispatchedRuns,
                    timeout: TimeSpan.FromMinutes(1));

            var finalStatus =
                finalStatuses.Single();

            Assert.True(
                finalStatus.Success,
                finalStatus.FailureReason ?? finalStatus.Message);

            Assert.Equal(
                "completed",
                finalStatus.RunState?.Status);

            Assert.False(
                string.IsNullOrWhiteSpace(finalStatus.ExecutionId ?? finalStatus.RunState?.ExecutionId));

            output.WriteLine(
                $"HTTP provider auto-dispatch completed. PipelineKey='{pipelineName}', RuntimeInstanceId='{dispatchedRun.AssignedRuntimeInstanceId}', LocalRunId='{dispatchedRun.LocalRunId}', ExecutionId='{finalStatus.ExecutionId ?? finalStatus.RunState?.ExecutionId}'.");
        }

        /// <summary>
        /// Verifies that several HTTP-provider runs can complete and expose distinct local run identifiers.
        /// </summary>
        [Fact]
        public async Task Submit_Three_Runs_Then_Wait_Should_Complete_All_Through_HttpProvider()
        {
            var pipelineName =
                $"mcp-http-test-pipeline-{Guid.NewGuid():N}";

            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 20,
                    flakyStepInterval: 0);

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                    submitRequest,
                    count: 3);

            Assert.Equal(
                3,
                submitResults.Count);

            Assert.All(
                submitResults,
                result => Assert.True(
                    result.Success,
                    result.FailureReason ?? result.Message));

            var drainResult =
                await DrainHttpRuntimeAsync(
                    maxDispatches: 3);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                    mcp,
                    pipelineName,
                    expectedCount: 3,
                    timeout: TimeSpan.FromMinutes(1));

            Assert.Equal(
                3,
                dispatchedRuns.Count);

            Assert.All(
                dispatchedRuns,
                run =>
                {
                    Assert.Equal(
                        RuntimeInstanceHttpTestHost.RuntimeInstanceId,
                        run.AssignedRuntimeInstanceId);

                    Assert.False(
                        string.IsNullOrWhiteSpace(run.LocalRunId));
                });

            Assert.Equal(
                3,
                dispatchedRuns
                    .Select(run => run.LocalRunId)
                    .Distinct(StringComparer.Ordinal)
                    .Count());

            var finalStatuses =
                await McpTestWaitHelpers.WaitForTerminalRuntimeRunStatusesAsync(
                    mcp,
                    dispatchedRuns,
                    timeout: TimeSpan.FromMinutes(2));

            Assert.Equal(
                3,
                finalStatuses.Count);

            Assert.All(
                finalStatuses,
                status =>
                {
                    Assert.True(
                        status.Success,
                        status.FailureReason ?? status.Message);

                    Assert.Equal(
                        "completed",
                        status.RunState?.Status);

                    Assert.False(
                        string.IsNullOrWhiteSpace(status.ExecutionId ?? status.RunState?.ExecutionId));
                });

            output.WriteLine(
                $"HTTP provider completed three runs successfully. PipelineKey='{pipelineName}', RuntimeInstanceId='{RuntimeInstanceHttpTestHost.RuntimeInstanceId}'.");
        }

        /// <summary>
        /// Verifies that a completed HTTP-provider run remains visible in shared run listing with the assigned runtime instance and local run id.
        /// </summary>
        [Fact]
        public async Task Submit_One_Run_Then_Complete_Should_Remain_Listed_With_Assigned_Http_Runtime()
        {
            var pipelineName =
                $"mcp-http-test-pipeline-{Guid.NewGuid():N}";

            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 20,
                    flakyStepInterval: 0);

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
                await DrainHttpRuntimeAsync(
                    maxDispatches: 1);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                    mcp,
                    pipelineName,
                    expectedCount: 1,
                    timeout: TimeSpan.FromSeconds(20));

            var dispatchedRun =
                dispatchedRuns.Single();

            var finalStatuses =
                await McpTestWaitHelpers.WaitForTerminalRuntimeRunStatusesAsync(
                    mcp,
                    dispatchedRuns,
                    timeout: TimeSpan.FromMinutes(1));

            var finalStatus =
                finalStatuses.Single();

            Assert.True(
                finalStatus.Success,
                finalStatus.FailureReason ?? finalStatus.Message);

            Assert.Equal(
                "completed",
                finalStatus.RunState?.Status);

            var listResult =
                await ListAllSharedRunsAsync();

            Assert.True(
                listResult.Success,
                listResult.FailureReason ?? listResult.Message);

            var listedRun =
                listResult.Runs.Single(run =>
                    string.Equals(
                        run.PipelineKey,
                        pipelineName,
                        StringComparison.Ordinal));

            Assert.Equal(
                RuntimeInstanceHttpTestHost.RuntimeInstanceId,
                listedRun.AssignedRuntimeInstanceId);

            Assert.Equal(
                dispatchedRun.LocalRunId,
                listedRun.LocalRunId);

            Assert.False(
                string.IsNullOrWhiteSpace(listedRun.LocalRunId));

            output.WriteLine(
                $"Completed HTTP-provider run remained listed. PipelineKey='{pipelineName}', RuntimeInstanceId='{listedRun.AssignedRuntimeInstanceId}', LocalRunId='{listedRun.LocalRunId}'.");
        }

        /// <summary>
        /// Verifies that shared queue activity still reports HTTP-provider runs after dispatch and completion.
        /// </summary>
        [Fact]
        public async Task Submit_Two_Runs_Then_Complete_Should_Show_Activity_For_HttpProvider()
        {
            var pipelineName =
                $"mcp-http-test-pipeline-{Guid.NewGuid():N}";

            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 20,
                    flakyStepInterval: 0);

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                    submitRequest,
                    count: 2);

            Assert.Equal(
                2,
                submitResults.Count);

            Assert.All(
                submitResults,
                result => Assert.True(
                    result.Success,
                    result.FailureReason ?? result.Message));

            var drainResult =
                await DrainHttpRuntimeAsync(
                    maxDispatches: 2);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                    mcp,
                    pipelineName,
                    expectedCount: 2,
                    timeout: TimeSpan.FromMinutes(1));

            var finalStatuses =
                await McpTestWaitHelpers.WaitForTerminalRuntimeRunStatusesAsync(
                    mcp,
                    dispatchedRuns,
                    timeout: TimeSpan.FromMinutes(2));

            Assert.Equal(
                2,
                finalStatuses.Count);

            Assert.All(
                finalStatuses,
                status =>
                {
                    Assert.True(
                        status.Success,
                        status.FailureReason ?? status.Message);

                    Assert.Equal(
                        "completed",
                        status.RunState?.Status);
                });

            var activity =
                await mcp.GetSharedQueueActivityAsync(
                    new AiSharedQueueActivityRequest
                    {
                        PipelineKey = pipelineName,
                        TenantId = TenantId,
                        MaxResults = 20,
                        IncludeCompleted = true,
                        IncludeFailed = true,
                        IncludeCancelled = true
                    });

            var matchingActivity =
                activity.Runs
                    .Where(run =>
                        string.Equals(
                            run.PipelineKey,
                            pipelineName,
                            StringComparison.Ordinal))
                    .ToArray();

            Assert.Equal(
                2,
                matchingActivity.Length);

            Assert.All(
                matchingActivity,
                run =>
                {
                    Assert.Equal(
                        RuntimeInstanceHttpTestHost.RuntimeInstanceId,
                        run.AssignedRuntimeInstanceId);

                    Assert.False(
                        string.IsNullOrWhiteSpace(run.LocalRunId));
                });

            output.WriteLine(
                $"HTTP provider activity validated after completion. PipelineKey='{pipelineName}', ActivityCount='{matchingActivity.Length}'.");
        }

        /// <summary>
        /// Verifies that a long-running HTTP-provider execution can be paused and resumed,
        /// then complete successfully.
        /// </summary>
        [Fact]
        public async Task Submit_Long_Running_Http_Execution_Then_Pause_And_Resume_Should_Complete()
        {
            var pipelineName =
                $"mcp-http-test-pipeline-{Guid.NewGuid():N}";

            var submitRequest =
                new AiSharedRuntimeControllerRequest
                {
                    Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                    PipelineKey = pipelineName,
                    TenantId = TenantId,
                    RequestedBy = RequestedBy,
                    Source = Source,
                    RunRequest = McpTestPipelineFactory.CreateRunRequest(
                        pipelineName,
                        stepCount: 100,
                        input: new
                        {
                            source = Source,
                            scenario = "http-execution-pause-resume",
                            delayMs = 100
                        },
                        flakyStepInterval: 0)
                };

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                    submitRequest,
                    count: 1);

            Assert.Single(submitResults);

            Assert.True(
                submitResults[0].Success,
                submitResults[0].FailureReason ?? submitResults[0].Message);

            var drainResult =
                await DrainHttpRuntimeAsync(
                    maxDispatches: 1);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                    mcp,
                    pipelineName,
                    expectedCount: 1,
                    timeout: TimeSpan.FromSeconds(30));

            var run =
                dispatchedRuns.Single();

            Assert.Equal(
                RuntimeInstanceHttpTestHost.RuntimeInstanceId,
                run.AssignedRuntimeInstanceId);

            var runningStatus =
                await McpTestWaitHelpers.WaitForRuntimeRunExecutionIdAsync(
                    mcp,
                    run,
                    timeout: TimeSpan.FromMinutes(1));

            var executionId =
                runningStatus.ExecutionId ??
                runningStatus.RunState?.ExecutionId;

            Assert.False(
                string.IsNullOrWhiteSpace(executionId));

            var pauseResult =
                await mcp.PauseExecutionAsync(
                    new AiExecutionControlPlaneRequest
                    {
                        Operation = AiExecutionControlPlaneOperation.Pause,
                        ExecutionId = executionId!,
                        Reason = "HTTP provider integration test execution pause.",
                        RequestedBy = RequestedBy,
                        Source = Source
                    });

            Assert.True(
                pauseResult.Success,
                pauseResult.FailureReason ?? pauseResult.Message);

            var pausedStatus =
                await McpTestWaitHelpers.WaitForExecutionControlStatusAsync(
                    mcp,
                    executionId!,
                    timeout: TimeSpan.FromSeconds(15),
                    expectedStatuses:
                    [
                        "Paused"
                    ]);

            Assert.True(
                pausedStatus.Success,
                pausedStatus.FailureReason ?? pausedStatus.Message);

            var resumeResult =
                await mcp.ResumeExecutionAsync(
                    new AiExecutionControlPlaneRequest
                    {
                        Operation = AiExecutionControlPlaneOperation.Resume,
                        ExecutionId = executionId!,
                        Reason = "HTTP provider integration test execution resume.",
                        RequestedBy = RequestedBy,
                        Source = Source
                    });

            Assert.True(
                resumeResult.Success,
                resumeResult.FailureReason ?? resumeResult.Message);

            var resumedStatus =
                await McpTestWaitHelpers.WaitForExecutionControlStatusAsync(
                    mcp,
                    executionId!,
                    timeout: TimeSpan.FromSeconds(15),
                    expectedStatuses:
                    [
                        "Running",
                "None",
                "Completed"
                    ]);

            Assert.True(
                resumedStatus.Success,
                resumedStatus.FailureReason ?? resumedStatus.Message);

            var finalStatuses =
                await McpTestWaitHelpers.WaitForTerminalRuntimeRunStatusesAsync(
                    mcp,
                    dispatchedRuns,
                    timeout: TimeSpan.FromMinutes(2));

            var finalStatus =
                finalStatuses.Single();

            Assert.True(
                finalStatus.Success,
                finalStatus.FailureReason ?? finalStatus.Message);

            Assert.Equal(
                "completed",
                finalStatus.RunState?.Status);

            Assert.False(
                string.IsNullOrWhiteSpace(finalStatus.ExecutionId ?? finalStatus.RunState?.ExecutionId));

            output.WriteLine(
                $"HTTP execution pause/resume completed. RuntimeInstanceId='{run.AssignedRuntimeInstanceId}', LocalRunId='{run.LocalRunId}', ExecutionId='{executionId}'.");
        }

        /// <summary>
        /// Verifies that a queued HTTP-provider run can be cancelled before it creates an execution.
        /// </summary>
        [Fact]
        public async Task Submit_Run_Then_Cancel_Queued_Http_Run_Should_Not_Create_Execution()
        {
            var pipelineName =
                $"mcp-http-test-pipeline-{Guid.NewGuid():N}";

            var pauseQueueResult =
                await mcp.PauseRuntimeQueueAsync(
                    new AiRuntimeQueueControlPlaneRequest
                    {
                        Operation = AiRuntimeQueueControlPlaneOperation.PauseQueue,
                        RuntimeInstanceId = RuntimeInstanceHttpTestHost.RuntimeInstanceId,
                        Reason = "HTTP provider integration test queued-run cancel setup.",
                        RequestedBy = RequestedBy,
                        Source = Source
                    });

            Assert.True(
                pauseQueueResult.Success,
                pauseQueueResult.FailureReason ?? pauseQueueResult.Message);

            try
            {
                var submitRequest =
                    CreateSubmitRequest(
                        pipelineName,
                        stepCount: 50,
                        flakyStepInterval: 0);

                var submitResults =
                    await mcp.SubmitManyRunsAsync(
                        submitRequest,
                        count: 1);

                Assert.Single(submitResults);

                Assert.True(
                    submitResults[0].Success,
                    submitResults[0].FailureReason ?? submitResults[0].Message);

                var drainResult =
                    await DrainHttpRuntimeAsync(
                        maxDispatches: 1);

                Assert.True(
                    drainResult.Success,
                    drainResult.FailureReason);

                var dispatchedRuns =
                    await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                        mcp,
                        pipelineName,
                        expectedCount: 1,
                        timeout: TimeSpan.FromSeconds(30));

                var run =
                    dispatchedRuns.Single();

                Assert.Equal(
                    RuntimeInstanceHttpTestHost.RuntimeInstanceId,
                    run.AssignedRuntimeInstanceId);

                Assert.False(
                    string.IsNullOrWhiteSpace(run.LocalRunId));

                var statusBeforeCancel =
                    await mcp.GetRuntimeQueueRunStatusAsync(
                        new AiRuntimeQueueControlPlaneRequest
                        {
                            Operation = AiRuntimeQueueControlPlaneOperation.GetRunStatus,
                            RuntimeInstanceId = RuntimeInstanceHttpTestHost.RuntimeInstanceId,
                            RunId = run.LocalRunId,
                            RequestedBy = RequestedBy,
                            Source = Source
                        });

                Assert.True(
                    statusBeforeCancel.Success,
                    statusBeforeCancel.FailureReason ?? statusBeforeCancel.Message);

                Assert.Equal(
                    "queued",
                    statusBeforeCancel.RunState?.Status);

                Assert.True(
                    string.IsNullOrWhiteSpace(statusBeforeCancel.ExecutionId ?? statusBeforeCancel.RunState?.ExecutionId));

                var cancelResult =
                    await mcp.CancelRuntimeQueueRunAsync(
                        new AiRuntimeQueueControlPlaneRequest
                        {
                            Operation = AiRuntimeQueueControlPlaneOperation.CancelRun,
                            RuntimeInstanceId = RuntimeInstanceHttpTestHost.RuntimeInstanceId,
                            RunId = run.LocalRunId,
                            Reason = "HTTP provider integration test queued-run cancel.",
                            RequestedBy = RequestedBy,
                            Source = Source
                        });

                Assert.True(
                    cancelResult.Success,
                    cancelResult.FailureReason ?? cancelResult.Message);

                var statusAfterCancel =
                    await McpTestWaitHelpers.WaitForRuntimeRunStatusAsync(
                        mcp,
                        run,
                        expectedStatus: "cancelled",
                        timeout: TimeSpan.FromSeconds(15));

                Assert.True(
                    statusAfterCancel.Success,
                    statusAfterCancel.FailureReason ?? statusAfterCancel.Message);

                Assert.True(
                    string.IsNullOrWhiteSpace(statusAfterCancel.ExecutionId ?? statusAfterCancel.RunState?.ExecutionId));

                output.WriteLine(
                    $"HTTP queued run cancelled before execution creation. RuntimeInstanceId='{run.AssignedRuntimeInstanceId}', LocalRunId='{run.LocalRunId}'.");
            }
            finally
            {
                var resumeQueueResult =
                    await mcp.ResumeRuntimeQueueAsync(
                        new AiRuntimeQueueControlPlaneRequest
                        {
                            Operation = AiRuntimeQueueControlPlaneOperation.ResumeQueue,
                            RuntimeInstanceId = RuntimeInstanceHttpTestHost.RuntimeInstanceId,
                            Reason = "HTTP provider integration test queued-run cancel cleanup.",
                            RequestedBy = RequestedBy,
                            Source = Source
                        });

                Assert.True(
                    resumeQueueResult.Success,
                    resumeQueueResult.FailureReason ?? resumeQueueResult.Message);
            }
        }

        /// <summary>
        /// Verifies that a long-running HTTP-provider execution can receive a cancellation request.
        /// </summary>
        [Fact]
        public async Task Submit_Long_Running_Http_Execution_Then_Cancel_Should_Request_Cancellation()
        {
            var pipelineName =
                $"mcp-http-test-pipeline-{Guid.NewGuid():N}";

            var submitRequest =
                new AiSharedRuntimeControllerRequest
                {
                    Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                    PipelineKey = pipelineName,
                    TenantId = TenantId,
                    RequestedBy = RequestedBy,
                    Source = Source,
                    RunRequest = McpTestPipelineFactory.CreateRunRequest(
                        pipelineName,
                        stepCount: 100,
                        input: new
                        {
                            source = Source,
                            scenario = "http-execution-cancel-request",
                            delayMs = 100
                        },
                        flakyStepInterval: 0)
                };

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                    submitRequest,
                    count: 1);

            Assert.Single(submitResults);

            Assert.True(
                submitResults[0].Success,
                submitResults[0].FailureReason ?? submitResults[0].Message);

            var drainResult =
                await DrainHttpRuntimeAsync(
                    maxDispatches: 1);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                    mcp,
                    pipelineName,
                    expectedCount: 1,
                    timeout: TimeSpan.FromSeconds(30));

            var run =
                dispatchedRuns.Single();

            Assert.Equal(
                RuntimeInstanceHttpTestHost.RuntimeInstanceId,
                run.AssignedRuntimeInstanceId);

            var runningStatus =
                await McpTestWaitHelpers.WaitForRuntimeRunExecutionIdAsync(
                    mcp,
                    run,
                    timeout: TimeSpan.FromMinutes(1));

            var executionId =
                runningStatus.ExecutionId ??
                runningStatus.RunState?.ExecutionId;

            Assert.False(
                string.IsNullOrWhiteSpace(executionId));

            var cancelResult =
                await mcp.CancelExecutionAsync(
                    new AiExecutionControlPlaneRequest
                    {
                        Operation = AiExecutionControlPlaneOperation.Cancel,
                        ExecutionId = executionId!,
                        Reason = "HTTP provider integration test execution cancellation request.",
                        RequestedBy = RequestedBy,
                        Source = Source
                    });

            Assert.True(
                cancelResult.Success,
                cancelResult.FailureReason ?? cancelResult.Message);

            var cancellingStatus =
                await McpTestWaitHelpers.WaitForExecutionControlStatusAsync(
                    mcp,
                    executionId!,
                    timeout: TimeSpan.FromSeconds(15),
                    expectedStatuses:
                    [
                        "Cancelling",
                        "Cancelled",
                        "Completed"
                    ]);

            Assert.True(
                cancellingStatus.Success,
                cancellingStatus.FailureReason ?? cancellingStatus.Message);

            output.WriteLine(
                $"HTTP execution cancellation requested. RuntimeInstanceId='{run.AssignedRuntimeInstanceId}', LocalRunId='{run.LocalRunId}', ExecutionId='{executionId}', ControlStatus='{cancellingStatus.State?.Status}'.");
        }

        private static AiSharedRuntimeControllerRequest CreateSubmitRequest(
            string pipelineName,
            int stepCount,
            int flakyStepInterval)
        {
            return new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                PipelineKey = pipelineName,
                TenantId = TenantId,
                RequestedBy = RequestedBy,
                Source = Source,
                RunRequest = McpTestPipelineFactory.CreateRunRequest(
                    pipelineName,
                    stepCount: stepCount,
                    flakyStepInterval: flakyStepInterval)
            };
        }

        private async Task<AiSharedQueuePumpResult> DrainHttpRuntimeAsync(
            int maxDispatches)
        {
            return await mcp.DrainQueueAsync(
                new AiSharedQueuePumpRequest
                {
                    RuntimeInstanceId = RuntimeInstanceHttpTestHost.RuntimeInstanceId,
                    WorkerId = WorkerId,
                    MaxDispatches = maxDispatches,
                    RequestedBy = RequestedBy,
                    Source = Source
                });
        }

        private async Task<AiSharedRuntimeControllerResult> ListAllSharedRunsAsync()
        {
            return await mcp.ListSharedRunsAsync(
                new AiSharedRuntimeControllerRequest
                {
                    Operation = AiSharedRuntimeControllerOperation.ListRuns,
                    IncludeCompleted = true,
                    IncludeFailed = true,
                    IncludeCancelled = true,
                    RequestedBy = RequestedBy,
                    Source = Source
                });
        }
    }
}