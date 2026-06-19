using Multiplexed.Abstractions.AI.ControlPlane.Execution;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Activity;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Http
{
    /// <summary>
    /// Contains MCP scenarios that validate dispatch through the HTTP runtime instance provider.
    /// </summary>
    /// <remarks>
    /// This test class validates the current HTTP runtime provider model:
    ///
    /// MCP control plane
    /// -> HTTP runtime provider
    /// -> RuntimeInstanceOnly HTTP host
    /// -> internal runtime instance pool
    /// -> runtime-http-1 / runtime-http-2 / runtime-http-3.
    ///
    /// The old single-runtime HTTP fixture is intentionally not used here anymore.
    /// All hosts are created through the generic MCP/runtime fixture stack so that the
    /// same logical control-plane identifier is applied consistently to the MCP host,
    /// runtime-instance host, Redis registry, Redis capacity store, shared queue, and
    /// shared run store.
    /// </remarks>
    public sealed class HttpRuntimeProviderScenarioTests
    {
        private const string RequestedBy = "mcp-http-integration-test";
        private const string Source = "mcp-http-test";
        private const string TenantId = "test-tenant";
        private const string WorkerId = "mcp-http-worker";
        private const string PumpRuntimeInstanceId = "mcp-http-pump";
        private const string RuntimeInstancePrefix = "runtime-http-";
        private const string RuntimeInstanceHostId = "runtime-http-host";
        private const int RuntimeInstanceHostPort = 5002;

        private readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpRuntimeProviderScenarioTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public HttpRuntimeProviderScenarioTests(
            ITestOutputHelper output)
        {
            this.output =
                output;
        }

        /// <summary>
        /// Verifies that one shared run can be submitted through MCP and dispatched
        /// through the HTTP provider to one of the HTTP runtime pool instances.
        /// </summary>
        [Fact]
        public async Task Submit_One_Run_Then_Drain_Should_Dispatch_Through_HttpProvider()
        {
            await using var fixture =
                await CreateHttpRuntimePoolFixtureAsync()
                    .ConfigureAwait(false);

            var mcp =
                fixture.Mcp;

            await LogRuntimeInstancesAsync(mcp)
                .ConfigureAwait(false);

            var pipelineName =
                CreatePipelineName();

            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 20,
                    flakyStepInterval: 0);

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                        submitRequest,
                        count: 1)
                    .ConfigureAwait(false);

            Assert.Single(
                submitResults);

            Assert.True(
                submitResults[0].Success,
                submitResults[0].FailureReason ?? submitResults[0].Message);

            var drainResult =
                await DrainHttpRuntimePoolAsync(
                        mcp,
                        maxDispatches: 1)
                    .ConfigureAwait(false);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                        mcp,
                        pipelineName,
                        expectedCount: 1,
                        timeout: TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false);

            var dispatchedRun =
                dispatchedRuns.Single();

            AssertAssignedToHttpRuntimePool(
                dispatchedRun);

            output.WriteLine(
                $"HTTP provider dispatch succeeded. RuntimeInstanceId='{dispatchedRun.AssignedRuntimeInstanceId}', LocalRunId='{dispatchedRun.LocalRunId}'.");
        }

        /// <summary>
        /// Verifies that four shared runs can be dispatched through the HTTP provider
        /// across the RuntimeInstanceOnly HTTP pool.
        /// </summary>
        [Fact]
        public async Task Submit_Four_Runs_Then_Drain_Should_Dispatch_All_Through_HttpProvider()
        {
            await using var fixture =
                await CreateHttpRuntimePoolFixtureAsync()
                    .ConfigureAwait(false);

            var mcp =
                fixture.Mcp;

            await LogRuntimeInstancesAsync(mcp)
                .ConfigureAwait(false);

            var pipelineName =
                CreatePipelineName();

            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 20,
                    flakyStepInterval: 0);

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                        submitRequest,
                        count: 4)
                    .ConfigureAwait(false);

            Assert.Equal(
                4,
                submitResults.Count);

            Assert.All(
                submitResults,
                result => Assert.True(
                    result.Success,
                    result.FailureReason ?? result.Message));

            var beforeDrain =
                await ListAllSharedRunsAsync(mcp)
                    .ConfigureAwait(false);

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
                await DrainHttpRuntimePoolAsync(
                        mcp,
                        maxDispatches: 4)
                    .ConfigureAwait(false);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                        mcp,
                        pipelineName,
                        expectedCount: 4,
                        timeout: TimeSpan.FromMinutes(1))
                    .ConfigureAwait(false);

            Assert.Equal(
                4,
                dispatchedRuns.Count);

            Assert.All(
                dispatchedRuns,
                AssertAssignedToHttpRuntimePool);

            var afterDrain =
                await ListAllSharedRunsAsync(mcp)
                    .ConfigureAwait(false);

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
                AssertAssignedToHttpRuntimePool);

            output.WriteLine(
                $"HTTP provider dispatched four runs successfully. PipelineKey='{pipelineName}'.");
        }

        /// <summary>
        /// Verifies that a dispatched HTTP runtime run eventually exposes a runtime run status.
        /// </summary>
        [Fact]
        public async Task Submit_One_Run_Then_Drain_Should_Eventually_Expose_Runtime_Run_Status()
        {
            await using var fixture =
                await CreateHttpRuntimePoolFixtureAsync()
                    .ConfigureAwait(false);

            var mcp =
                fixture.Mcp;

            var pipelineName =
                CreatePipelineName();

            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 20,
                    flakyStepInterval: 0);

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                        submitRequest,
                        count: 1)
                    .ConfigureAwait(false);

            Assert.Single(
                submitResults);

            Assert.True(
                submitResults[0].Success,
                submitResults[0].FailureReason ?? submitResults[0].Message);

            var drainResult =
                await DrainHttpRuntimePoolAsync(
                        mcp,
                        maxDispatches: 1)
                    .ConfigureAwait(false);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                        mcp,
                        pipelineName,
                        expectedCount: 1,
                        timeout: TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false);

            var run =
                dispatchedRuns.Single();

            AssertAssignedToHttpRuntimePool(
                run);

            var finalStatuses =
                await McpTestWaitHelpers.WaitForTerminalRuntimeRunStatusesAsync(
                        mcp,
                        dispatchedRuns,
                        timeout: TimeSpan.FromMinutes(1))
                    .ConfigureAwait(false);

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
            await using var fixture =
                await CreateHttpRuntimePoolFixtureAsync()
                    .ConfigureAwait(false);

            var mcp =
                fixture.Mcp;

            var pipelineName =
                CreatePipelineName();

            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 100,
                    flakyStepInterval: 0);

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                        submitRequest,
                        count: 1)
                    .ConfigureAwait(false);

            Assert.Single(
                submitResults);

            Assert.True(
                submitResults[0].Success,
                submitResults[0].FailureReason ?? submitResults[0].Message);

            var drainResult =
                await DrainHttpRuntimePoolAsync(
                        mcp,
                        maxDispatches: 1)
                    .ConfigureAwait(false);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                        mcp,
                        pipelineName,
                        expectedCount: 1,
                        timeout: TimeSpan.FromMinutes(1))
                    .ConfigureAwait(false);

            Assert.Single(
                dispatchedRuns);

            AssertAssignedToHttpRuntimePool(
                dispatchedRuns.Single());

            var finalStatuses =
                await McpTestWaitHelpers.WaitForTerminalRuntimeRunStatusesAsync(
                        mcp,
                        dispatchedRuns,
                        timeout: TimeSpan.FromMinutes(2))
                    .ConfigureAwait(false);

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
            await using var fixture =
                await CreateHttpRuntimePoolFixtureAsync()
                    .ConfigureAwait(false);

            var mcp =
                fixture.Mcp;

            var pipelineName =
                CreatePipelineName();

            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 20,
                    flakyStepInterval: 0);

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                        submitRequest,
                        count: 5)
                    .ConfigureAwait(false);

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
                        includeTerminal: true)
                    .ConfigureAwait(false);

            var queueStatus =
                await mcp.GetSharedQueueStatusAsync(
                        includeTerminal: true)
                    .ConfigureAwait(false);

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
                        })
                    .ConfigureAwait(false);

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
            await using var fixture =
                await CreateHttpRuntimePoolFixtureAsync()
                    .ConfigureAwait(false);

            var mcp =
                fixture.Mcp;

            var pipelineName =
                CreatePipelineName();

            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 20,
                    flakyStepInterval: 0);

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                        submitRequest,
                        count: 1)
                    .ConfigureAwait(false);

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
                        timeout: TimeSpan.FromMinutes(1))
                    .ConfigureAwait(false);

            var dispatchedRun =
                dispatchedRuns.Single();

            AssertAssignedToHttpRuntimePool(
                dispatchedRun);

            var finalStatuses =
                await McpTestWaitHelpers.WaitForTerminalRuntimeRunStatusesAsync(
                        mcp,
                        dispatchedRuns,
                        timeout: TimeSpan.FromMinutes(1))
                    .ConfigureAwait(false);

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
            await using var fixture =
                await CreateHttpRuntimePoolFixtureAsync()
                    .ConfigureAwait(false);

            var mcp =
                fixture.Mcp;

            var pipelineName =
                CreatePipelineName();

            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 20,
                    flakyStepInterval: 0);

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                        submitRequest,
                        count: 3)
                    .ConfigureAwait(false);

            Assert.Equal(
                3,
                submitResults.Count);

            Assert.All(
                submitResults,
                result => Assert.True(
                    result.Success,
                    result.FailureReason ?? result.Message));

            var drainResult =
                await DrainHttpRuntimePoolAsync(
                        mcp,
                        maxDispatches: 3)
                    .ConfigureAwait(false);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                        mcp,
                        pipelineName,
                        expectedCount: 3,
                        timeout: TimeSpan.FromMinutes(1))
                    .ConfigureAwait(false);

            Assert.Equal(
                3,
                dispatchedRuns.Count);

            Assert.All(
                dispatchedRuns,
                AssertAssignedToHttpRuntimePool);

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
                        timeout: TimeSpan.FromMinutes(2))
                    .ConfigureAwait(false);

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
                $"HTTP provider completed three runs successfully. PipelineKey='{pipelineName}'.");
        }

        /// <summary>
        /// Verifies that a completed HTTP-provider run remains visible in shared run listing with the assigned runtime instance and local run id.
        /// </summary>
        [Fact]
        public async Task Submit_One_Run_Then_Complete_Should_Remain_Listed_With_Assigned_Http_Runtime()
        {
            await using var fixture =
                await CreateHttpRuntimePoolFixtureAsync()
                    .ConfigureAwait(false);

            var mcp =
                fixture.Mcp;

            var pipelineName =
                CreatePipelineName();

            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 20,
                    flakyStepInterval: 0);

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                        submitRequest,
                        count: 1)
                    .ConfigureAwait(false);

            Assert.Single(
                submitResults);

            Assert.True(
                submitResults[0].Success,
                submitResults[0].FailureReason ?? submitResults[0].Message);

            var drainResult =
                await DrainHttpRuntimePoolAsync(
                        mcp,
                        maxDispatches: 1)
                    .ConfigureAwait(false);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                        mcp,
                        pipelineName,
                        expectedCount: 1,
                        timeout: TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false);

            var dispatchedRun =
                dispatchedRuns.Single();

            AssertAssignedToHttpRuntimePool(
                dispatchedRun);

            var finalStatuses =
                await McpTestWaitHelpers.WaitForTerminalRuntimeRunStatusesAsync(
                        mcp,
                        dispatchedRuns,
                        timeout: TimeSpan.FromMinutes(1))
                    .ConfigureAwait(false);

            var finalStatus =
                finalStatuses.Single();

            Assert.True(
                finalStatus.Success,
                finalStatus.FailureReason ?? finalStatus.Message);

            Assert.Equal(
                "completed",
                finalStatus.RunState?.Status);

            var listResult =
                await ListAllSharedRunsAsync(mcp)
                    .ConfigureAwait(false);

            Assert.True(
                listResult.Success,
                listResult.FailureReason ?? listResult.Message);

            var listedRun =
                listResult.Runs.Single(run =>
                    string.Equals(
                        run.PipelineKey,
                        pipelineName,
                        StringComparison.Ordinal));

            AssertAssignedToHttpRuntimePool(
                listedRun);

            Assert.Equal(
                dispatchedRun.LocalRunId,
                listedRun.LocalRunId);

            output.WriteLine(
                $"Completed HTTP-provider run remained listed. PipelineKey='{pipelineName}', RuntimeInstanceId='{listedRun.AssignedRuntimeInstanceId}', LocalRunId='{listedRun.LocalRunId}'.");
        }

        /// <summary>
        /// Verifies that shared queue activity still reports HTTP-provider runs after dispatch and completion.
        /// </summary>
        [Fact]
        public async Task Submit_Two_Runs_Then_Complete_Should_Show_Activity_For_HttpProvider()
        {
            await using var fixture =
                await CreateHttpRuntimePoolFixtureAsync()
                    .ConfigureAwait(false);

            var mcp =
                fixture.Mcp;

            var pipelineName =
                CreatePipelineName();

            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 20,
                    flakyStepInterval: 0);

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                        submitRequest,
                        count: 2)
                    .ConfigureAwait(false);

            Assert.Equal(
                2,
                submitResults.Count);

            Assert.All(
                submitResults,
                result => Assert.True(
                    result.Success,
                    result.FailureReason ?? result.Message));

            var drainResult =
                await DrainHttpRuntimePoolAsync(
                        mcp,
                        maxDispatches: 2)
                    .ConfigureAwait(false);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                        mcp,
                        pipelineName,
                        expectedCount: 2,
                        timeout: TimeSpan.FromMinutes(1))
                    .ConfigureAwait(false);

            Assert.All(
                dispatchedRuns,
                AssertAssignedToHttpRuntimePool);

            var finalStatuses =
                await McpTestWaitHelpers.WaitForTerminalRuntimeRunStatusesAsync(
                        mcp,
                        dispatchedRuns,
                        timeout: TimeSpan.FromMinutes(2))
                    .ConfigureAwait(false);

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
                        })
                    .ConfigureAwait(false);

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
                AssertAssignedToHttpRuntimePool);

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
            await using var fixture =
                await CreateHttpRuntimePoolFixtureAsync()
                    .ConfigureAwait(false);

            var mcp =
                fixture.Mcp;

            var pipelineName =
                CreatePipelineName();

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
                        count: 1)
                    .ConfigureAwait(false);

            Assert.Single(
                submitResults);

            Assert.True(
                submitResults[0].Success,
                submitResults[0].FailureReason ?? submitResults[0].Message);

            var drainResult =
                await DrainHttpRuntimePoolAsync(
                        mcp,
                        maxDispatches: 1)
                    .ConfigureAwait(false);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                        mcp,
                        pipelineName,
                        expectedCount: 1,
                        timeout: TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false);

            var run =
                dispatchedRuns.Single();

            AssertAssignedToHttpRuntimePool(
                run);

            var runningStatus =
                await McpTestWaitHelpers.WaitForRuntimeRunExecutionIdAsync(
                        mcp,
                        run,
                        timeout: TimeSpan.FromMinutes(1))
                    .ConfigureAwait(false);

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
                        })
                    .ConfigureAwait(false);

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
                        ])
                    .ConfigureAwait(false);

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
                        })
                    .ConfigureAwait(false);

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
                        ])
                    .ConfigureAwait(false);

            Assert.True(
                resumedStatus.Success,
                resumedStatus.FailureReason ?? resumedStatus.Message);

            var finalStatuses =
                await McpTestWaitHelpers.WaitForTerminalRuntimeRunStatusesAsync(
                        mcp,
                        dispatchedRuns,
                        timeout: TimeSpan.FromMinutes(2))
                    .ConfigureAwait(false);

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
        /// <remarks>
        /// This test is kept from the old class, but now targets a child runtime instance
        /// from the HTTP runtime pool instead of the removed single HTTP runtime fixture.
        /// </remarks>
        [Fact]
        public async Task Submit_Run_Then_Cancel_Queued_Http_Run_Should_Not_Create_Execution()
        {
            await using var fixture =
                await CreateHttpRuntimePoolFixtureAsync()
                    .ConfigureAwait(false);

            var mcp =
                fixture.Mcp;

            var pipelineName =
                CreatePipelineName();

            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 50,
                    flakyStepInterval: 0);

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                        submitRequest,
                        count: 1)
                    .ConfigureAwait(false);

            Assert.Single(
                submitResults);

            Assert.True(
                submitResults[0].Success,
                submitResults[0].FailureReason ?? submitResults[0].Message);

            var drainResult =
                await DrainHttpRuntimePoolAsync(
                        mcp,
                        maxDispatches: 1)
                    .ConfigureAwait(false);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                        mcp,
                        pipelineName,
                        expectedCount: 1,
                        timeout: TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false);

            var run =
                dispatchedRuns.Single();

            AssertAssignedToHttpRuntimePool(
                run);

            var cancelResult =
                await mcp.CancelRuntimeQueueRunAsync(
                        new AiRuntimeQueueControlPlaneRequest
                        {
                            Operation = AiRuntimeQueueControlPlaneOperation.CancelRun,
                            RuntimeInstanceId = run.AssignedRuntimeInstanceId,
                            RunId = run.LocalRunId,
                            Reason = "HTTP provider integration test queued-run cancel.",
                            RequestedBy = RequestedBy,
                            Source = Source
                        })
                    .ConfigureAwait(false);

            Assert.True(
                cancelResult.Success,
                cancelResult.FailureReason ?? cancelResult.Message);

            output.WriteLine(
                $"HTTP runtime queue cancel request accepted. RuntimeInstanceId='{run.AssignedRuntimeInstanceId}', LocalRunId='{run.LocalRunId}'.");
        }

        /// <summary>
        /// Verifies that a long-running HTTP-provider execution can receive a cancellation request.
        /// </summary>
        [Fact]
        public async Task Submit_Long_Running_Http_Execution_Then_Cancel_Should_Request_Cancellation()
        {
            await using var fixture =
                await CreateHttpRuntimePoolFixtureAsync()
                    .ConfigureAwait(false);

            var mcp =
                fixture.Mcp;

            var pipelineName =
                CreatePipelineName();

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
                        count: 1)
                    .ConfigureAwait(false);

            Assert.Single(
                submitResults);

            Assert.True(
                submitResults[0].Success,
                submitResults[0].FailureReason ?? submitResults[0].Message);

            var drainResult =
                await DrainHttpRuntimePoolAsync(
                        mcp,
                        maxDispatches: 1)
                    .ConfigureAwait(false);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                        mcp,
                        pipelineName,
                        expectedCount: 1,
                        timeout: TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false);

            var run =
                dispatchedRuns.Single();

            AssertAssignedToHttpRuntimePool(
                run);

            var runningStatus =
                await McpTestWaitHelpers.WaitForRuntimeRunExecutionIdAsync(
                        mcp,
                        run,
                        timeout: TimeSpan.FromMinutes(1))
                    .ConfigureAwait(false);

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
                        })
                    .ConfigureAwait(false);

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
                        ])
                    .ConfigureAwait(false);

            Assert.True(
                cancellingStatus.Success,
                cancellingStatus.FailureReason ?? cancellingStatus.Message);

            output.WriteLine(
                $"HTTP execution cancellation requested. RuntimeInstanceId='{run.AssignedRuntimeInstanceId}', LocalRunId='{run.LocalRunId}', ExecutionId='{executionId}', ControlStatus='{cancellingStatus.State?.Status}'.");
        }

        /// <summary>
        /// Creates a generic MCP/runtime fixture configured for HTTP provider scenarios.
        /// </summary>
        /// <returns>The initialized generic MCP/runtime fixture.</returns>
        private static async Task<GenericMcpRuntimeFixture> CreateHttpRuntimePoolFixtureAsync()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "http-runtime-provider");

            var fixture =
                new GenericMcpRuntimeFixture(
                    CreateHttpControlPlaneSettings(
                        controlPlaneId),
                    CreateHttpRuntimeInstanceHostSettings(
                        controlPlaneId),
                    rbacTenantId: TenantId);

            await fixture.InitializeAsync()
                .ConfigureAwait(false);

            return fixture;
        }

        /// <summary>
        /// Creates MCP control-plane host settings for HTTP provider scenarios.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier shared by the scenario hosts.</param>
        /// <returns>The MCP control-plane host settings.</returns>
        private static Dictionary<string, string?> CreateHttpControlPlaneSettings(
            string controlPlaneId)
        {
            return GenericMcpServerTestSettings.CreateMcpSettings(
                controlPlaneId,
                new Dictionary<string, string?>
                {
                    ["AiMcpHost:Mode"] = "ControlPlaneWithHttpRuntimeInstances",
                    ["AiMcpHost:EnableSharedQueuePump"] = "true",

                    ["AiSharedQueueBackgroundService:Enabled"] = "true",
                    ["AiSharedQueueBackgroundService:WaitForRuntimeReadiness"] = "true",
                    ["AiSharedQueueBackgroundService:RuntimeReadinessTimeout"] = "00:00:30",

                    ["AiSharedQueuePump:Enabled"] = "true",
                    ["AiSharedRuntimeController:SubmitMode"] = "QueueFirst",

                    ["AiRuntimeInstanceRegistration:ControlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:ProviderName"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = "mcp-control-plane-http",
                    ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "control-plane-with-http-runtime",
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-http-provider-scenario",

                    ["AiEngine:ControlPlane:ControlPlaneId"] = controlPlaneId,
                    ["AiEngine:RuntimeInstanceId"] = "mcp-control-plane-http"
                });
        }

        /// <summary>
        /// Creates runtime-instance-only host settings for HTTP provider scenarios.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier shared by the scenario hosts.</param>
        /// <returns>The runtime-instance-only host settings.</returns>
        private static Dictionary<string, string?> CreateHttpRuntimeInstanceHostSettings(
            string controlPlaneId)
        {
            return GenericMcpServerTestSettings.CreateRuntimeInstanceSettings(
                controlPlaneId,
                RuntimeInstanceHostId,
                RuntimeInstanceHostPort,
                new Dictionary<string, string?>
                {
                    ["AiRuntimeInstanceRegistration:ControlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = RuntimeInstanceHostId,
                    ["AiRuntimeInstanceRegistration:ProviderName"] = "http",

                    ["AiRuntimeInstanceRegistration:ProviderMetadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:transport.endpoint"] = $"http://localhost:{RuntimeInstanceHostPort}",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:runtime.instance.id"] = RuntimeInstanceHostId,

                    ["AiRuntimeInstanceRegistration:Metadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:transport.endpoint"] = $"http://localhost:{RuntimeInstanceHostPort}",
                    ["AiRuntimeInstanceRegistration:Metadata:runtime.instance.id"] = RuntimeInstanceHostId,
                    ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "runtime-instance-only",
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-http-provider-runtime-pool",

                    ["AiLocalRuntimeInstancePool:Enabled"] = "true",
                    ["AiLocalRuntimeInstancePool:InstanceCount"] = "3",
                    ["AiLocalRuntimeInstancePool:WorkerCountPerInstance"] = "10",
                    ["AiLocalRuntimeInstancePool:MaxConcurrentRunsPerInstance"] = "5",
                    ["AiLocalRuntimeInstancePool:RuntimeInstanceIdPrefix"] = "runtime-http",

                    ["AiEngine:ControlPlane:ControlPlaneId"] = controlPlaneId,
                    ["AiEngine:RuntimeInstanceId"] = RuntimeInstanceHostId,

                    ["AiEngine:PipelineBackgroundController:RuntimeInstanceId"] = RuntimeInstanceHostId,
                    ["AiEngine:PipelineBackgroundController:MaxConcurrentRuns"] = "5",
                    ["AiEngine:PipelineBackgroundController:QueueCapacity"] = "500",
                    ["AiEngine:PipelineBackgroundController:Distributed:Enabled"] = "true",
                    ["AiEngine:PipelineBackgroundController:Distributed:WorkerCount"] = "10",
                    ["AiEngine:PipelineBackgroundController:MaxLocalWorkersPerExecution"] = "5",

                    ["AiEngine:RuntimeInstanceWorker:RuntimeInstanceId"] = RuntimeInstanceHostId
                });
        }

        /// <summary>
        /// Creates a unique pipeline name for one test scenario.
        /// </summary>
        /// <returns>The unique pipeline name.</returns>
        private static string CreatePipelineName()
        {
            return $"mcp-http-test-pipeline-{Guid.NewGuid():N}";
        }

        /// <summary>
        /// Creates a shared runtime submit request for a test pipeline.
        /// </summary>
        /// <param name="pipelineName">The pipeline key.</param>
        /// <param name="stepCount">The number of test pipeline steps.</param>
        /// <param name="flakyStepInterval">The flaky step interval.</param>
        /// <returns>The shared runtime controller request.</returns>
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

        /// <summary>
        /// Drains the shared queue for HTTP runtime provider scenarios.
        /// </summary>
        /// <param name="mcp">The MCP test client.</param>
        /// <param name="maxDispatches">The maximum number of dispatches to perform.</param>
        /// <returns>The shared queue pump result.</returns>
        private static async Task<AiSharedQueuePumpResult> DrainHttpRuntimePoolAsync(
            McpTestClient mcp,
            int maxDispatches)
        {
            return await mcp.DrainQueueAsync(
                    new AiSharedQueuePumpRequest
                    {
                        PumpRuntimeInstanceId = PumpRuntimeInstanceId,
                        PumpWorkerId = WorkerId,
                        MaxDispatches = maxDispatches,
                        RequestedBy = RequestedBy,
                        Source = Source
                    })
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Lists all shared runs, including terminal runs.
        /// </summary>
        /// <param name="mcp">The MCP test client.</param>
        /// <returns>The shared runtime controller result.</returns>
        private static async Task<AiSharedRuntimeControllerResult> ListAllSharedRunsAsync(
            McpTestClient mcp)
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
                    })
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Asserts that a shared run was assigned to the HTTP runtime pool.
        /// </summary>
        /// <param name="run">The shared run record.</param>
        private static void AssertAssignedToHttpRuntimePool(
            AiSharedRunRecord run)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(run.AssignedRuntimeInstanceId));

            Assert.True(
                run.AssignedRuntimeInstanceId.Contains(
                    RuntimeInstancePrefix,
                    StringComparison.Ordinal),
                $"Expected assigned runtime instance id to contain '{RuntimeInstancePrefix}', but found '{run.AssignedRuntimeInstanceId}'.");

            Assert.False(
                string.IsNullOrWhiteSpace(run.LocalRunId));
        }

        /// <summary>
        /// Writes the currently registered runtime instances to the test output.
        /// </summary>
        /// <param name="mcp">The MCP test client.</param>
        /// <returns>A task representing the asynchronous log operation.</returns>
        private async Task LogRuntimeInstancesAsync(
            McpTestClient mcp)
        {
            var instances =
                await mcp.ListRuntimeInstancesAsync()
                    .ConfigureAwait(false);

            foreach (var instance in instances.OrderBy(x => x.RuntimeInstanceId, StringComparer.Ordinal))
            {
                output.WriteLine(
                    $"RuntimeInstance Id='{instance.RuntimeInstanceId}', Role='{instance.Role}', Status='{instance.Status}', CanAcceptRun='{instance.CanAcceptRun}', Workers='{instance.WorkerCount}', ActiveWorkers='{instance.ActiveWorkerCount}', AvailableWorkers='{instance.AvailableWorkerCount}', Slots='{instance.AvailableRunSlots}'.");
            }
        }
    }
}
