using Microsoft.VisualStudio.TestPlatform.Utilities;
using Multiplexed.Abstractions.AI.ControlPlane.Execution;
using Multiplexed.Abstractions.AI.ControlPlane.Replay;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Activity;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
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
        private const string TenantId = "test-tenant";
        private const string RequestedBy = "mcp-integration-test";
        private const string Source = "mcp-test";

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
                TenantId = TenantId,
                CorrelationId = $"mcp-test-correlation-{Guid.NewGuid():N}",
                RequestedBy = RequestedBy,
                Source = Source,
                RunRequest = McpTestPipelineFactory.CreateRunRequest(
                    pipelineName: pipelineName,
                    stepCount: 50,
                    input: new
                    {
                        source = RequestedBy,
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
                RequestedBy = RequestedBy,
                Source = Source
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
                TenantId = TenantId,
                RequestedBy = RequestedBy,
                Source = Source,
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
                        IncludeCompleted = true,
                        IncludeFailed = true,
                        IncludeCancelled = true,
                        RequestedBy = RequestedBy,
                        Source = Source
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
                await DrainPipelineQueueAsync(
                    pipelineName,
                    maxDispatches: 4,
                    reason: "MCP integration test manual pipeline drain.");

            Assert.NotNull(
                drainResult);

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
                    Assert.False(
                        string.IsNullOrWhiteSpace(run.AssignedRuntimeInstanceId));

                    Assert.False(
                        string.IsNullOrWhiteSpace(run.LocalRunId));
                });

            var afterDrain =
                await mcp.ListSharedRunsAsync(
                    new AiSharedRuntimeControllerRequest
                    {
                        Operation = AiSharedRuntimeControllerOperation.ListRuns,
                        IncludeCompleted = true,
                        IncludeFailed = true,
                        IncludeCancelled = true,
                        RequestedBy = RequestedBy,
                        Source = Source
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

            Assert.All(
                matchingRuns,
                run =>
                {
                    Assert.False(
                        string.IsNullOrWhiteSpace(run.AssignedRuntimeInstanceId));

                    Assert.False(
                        string.IsNullOrWhiteSpace(run.LocalRunId));
                });

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

            var drainResult =
                await DrainPipelineQueueAsync(
                    pipelineName,
                    maxDispatches: 4,
                    reason: "MCP integration test manual pipeline drain.");

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                    mcp,
                    pipelineName,
                    expectedCount: 4,
                    timeout: TimeSpan.FromMinutes(1));

            foreach (var run in dispatchedRuns)
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(run.LocalRunId));

                Assert.False(
                    string.IsNullOrWhiteSpace(run.AssignedRuntimeInstanceId));
            }

            var finalStatuses =
                await McpTestWaitHelpers.WaitForTerminalRuntimeRunStatusesAsync(
                    mcp,
                    dispatchedRuns,
                    timeout: TimeSpan.FromMinutes(1));

            McpScenarioOutput.WriteRuntimeRunStatusSummary(
                output,
                nameof(Submit_Four_Runs_Then_Drain_Should_Eventually_Expose_Runtime_Run_Status),
                pipelineName,
                dispatchedRuns,
                finalStatuses);

            Assert.All(
                finalStatuses,
                result =>
                {
                    Assert.True(
                        result.Success,
                        result.FailureReason ?? result.Message);

                    Assert.Equal(
                        "completed",
                        result.RunState?.Status);

                    Assert.False(
                        string.IsNullOrWhiteSpace(result.ExecutionId ?? result.RunState?.ExecutionId),
                        $"RunId='{result.RunId}' failed to expose an ExecutionId. " +
                        $"Status='{result.RunState?.Status}', " +
                        $"FailureReason='{result.FailureReason ?? result.RunState?.FailureReason}', " +
                        $"Message='{result.Message}'.");
                });
        }


        [Fact]
        public async Task Submit_One_Run_With_100_Step_Pipeline_Then_Drain_Should_Complete_And_Display_Observability()
        {
            var pipelineName =
                $"mcp-test-pipeline-{Guid.NewGuid():N}";

            var scenarioName =
                nameof(Submit_One_Run_With_100_Step_Pipeline_Then_Drain_Should_Complete_And_Display_Observability);

            var submitRequest = new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                PipelineKey = pipelineName,
                TenantId = TenantId,
                RequestedBy = RequestedBy,
                Source = Source,
                RunRequest = McpTestPipelineFactory.CreateRunRequest(
                    pipelineName,
                    stepCount: 100,
                    flakyStepInterval: 10)
            };

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                    submitRequest,
                    count: 1);

            Assert.Single(submitResults);

            Assert.All(
                submitResults,
                result => Assert.True(
                    result.Success,
                    result.FailureReason ?? result.Message));

            var drainResult =
                await DrainPipelineQueueAsync(
                    pipelineName,
                    maxDispatches: 1,
                    reason: "MCP integration test manual pipeline drain.");

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

            McpScenarioOutput.WriteRuntimeRunStatusSummary(
                output,
                scenarioName,
                pipelineName,
                dispatchedRuns,
                finalStatuses);

            var finalStatus =
                finalStatuses.Single();

            Assert.True(
                finalStatus.Success,
                finalStatus.FailureReason ?? finalStatus.Message);

            Assert.Equal(
                "completed",
                finalStatus.RunState?.Status);

            var executionId =
                finalStatus.ExecutionId ??
                finalStatus.RunState?.ExecutionId;

            Assert.False(
                string.IsNullOrWhiteSpace(executionId));

            var ledgerEntries =
                await mcp.GetLedgerByExecutionAsync(
                    executionId!);

            var traceEvents =
                await mcp.GetTraceByExecutionAsync(
                    executionId!);

            var metricsStatus =
                await mcp.GetMetricsStatusAsync();

            McpScenarioOutput.WriteObservabilitySummary(
                output,
                scenarioName,
                executionId!,
                ledgerEntries,
                traceEvents,
                metricsStatus);

            Assert.NotEmpty(
                ledgerEntries);

            Assert.NotEmpty(
                traceEvents);

            Assert.False(
                string.IsNullOrWhiteSpace(metricsStatus));
        }

        [Fact]
        public async Task Submit_One_Run_With_100_Step_Pipeline_Then_Replay_Should_Return_Report_Ledger_And_Trace()
        {
            var pipelineName =
                $"mcp-test-pipeline-{Guid.NewGuid():N}";

            var scenarioName =
                nameof(Submit_One_Run_With_100_Step_Pipeline_Then_Replay_Should_Return_Report_Ledger_And_Trace);

            var submitRequest = new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                PipelineKey = pipelineName,
                TenantId = TenantId,
                RequestedBy = RequestedBy,
                Source = Source,
                RunRequest = McpTestPipelineFactory.CreateRunRequest(
                    pipelineName,
                    stepCount: 100,
                    flakyStepInterval: 10)
            };

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                    submitRequest,
                    count: 1);

            Assert.Single(submitResults);

            Assert.All(
                submitResults,
                result => Assert.True(
                    result.Success,
                    result.FailureReason ?? result.Message));

            var drainResult =
                await DrainPipelineQueueAsync(
                    pipelineName,
                    maxDispatches: 1,
                    reason: "MCP integration test manual pipeline drain.");

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

            Assert.Equal(
                "completed",
                finalStatus.RunState?.Status);

            var executionId =
                finalStatus.ExecutionId ??
                finalStatus.RunState?.ExecutionId;

            Assert.False(
                string.IsNullOrWhiteSpace(executionId));

            var replayRequest = new AiReplayControlRequest
            {
                ExecutionId = executionId!,
                CorrelationId = $"mcp-replay-correlation-{Guid.NewGuid():N}",
                RequestedBy = RequestedBy,
                Source = Source,
                Operation = AiReplayOperation.Replay
            };

            replayRequest.Operation = AiReplayOperation.Replay;
            var replayResult =
                await mcp.ReplayExecutionAsync(
                    replayRequest);

            replayRequest.Operation = AiReplayOperation.GetReport;
            var replayReport =
                await mcp.GetReplayReportAsync(
                    replayRequest);

            replayRequest.Operation = AiReplayOperation.GetLedger;
            var replayLedger =
                await mcp.GetReplayLedgerAsync(
                    replayRequest);

            replayRequest.Operation = AiReplayOperation.GetTimeline;
            var replayTrace =
                await mcp.GetReplayTraceAsync(
                    replayRequest);

            McpScenarioOutput.WriteReplaySummary(
                output,
                scenarioName,
                executionId!,
                replayResult,
                replayReport,
                replayLedger,
                replayTrace);

            Assert.True(
                replayResult.Success,
                replayResult.FailureReason ?? replayResult.Message);

            Assert.True(
                replayReport.Success,
                replayReport.FailureReason ?? replayReport.Message);

            Assert.True(
                replayLedger.Success,
                replayLedger.FailureReason ?? replayLedger.Message);

            Assert.True(
                replayTrace.Success,
                replayTrace.FailureReason ?? replayTrace.Message);
        }

        [Fact]
        public async Task Submit_Long_Running_Execution_Then_Pause_And_Resume_Should_Complete()
        {
            var pipelineName =
                $"mcp-test-pipeline-{Guid.NewGuid():N}";

            var scenarioName =
                nameof(Submit_Long_Running_Execution_Then_Pause_And_Resume_Should_Complete);

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
                            source = RequestedBy,
                            scenario = "execution-pause-resume",
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
                await DrainPipelineQueueAsync(
                    pipelineName,
                    maxDispatches: 1,
                    reason: "MCP integration test manual pipeline drain.");

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                    mcp,
                    pipelineName,
                    expectedCount: 1,
                    timeout: TimeSpan.FromMinutes(1));

            var run =
                dispatchedRuns.Single();

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
                        Reason = "MCP integration test execution pause.",
                        RequestedBy = RequestedBy,
                        Source = Source
                    });

            Assert.True(
                pauseResult.Success,
                pauseResult.FailureReason ?? pauseResult.Message);

            var pausedStatus =
                await WaitForExecutionControlStatusAsync(
                    executionId!,
                    timeout: TimeSpan.FromSeconds(10),
                    expectedStatuses: new[]
                    {
                        "Paused"
                    });

            Assert.True(
                pausedStatus.Success,
                pausedStatus.FailureReason ?? pausedStatus.Message);

            McpScenarioOutput.WriteExecutionControlSummary(
                output,
                $"{scenarioName}_ExecutionPaused",
                executionId!,
                pauseResult,
                pausedStatus,
                resumeResult: null,
                resumedStatus: null);

            var resumeResult =
                await mcp.ResumeExecutionAsync(
                    new AiExecutionControlPlaneRequest
                    {
                        Operation = AiExecutionControlPlaneOperation.Resume,
                        ExecutionId = executionId!,
                        Reason = "MCP integration test execution resume.",
                        RequestedBy = RequestedBy,
                        Source = Source
                    });

            Assert.True(
                resumeResult.Success,
                resumeResult.FailureReason ?? resumeResult.Message);

            var resumedStatus =
                await WaitForExecutionControlStatusAsync(
                    executionId!,
                    timeout: TimeSpan.FromSeconds(10),
                    expectedStatuses: new[]
                    {
                        "Running",
                        "None",
                        "Completed"
                    });

            Assert.True(
                resumedStatus.Success,
                resumedStatus.FailureReason ?? resumedStatus.Message);

            McpScenarioOutput.WriteExecutionControlSummary(
                output,
                $"{scenarioName}_ExecutionResumed",
                executionId!,
                pauseResult,
                pausedStatus,
                resumeResult,
                resumedStatus);

            var finalStatuses =
                await McpTestWaitHelpers.WaitForTerminalRuntimeRunStatusesAsync(
                    mcp,
                    dispatchedRuns,
                    timeout: TimeSpan.FromMinutes(2));

            McpScenarioOutput.WriteRuntimeRunStatusSummary(
                output,
                scenarioName,
                pipelineName,
                dispatchedRuns,
                finalStatuses);

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

            async Task<AiExecutionControlPlaneResult> WaitForExecutionControlStatusAsync(
                string targetExecutionId,
                TimeSpan timeout,
                IReadOnlyCollection<string> expectedStatuses)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(targetExecutionId);
                ArgumentNullException.ThrowIfNull(expectedStatuses);

                var expected =
                    new HashSet<string>(
                        expectedStatuses,
                        StringComparer.OrdinalIgnoreCase);

                var deadline =
                    DateTimeOffset.UtcNow.Add(timeout);

                AiExecutionControlPlaneResult? lastStatus = null;

                while (DateTimeOffset.UtcNow < deadline)
                {
                    lastStatus =
                        await mcp.GetExecutionStatusAsync(
                            new AiExecutionControlPlaneRequest
                            {
                                Operation = AiExecutionControlPlaneOperation.GetStatus,
                                ExecutionId = targetExecutionId,
                                RequestedBy = RequestedBy,
                                Source = Source
                            });

                    Assert.True(
                        lastStatus.Success,
                        lastStatus.FailureReason ?? lastStatus.Message);

                    var controlStatus =
                        Convert.ToString(
                            lastStatus.State?.Status);

                    if (!string.IsNullOrWhiteSpace(controlStatus) &&
                        expected.Contains(controlStatus))
                    {
                        return lastStatus;
                    }

                    await Task.Delay(
                        TimeSpan.FromMilliseconds(100));
                }

                var lastControlStatus =
                    lastStatus is null
                        ? "<none>"
                        : Convert.ToString(lastStatus.State?.Status) ?? "<null>";

                Assert.Fail(
                    $"Execution '{targetExecutionId}' did not reach expected control status '{string.Join(", ", expectedStatuses)}' within '{timeout}'. LastControlStatus='{lastControlStatus}'.");

                throw new InvalidOperationException(
                    "Unreachable assertion path.");
            }
        }

        [Fact]
        public async Task Submit_Run_Then_Cancel_Queued_Run_Should_Not_Create_Execution()
        {
            var pipelineName =
                $"mcp-test-pipeline-{Guid.NewGuid():N}";

            var scenarioName =
                nameof(Submit_Run_Then_Cancel_Queued_Run_Should_Not_Create_Execution);

            var pausedRuntimeInstanceIds =
                await PauseRuntimeQueuesAsync(
                    mcp,
                    "MCP integration test queued-run cancel setup.");

            try
            {
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
                            stepCount: 50,
                            flakyStepInterval: 10)
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
                    await DrainPipelineQueueAsync(
                        pipelineName,
                        maxDispatches: 1,
                        reason: "MCP integration test manual pipeline drain.");

                Assert.True(
                    drainResult.Success,
                    drainResult.FailureReason);

                var dispatchedRuns =
                    await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                        mcp,
                        pipelineName,
                        expectedCount: 1,
                        timeout: TimeSpan.FromMinutes(1));

                var run =
                    dispatchedRuns.Single();

                var statusBeforeCancel =
                    await McpTestWaitHelpers.WaitForRuntimeRunStatusAsync(
                        mcp,
                        run,
                        expectedStatus: "queued",
                        timeout: TimeSpan.FromSeconds(10));

                Assert.True(
                    statusBeforeCancel.Success,
                    statusBeforeCancel.FailureReason ?? statusBeforeCancel.Message);

                Assert.Equal(
                    "queued",
                    statusBeforeCancel.RunState?.Status);

                Assert.True(
                    string.IsNullOrWhiteSpace(statusBeforeCancel.ExecutionId ?? statusBeforeCancel.RunState?.ExecutionId));

                McpScenarioOutput.WriteRuntimeRunStatusSummary(
                    output,
                    $"{scenarioName}_BeforeCancel",
                    pipelineName,
                    dispatchedRuns,
                    new[] { statusBeforeCancel });

                var cancelResult =
                    await mcp.CancelRuntimeQueueRunAsync(
                        new AiRuntimeQueueControlPlaneRequest
                        {
                            Operation = AiRuntimeQueueControlPlaneOperation.CancelRun,
                            RuntimeInstanceId = run.AssignedRuntimeInstanceId,
                            RunId = run.LocalRunId,
                            Reason = "MCP integration test queued/run cancel.",
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
                        timeout: TimeSpan.FromSeconds(10));

                Assert.True(
                    statusAfterCancel.Success,
                    statusAfterCancel.FailureReason ?? statusAfterCancel.Message);

                Assert.True(
                    string.IsNullOrWhiteSpace(statusAfterCancel.ExecutionId ?? statusAfterCancel.RunState?.ExecutionId));

                McpScenarioOutput.WriteRuntimeRunStatusSummary(
                    output,
                    $"{scenarioName}_AfterCancel",
                    pipelineName,
                    dispatchedRuns,
                    new[] { statusAfterCancel });
            }
            finally
            {
                await ResumeRuntimeQueuesBestEffortAsync(
                    mcp,
                    pausedRuntimeInstanceIds,
                    "MCP integration test cleanup resume.");
            }

            await Task.Delay(500);

            var finalRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                    mcp,
                    pipelineName,
                    expectedCount: 1,
                    timeout: TimeSpan.FromSeconds(10));

            var finalRun =
                finalRuns.Single();

            var statusAfterResume =
                await mcp.GetRuntimeQueueRunStatusAsync(
                    new AiRuntimeQueueControlPlaneRequest
                    {
                        Operation = AiRuntimeQueueControlPlaneOperation.GetRunStatus,
                        RuntimeInstanceId = finalRun.AssignedRuntimeInstanceId,
                        RunId = finalRun.LocalRunId,
                        RequestedBy = RequestedBy,
                        Source = Source
                    });

            Assert.True(
                statusAfterResume.Success,
                statusAfterResume.FailureReason ?? statusAfterResume.Message);

            Assert.Equal(
                "cancelled",
                statusAfterResume.RunState?.Status);

            Assert.True(
                string.IsNullOrWhiteSpace(statusAfterResume.ExecutionId ?? statusAfterResume.RunState?.ExecutionId));

            McpScenarioOutput.WriteRuntimeRunStatusSummary(
                output,
                $"{scenarioName}_AfterResumeCleanup",
                pipelineName,
                finalRuns,
                new[] { statusAfterResume });
        }

        [Fact]
        public async Task Submit_Long_Running_Execution_Then_Cancel_Should_Request_Cancellation()
        {
            var pipelineName =
                $"mcp-test-pipeline-{Guid.NewGuid():N}";

            var scenarioName =
                nameof(Submit_Long_Running_Execution_Then_Cancel_Should_Request_Cancellation);

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
                            source = RequestedBy,
                            scenario = "execution-cancel-request",
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
                await DrainPipelineQueueAsync(
                    pipelineName,
                    maxDispatches: 1,
                    reason: "MCP integration test manual pipeline drain.");

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
                        Reason = "MCP integration test execution cancellation request.",
                        RequestedBy = RequestedBy,
                        Source = Source
                    });

            Assert.True(
                cancelResult.Success,
                cancelResult.FailureReason ?? cancelResult.Message);

            var cancellingStatus =
                await mcp.GetExecutionStatusAsync(
                    new AiExecutionControlPlaneRequest
                    {
                        Operation = AiExecutionControlPlaneOperation.GetStatus,
                        ExecutionId = executionId!,
                        RequestedBy = RequestedBy,
                        Source = Source
                    });

            Assert.True(
                cancellingStatus.Success,
                cancellingStatus.FailureReason ?? cancellingStatus.Message);

            Assert.Equal(
                "Cancelling",
                cancellingStatus.State?.Status.ToString());

            McpScenarioOutput.WriteExecutionControlSummary(
                output,
                $"{scenarioName}_ExecutionCancelling",
                executionId!,
                cancelResult,
                cancellingStatus,
                resumeResult: null,
                resumedStatus: null);
        }

        [Fact]
        public async Task Runtime_Instance_Tools_Should_Return_Empty_List_When_No_Instance_Is_Registered()
        {
            var allInstances =
                await mcp.ListRuntimeInstancesAsync(
                    includeStopped: true);

            var activeInstances =
                await mcp.ListActiveRuntimeInstancesAsync();

            Assert.NotNull(allInstances);
            Assert.NotNull(activeInstances);

            McpScenarioOutput.WriteRuntimeInstanceSummary(
                output,
                nameof(Runtime_Instance_Tools_Should_Return_Empty_List_When_No_Instance_Is_Registered),
                allInstances,
                activeInstances,
                selectedStatus: null);
        }

        [Fact]
        public async Task Submit_Five_Runs_Without_Manual_Drain_Should_Show_Shared_Queue_And_Shared_Run_Status()
        {
            var pipelineName =
                $"mcp-test-pipeline-{Guid.NewGuid():N}";

            var scenarioName =
                nameof(Submit_Five_Runs_Without_Manual_Drain_Should_Show_Shared_Queue_And_Shared_Run_Status);

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
                        stepCount: 20,
                        flakyStepInterval: 0)
                };

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

            var sharedRuns =
                await mcp.ListSharedRunsAsync(
                    new AiSharedRuntimeControllerRequest
                    {
                        Operation = AiSharedRuntimeControllerOperation.ListRuns,
                        IncludeCompleted = true,
                        IncludeFailed = true,
                        IncludeCancelled = true,
                        RequestedBy = RequestedBy,
                        Source = Source
                    });

            Assert.True(
                sharedRuns.Success,
                sharedRuns.FailureReason ?? sharedRuns.Message);

            var scenarioRuns =
                sharedRuns.Runs
                    .Where(run =>
                        string.Equals(
                            run.PipelineKey,
                            pipelineName,
                            StringComparison.Ordinal))
                    .ToArray();

            Assert.Equal(
                5,
                scenarioRuns.Length);

            var queueItems =
                await mcp.ListSharedQueueAsync(
                    includeTerminal: true);

            var status =
                await mcp.GetSharedQueueStatusAsync(
                    includeTerminal: true);

            McpScenarioOutput.WriteSharedQueueSummary(
                output,
                scenarioName,
                pipelineName,
                queueItems,
                status);

            Assert.NotNull(queueItems);
            Assert.NotNull(status);
        }

        [Fact]
        public async Task Submit_Five_Runs_Should_Show_Shared_Queue_Activity_Even_When_Active_Queue_Is_Empty()
        {
            var pipelineName =
                $"mcp-test-pipeline-{Guid.NewGuid():N}";

            var scenarioName =
                nameof(Submit_Five_Runs_Should_Show_Shared_Queue_Activity_Even_When_Active_Queue_Is_Empty);

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
                        stepCount: 20,
                        flakyStepInterval: 0)
                };

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

            McpScenarioOutput.WriteSharedQueueSummary(
                output,
                scenarioName,
                pipelineName,
                queueItems,
                queueStatus);

            McpScenarioOutput.WriteSharedQueueActivitySummary(
                output,
                scenarioName,
                pipelineName,
                activity);
        }

        private async Task<AiSharedQueuePumpResult> DrainPipelineQueueAsync(
            string pipelineName,
            int maxDispatches,
            string reason)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);

            return await mcp.DrainQueueAsync(
                new AiSharedQueuePumpRequest
                {
                    PumpRuntimeInstanceId = "mcp-manual-drain-pump",
                    PumpWorkerId = "mcp-manual-drain-worker",
                    MaxDispatches = maxDispatches,
                    TenantId = TenantId,
                    PipelineKey = pipelineName,
                    RequestedBy = RequestedBy,
                    Source = Source,
                    Reason = reason
                });
        }

        private static async Task<IReadOnlyList<string>> PauseRuntimeQueuesAsync(
            McpTestClient mcp,
            string reason)
        {
            var instances =
                await mcp.ListRuntimeInstancesAsync(
                    includeStopped: false);

            var runtimeInstanceIds =
                instances
                    .Where(instance => instance.Role == AiRuntimeInstanceRole.Runtime)
                    .Where(instance => instance.CanAcceptRun)
                    .Select(instance => instance.RuntimeInstanceId)
                    .Where(runtimeInstanceId => !string.IsNullOrWhiteSpace(runtimeInstanceId))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

            Assert.NotEmpty(runtimeInstanceIds);

            foreach (var runtimeInstanceId in runtimeInstanceIds)
            {
                var pauseResult =
                    await mcp.PauseRuntimeQueueAsync(
                        new AiRuntimeQueueControlPlaneRequest
                        {
                            Operation = AiRuntimeQueueControlPlaneOperation.PauseQueue,
                            RuntimeInstanceId = runtimeInstanceId,
                            Reason = reason,
                            RequestedBy = RequestedBy,
                            Source = Source
                        });

                Assert.True(
                    pauseResult.Success,
                    pauseResult.FailureReason ?? pauseResult.Message);
            }

            return runtimeInstanceIds;
        }

        private static async Task ResumeRuntimeQueuesBestEffortAsync(
            McpTestClient mcp,
            IReadOnlyCollection<string> runtimeInstanceIds,
            string reason)
        {
            foreach (var runtimeInstanceId in runtimeInstanceIds)
            {
                try
                {
                    await mcp.ResumeRuntimeQueueAsync(
                            new AiRuntimeQueueControlPlaneRequest
                            {
                                Operation = AiRuntimeQueueControlPlaneOperation.ResumeQueue,
                                RuntimeInstanceId = runtimeInstanceId,
                                Reason = reason,
                                RequestedBy = RequestedBy,
                                Source = Source
                            })
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort cleanup for integration tests.
                }
            }
        }
    }
}
