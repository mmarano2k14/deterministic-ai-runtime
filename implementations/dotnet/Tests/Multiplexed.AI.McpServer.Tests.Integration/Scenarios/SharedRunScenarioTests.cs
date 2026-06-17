using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Execution;
using Multiplexed.Abstractions.AI.ControlPlane.Replay;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Activity;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.AI.McpServer.Tests.Integration.Auth;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios
{
    /// <summary>
    /// Contains end-to-end shared run MCP scenarios.
    /// </summary>
    /// <remarks>
    /// Each test starts an isolated generic MCP control-plane host with a unique
    /// logical control-plane identifier. This avoids Redis ghost state between tests
    /// while still exercising the same MCP tools, shared queue, runtime queue,
    /// execution-control, replay, ledger, trace, and runtime-instance APIs.
    /// </remarks>
    public sealed class SharedRunScenarioTests : IAsyncLifetime
    {
        private const string TenantId = "test-tenant";
        private const string RequestedBy = "mcp-integration-test";
        private const string Source = "mcp-test";

        private readonly ITestOutputHelper output;

        private GenericMcpServerTestHost? host;
        private HttpClient? client;
        private McpTestClient mcp = default!;

        /// <summary>
        /// Initializes a new instance of the <see cref="SharedRunScenarioTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public SharedRunScenarioTests(
            ITestOutputHelper output)
        {
            this.output =
                output;
        }

        /// <summary>
        /// Starts an isolated generic MCP host for the current test.
        /// </summary>
        /// <returns>A task representing the asynchronous initialization operation.</returns>
        public async Task InitializeAsync()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "shared-run-scenario");

            host =
                new GenericMcpServerTestHost(
                    CreateDefaultLocalControlPlaneSettings(
                        controlPlaneId));

            client =
                host.CreateClient();

            mcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        client,
                        RequestedBy,
                        tenantId: TenantId)
                    .ConfigureAwait(false);

            Console.WriteLine(
                $"[SHARED RUN SCENARIO] Configured MCP RBAC headers. UserId='{RequestedBy}', TenantId='{TenantId}'.");
        }

        /// <summary>
        /// Disposes the isolated generic MCP host for the current test.
        /// </summary>
        /// <returns>A task representing the asynchronous disposal operation.</returns>
        public async Task DisposeAsync()
        {
            client?.Dispose();

            if (host is not null)
            {
                await host
                    .DisposeAsync()
                    .ConfigureAwait(false);
            }
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

            var submitRequest =
                new AiSharedRuntimeControllerRequest
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

            var submitResult =
                await mcp.SubmitRunAsync(
                        submitRequest)
                    .ConfigureAwait(false);

            Assert.True(
                submitResult.Success,
                submitResult.FailureReason ?? submitResult.Message);

            Assert.Equal(
                requestedSharedRunId,
                submitResult.SharedRunId);

            var listRequest =
                new AiSharedRuntimeControllerRequest
                {
                    Operation = AiSharedRuntimeControllerOperation.ListRuns,
                    RequestedBy = RequestedBy,
                    Source = Source
                };

            var listResult =
                await mcp.ListSharedRunsAsync(
                        listRequest)
                    .ConfigureAwait(false);

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
        /// Verifies that submitted shared runs are placed into the shared queue and that
        /// the manual drain operation dispatches available runs.
        /// </summary>
        [Fact]
        public async Task Submit_Four_Runs_Then_Drain_Should_Dispatch_Available_Runs()
        {
            var pipelineName =
                $"mcp-test-pipeline-{Guid.NewGuid():N}";

            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 20,
                    flakyStepInterval: 5);

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
                await ListAllSharedRunsAsync()
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
                await DrainPipelineQueueAsync(
                        pipelineName,
                        maxDispatches: 4,
                        reason: "MCP integration test manual pipeline drain.")
                    .ConfigureAwait(false);

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
                        timeout: TimeSpan.FromMinutes(1))
                    .ConfigureAwait(false);

            AssertDispatchedRuns(
                dispatchedRuns,
                expectedCount: 4);

            var afterDrain =
                await ListAllSharedRunsAsync()
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

            AssertDispatchedRuns(
                matchingRuns,
                expectedCount: 4);

            McpScenarioOutput.WriteDrainSummary(
                output,
                nameof(Submit_Four_Runs_Then_Drain_Should_Dispatch_Available_Runs),
                pipelineName,
                submitResults,
                beforeDrain,
                drainResult,
                afterDrain);
        }

        /// <summary>
        /// Verifies that manually drained shared runs eventually expose terminal runtime run status.
        /// </summary>
        [Fact]
        public async Task Submit_Four_Runs_Then_Drain_Should_Eventually_Expose_Runtime_Run_Status()
        {
            var pipelineName =
                $"mcp-test-pipeline-{Guid.NewGuid():N}";

            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 20,
                    flakyStepInterval: 5);

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

            var drainResult =
                await DrainPipelineQueueAsync(
                        pipelineName,
                        maxDispatches: 4,
                        reason: "MCP integration test manual pipeline drain.")
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

            AssertDispatchedRuns(
                dispatchedRuns,
                expectedCount: 4);

            var finalStatuses =
                await McpTestWaitHelpers.WaitForTerminalRuntimeRunStatusesAsync(
                        mcp,
                        dispatchedRuns,
                        timeout: TimeSpan.FromMinutes(1))
                    .ConfigureAwait(false);

            McpScenarioOutput.WriteRuntimeRunStatusSummary(
                output,
                nameof(Submit_Four_Runs_Then_Drain_Should_Eventually_Expose_Runtime_Run_Status),
                pipelineName,
                dispatchedRuns,
                finalStatuses);

            AssertTerminalCompletedStatuses(
                finalStatuses);
        }

        /// <summary>
        /// Verifies that a 100-step execution completes and exposes ledger, trace, and metrics.
        /// </summary>
        [Fact]
        public async Task Submit_One_Run_With_100_Step_Pipeline_Then_Drain_Should_Complete_And_Display_Observability()
        {
            var pipelineName =
                $"mcp-test-pipeline-{Guid.NewGuid():N}";

            var scenarioName =
                nameof(Submit_One_Run_With_100_Step_Pipeline_Then_Drain_Should_Complete_And_Display_Observability);

            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 100,
                    flakyStepInterval: 10);

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                        submitRequest,
                        count: 1)
                    .ConfigureAwait(false);

            Assert.Single(
                submitResults);

            Assert.All(
                submitResults,
                result => Assert.True(
                    result.Success,
                    result.FailureReason ?? result.Message));

            var drainResult =
                await DrainPipelineQueueAsync(
                        pipelineName,
                        maxDispatches: 1,
                        reason: "MCP integration test manual pipeline drain.")
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

            var finalStatuses =
                await McpTestWaitHelpers.WaitForTerminalRuntimeRunStatusesAsync(
                        mcp,
                        dispatchedRuns,
                        timeout: TimeSpan.FromMinutes(2))
                    .ConfigureAwait(false);

            McpScenarioOutput.WriteRuntimeRunStatusSummary(
                output,
                scenarioName,
                pipelineName,
                dispatchedRuns,
                finalStatuses);

            var finalStatus =
                finalStatuses.Single();

            AssertCompletedRuntimeStatus(
                finalStatus);

            var executionId =
                finalStatus.ExecutionId ??
                finalStatus.RunState?.ExecutionId;

            Assert.False(
                string.IsNullOrWhiteSpace(executionId));

            var ledgerEntries =
                await mcp.GetLedgerByExecutionAsync(
                        executionId!)
                    .ConfigureAwait(false);

            var traceEvents =
                await mcp.GetTraceByExecutionAsync(
                        executionId!)
                    .ConfigureAwait(false);

            var metricsStatus =
                await mcp.GetMetricsStatusAsync()
                    .ConfigureAwait(false);

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

        /// <summary>
        /// Verifies that replay returns a report, ledger, and timeline for a completed execution.
        /// </summary>
        [Fact]
        public async Task Submit_One_Run_With_100_Step_Pipeline_Then_Replay_Should_Return_Report_Ledger_And_Trace()
        {
            var pipelineName =
                $"mcp-test-pipeline-{Guid.NewGuid():N}";

            var scenarioName =
                nameof(Submit_One_Run_With_100_Step_Pipeline_Then_Replay_Should_Return_Report_Ledger_And_Trace);

            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 100,
                    flakyStepInterval: 10);

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                        submitRequest,
                        count: 1)
                    .ConfigureAwait(false);

            Assert.Single(
                submitResults);

            Assert.All(
                submitResults,
                result => Assert.True(
                    result.Success,
                    result.FailureReason ?? result.Message));

            var drainResult =
                await DrainPipelineQueueAsync(
                        pipelineName,
                        maxDispatches: 1,
                        reason: "MCP integration test manual pipeline drain.")
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

            var finalStatuses =
                await McpTestWaitHelpers.WaitForTerminalRuntimeRunStatusesAsync(
                        mcp,
                        dispatchedRuns,
                        timeout: TimeSpan.FromMinutes(2))
                    .ConfigureAwait(false);

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

            var replayRequest =
                new AiReplayControlRequest
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
                        replayRequest)
                    .ConfigureAwait(false);

            replayRequest.Operation = AiReplayOperation.GetReport;

            var replayReport =
                await mcp.GetReplayReportAsync(
                        replayRequest)
                    .ConfigureAwait(false);

            replayRequest.Operation = AiReplayOperation.GetLedger;

            var replayLedger =
                await mcp.GetReplayLedgerAsync(
                        replayRequest)
                    .ConfigureAwait(false);

            replayRequest.Operation = AiReplayOperation.GetTimeline;

            var replayTrace =
                await mcp.GetReplayTraceAsync(
                        replayRequest)
                    .ConfigureAwait(false);

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

        /// <summary>
        /// Verifies that a long-running execution can be paused, resumed, and completed.
        /// </summary>
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
                        count: 1)
                    .ConfigureAwait(false);

            Assert.Single(
                submitResults);

            Assert.True(
                submitResults[0].Success,
                submitResults[0].FailureReason ?? submitResults[0].Message);

            var drainResult =
                await DrainPipelineQueueAsync(
                        pipelineName,
                        maxDispatches: 1,
                        reason: "MCP integration test manual pipeline drain.")
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

            var run =
                dispatchedRuns.Single();

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
                            Reason = "MCP integration test execution pause.",
                            RequestedBy = RequestedBy,
                            Source = Source
                        })
                    .ConfigureAwait(false);

            Assert.True(
                pauseResult.Success,
                pauseResult.FailureReason ?? pauseResult.Message);

            var pausedStatus =
                await WaitForExecutionControlStatusAsync(
                        executionId!,
                        timeout: TimeSpan.FromSeconds(10),
                        expectedStatuses:
                        [
                            "Paused"
                        ])
                    .ConfigureAwait(false);

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
                        })
                    .ConfigureAwait(false);

            Assert.True(
                resumeResult.Success,
                resumeResult.FailureReason ?? resumeResult.Message);

            var resumedStatus =
                await WaitForExecutionControlStatusAsync(
                        executionId!,
                        timeout: TimeSpan.FromSeconds(10),
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
                        timeout: TimeSpan.FromMinutes(2))
                    .ConfigureAwait(false);

            McpScenarioOutput.WriteRuntimeRunStatusSummary(
                output,
                scenarioName,
                pipelineName,
                dispatchedRuns,
                finalStatuses);

            var finalStatus =
                finalStatuses.Single();

            AssertCompletedRuntimeStatus(
                finalStatus);
        }

        /// <summary>
        /// Verifies that a queued run can be cancelled without creating an execution.
        /// </summary>
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
                        "MCP integration test queued-run cancel setup.")
                    .ConfigureAwait(false);

            try
            {
                var submitRequest =
                    CreateSubmitRequest(
                        pipelineName,
                        stepCount: 50,
                        flakyStepInterval: 10);

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
                    await DrainPipelineQueueAsync(
                            pipelineName,
                            maxDispatches: 1,
                            reason: "MCP integration test manual pipeline drain.")
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

                var run =
                    dispatchedRuns.Single();

                var statusBeforeCancel =
                    await McpTestWaitHelpers.WaitForRuntimeRunStatusAsync(
                            mcp,
                            run,
                            expectedStatus: "queued",
                            timeout: TimeSpan.FromSeconds(10))
                        .ConfigureAwait(false);

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
                            })
                        .ConfigureAwait(false);

                Assert.True(
                    cancelResult.Success,
                    cancelResult.FailureReason ?? cancelResult.Message);

                var statusAfterCancel =
                    await McpTestWaitHelpers.WaitForRuntimeRunStatusAsync(
                            mcp,
                            run,
                            expectedStatus: "cancelled",
                            timeout: TimeSpan.FromSeconds(10))
                        .ConfigureAwait(false);

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
                        "MCP integration test cleanup resume.")
                    .ConfigureAwait(false);
            }

            await Task.Delay(500)
                .ConfigureAwait(false);

            var finalRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                        mcp,
                        pipelineName,
                        expectedCount: 1,
                        timeout: TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);

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
                        })
                    .ConfigureAwait(false);

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

        /// <summary>
        /// Verifies that a long-running execution can receive a cancellation request.
        /// </summary>
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
                        count: 1)
                    .ConfigureAwait(false);

            Assert.Single(
                submitResults);

            Assert.True(
                submitResults[0].Success,
                submitResults[0].FailureReason ?? submitResults[0].Message);

            var drainResult =
                await DrainPipelineQueueAsync(
                        pipelineName,
                        maxDispatches: 1,
                        reason: "MCP integration test manual pipeline drain.")
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
                            Reason = "MCP integration test execution cancellation request.",
                            RequestedBy = RequestedBy,
                            Source = Source
                        })
                    .ConfigureAwait(false);

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
                        })
                    .ConfigureAwait(false);

            Assert.True(
                cancellingStatus.Success,
                cancellingStatus.FailureReason ?? cancellingStatus.Message);

            var cancellationStatus =
                cancellingStatus.State?.Status.ToString();

            Assert.Contains(
                cancellationStatus,
                new[]
                {
                    "Cancelling",
                    "Cancelled"
                });

            McpScenarioOutput.WriteExecutionControlSummary(
                output,
                $"{scenarioName}_ExecutionCancelling",
                executionId!,
                cancelResult,
                cancellingStatus,
                resumeResult: null,
                resumedStatus: null);
        }

        /// <summary>
        /// Verifies that runtime instance tools do not return runtime instances when
        /// the local runtime instance pool is disabled.
        /// </summary>
        [Fact]
        public async Task Runtime_Instance_Tools_Should_Return_Empty_List_When_No_Instance_Is_Registered()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "shared-run-empty-runtime-registry");

            await using var emptyHost =
                new GenericMcpServerTestHost(
                    CreateControlPlaneWithoutRuntimeInstancesSettings(
                        controlPlaneId));

            using var emptyClient =
                emptyHost.CreateClient();

            var emptyMcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        emptyHost,
                        emptyClient,
                        RequestedBy,
                        tenantId: TenantId)
                    .ConfigureAwait(false);

            Console.WriteLine(
                $"[SHARED RUN EMPTY RUNTIME TEST] Configured MCP RBAC headers. UserId='{RequestedBy}', TenantId='{TenantId}'.");

            var allInstances =
                await emptyMcp.ListRuntimeInstancesAsync(
                        includeStopped: true)
                    .ConfigureAwait(false);

            var activeInstances =
                await emptyMcp.ListActiveRuntimeInstancesAsync()
                    .ConfigureAwait(false);

            Assert.NotNull(
                allInstances);

            Assert.NotNull(
                activeInstances);

            Assert.DoesNotContain(
                allInstances,
                instance => instance.Role == AiRuntimeInstanceRole.Runtime);

            Assert.DoesNotContain(
                activeInstances,
                instance => instance.Role == AiRuntimeInstanceRole.Runtime);

            McpScenarioOutput.WriteRuntimeInstanceSummary(
                output,
                nameof(Runtime_Instance_Tools_Should_Return_Empty_List_When_No_Instance_Is_Registered),
                allInstances,
                activeInstances,
                selectedStatus: null);
        }

        /// <summary>
        /// Verifies that submitted runs are visible in shared run and shared queue status without manual drain.
        /// </summary>
        [Fact]
        public async Task Submit_Five_Runs_Without_Manual_Drain_Should_Show_Shared_Queue_And_Shared_Run_Status()
        {
            var pipelineName =
                $"mcp-test-pipeline-{Guid.NewGuid():N}";

            var scenarioName =
                nameof(Submit_Five_Runs_Without_Manual_Drain_Should_Show_Shared_Queue_And_Shared_Run_Status);

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

            var sharedRuns =
                await ListAllSharedRunsAsync()
                    .ConfigureAwait(false);

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
                        includeTerminal: true)
                    .ConfigureAwait(false);

            var status =
                await mcp.GetSharedQueueStatusAsync(
                        includeTerminal: true)
                    .ConfigureAwait(false);

            McpScenarioOutput.WriteSharedQueueSummary(
                output,
                scenarioName,
                pipelineName,
                queueItems,
                status);

            Assert.NotNull(
                queueItems);

            Assert.NotNull(
                status);
        }

        /// <summary>
        /// Verifies that queue activity still reports submitted runs even when the active queue is empty.
        /// </summary>
        [Fact]
        public async Task Submit_Five_Runs_Should_Show_Shared_Queue_Activity_Even_When_Active_Queue_Is_Empty()
        {
            var pipelineName =
                $"mcp-test-pipeline-{Guid.NewGuid():N}";

            var scenarioName =
                nameof(Submit_Five_Runs_Should_Show_Shared_Queue_Activity_Even_When_Active_Queue_Is_Empty);

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

        /// <summary>
        /// Creates default local control-plane settings for shared run scenarios.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <returns>The default local control-plane settings.</returns>
        private static Dictionary<string, string?> CreateDefaultLocalControlPlaneSettings(
            string controlPlaneId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                controlPlaneId);

            var controlPlaneRuntimeInstanceId =
                $"mcp-control-plane-local-{Guid.NewGuid():N}";

            return GenericMcpServerTestSettings.CreateMcpSettings(
                controlPlaneId,
                new Dictionary<string, string?>
                {
                    ["AiMcpHost:Mode"] = "ControlPlaneWithLocalRuntimeInstances",
                    ["AiMcpHost:EnableSharedQueuePump"] = "false",

                    ["AiSharedQueueBackgroundService:Enabled"] = "false",
                    ["AiSharedQueueBackgroundService:WaitForRuntimeReadiness"] = "false",
                    ["AiSharedQueuePump:Enabled"] = "true",
                    ["AiSharedRuntimeController:SubmitMode"] = "QueueFirst",

                    ["AiRuntimeInstanceRegistration:ControlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:ProviderName"] = "local",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "local",
                    ["AiRuntimeInstanceRegistration:Metadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "local",
                    ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "control-plane-with-local-runtime",
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-shared-run-scenario",

                    ["AiLocalRuntimeInstancePool:Enabled"] = "true",
                    ["AiLocalRuntimeInstancePool:InstanceCount"] = "3",
                    ["AiLocalRuntimeInstancePool:WorkerCountPerInstance"] = "10",
                    ["AiLocalRuntimeInstancePool:MaxConcurrentRunsPerInstance"] = "5",
                    ["AiLocalRuntimeInstancePool:RuntimeInstanceIdPrefix"] = "mcp-runtime",

                    ["AiEngine:ControlPlane:ControlPlaneId"] = controlPlaneId,
                    ["AiEngine:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiEngine:PipelineBackgroundController:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiEngine:PipelineBackgroundController:MaxConcurrentRuns"] = "5",
                    ["AiEngine:PipelineBackgroundController:QueueCapacity"] = "500",
                    ["AiEngine:PipelineBackgroundController:Distributed:Enabled"] = "true",
                    ["AiEngine:PipelineBackgroundController:Distributed:WorkerCount"] = "10",
                    ["AiEngine:PipelineBackgroundController:MaxLocalWorkersPerExecution"] = "5",
                    ["AiEngine:RuntimeInstanceWorker:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,

                    ["AiRuntimeInstanceRegistration:HeartbeatInterval"] = "00:00:02",
                    ["AiRuntimeInstanceRegistration:RegistryTtl"] = "00:00:30",
                    ["AiRuntimeInstanceRegistration:CapacityTtl"] = "00:00:30"
                });
        }

        /// <summary>
        /// Creates control-plane settings without dispatchable runtime instances.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <returns>The control-plane settings without a local runtime instance pool.</returns>
        private static Dictionary<string, string?> CreateControlPlaneWithoutRuntimeInstancesSettings(
            string controlPlaneId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                controlPlaneId);

            var controlPlaneRuntimeInstanceId =
                $"mcp-control-plane-empty-{Guid.NewGuid():N}";

            return GenericMcpServerTestSettings.CreateMcpSettings(
                controlPlaneId,
                new Dictionary<string, string?>
                {
                    ["AiMcpHost:Mode"] = "ControlPlaneWithLocalRuntimeInstances",
                    ["AiMcpHost:EnableSharedQueuePump"] = "false",

                    ["AiSharedQueueBackgroundService:Enabled"] = "false",
                    ["AiSharedQueueBackgroundService:WaitForRuntimeReadiness"] = "false",
                    ["AiSharedQueuePump:Enabled"] = "true",
                    ["AiSharedRuntimeController:SubmitMode"] = "QueueFirst",

                    ["AiRuntimeInstanceRegistration:ControlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:Role"] = "ControlPlane",
                    ["AiRuntimeInstanceRegistration:ProviderName"] = "local",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "local",
                    ["AiRuntimeInstanceRegistration:Metadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "local",
                    ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "control-plane-without-runtime",
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-shared-run-empty-runtime-registry",

                    ["AiLocalRuntimeInstancePool:Enabled"] = "false",
                    ["AiLocalRuntimeInstancePool:InstanceCount"] = "0",
                    ["AiLocalRuntimeInstancePool:WorkerCountPerInstance"] = "0",
                    ["AiLocalRuntimeInstancePool:MaxConcurrentRunsPerInstance"] = "0",
                    ["AiLocalRuntimeInstancePool:RuntimeInstanceIdPrefix"] = "mcp-runtime-empty",

                    ["AiEngine:ControlPlane:ControlPlaneId"] = controlPlaneId,
                    ["AiEngine:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiEngine:PipelineBackgroundController:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiEngine:PipelineBackgroundController:MaxConcurrentRuns"] = "1",
                    ["AiEngine:PipelineBackgroundController:QueueCapacity"] = "10",
                    ["AiEngine:PipelineBackgroundController:Distributed:Enabled"] = "false",
                    ["AiEngine:PipelineBackgroundController:Distributed:WorkerCount"] = "0",
                    ["AiEngine:PipelineBackgroundController:MaxLocalWorkersPerExecution"] = "1",
                    ["AiEngine:RuntimeInstanceWorker:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId
                });
        }

        /// <summary>
        /// Creates a shared runtime controller submit request.
        /// </summary>
        /// <param name="pipelineName">The pipeline key.</param>
        /// <param name="stepCount">The number of pipeline steps.</param>
        /// <param name="flakyStepInterval">The flaky step interval.</param>
        /// <returns>The submit request.</returns>
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
        /// Lists all shared runs including terminal states.
        /// </summary>
        /// <returns>The shared runtime controller list result.</returns>
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
                    })
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Drains queued shared runs for a specific pipeline.
        /// </summary>
        /// <param name="pipelineName">The pipeline key.</param>
        /// <param name="maxDispatches">The maximum number of dispatches.</param>
        /// <param name="reason">The drain reason.</param>
        /// <returns>The pump result.</returns>
        private async Task<AiSharedQueuePumpResult> DrainPipelineQueueAsync(
            string pipelineName,
            int maxDispatches,
            string reason)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                pipelineName);

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
                    })
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Pauses all currently available runtime queues.
        /// </summary>
        /// <param name="mcp">The MCP test client.</param>
        /// <param name="reason">The pause reason.</param>
        /// <returns>The paused runtime instance ids.</returns>
        private static async Task<IReadOnlyList<string>> PauseRuntimeQueuesAsync(
            McpTestClient mcp,
            string reason)
        {
            var instances =
                await mcp.ListRuntimeInstancesAsync(
                        includeStopped: false)
                    .ConfigureAwait(false);

            var runtimeInstanceIds =
                instances
                    .Where(instance => instance.Role == AiRuntimeInstanceRole.Runtime)
                    .Where(instance => instance.CanAcceptRun)
                    .Select(instance => instance.RuntimeInstanceId)
                    .Where(runtimeInstanceId => !string.IsNullOrWhiteSpace(runtimeInstanceId))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

            Assert.NotEmpty(
                runtimeInstanceIds);

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
                            })
                        .ConfigureAwait(false);

                Assert.True(
                    pauseResult.Success,
                    pauseResult.FailureReason ?? pauseResult.Message);
            }

            return runtimeInstanceIds;
        }

        /// <summary>
        /// Resumes runtime queues on a best-effort basis during test cleanup.
        /// </summary>
        /// <param name="mcp">The MCP test client.</param>
        /// <param name="runtimeInstanceIds">The runtime instance ids to resume.</param>
        /// <param name="reason">The cleanup reason.</param>
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

        /// <summary>
        /// Waits until an execution control state reaches one of the expected statuses.
        /// </summary>
        /// <param name="targetExecutionId">The execution id to inspect.</param>
        /// <param name="timeout">The maximum wait duration.</param>
        /// <param name="expectedStatuses">The expected control statuses.</param>
        /// <returns>The matching execution control result.</returns>
        private async Task<AiExecutionControlPlaneResult> WaitForExecutionControlStatusAsync(
            string targetExecutionId,
            TimeSpan timeout,
            IReadOnlyCollection<string> expectedStatuses)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                targetExecutionId);

            ArgumentNullException.ThrowIfNull(
                expectedStatuses);

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
                            })
                        .ConfigureAwait(false);

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
                        TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
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

        /// <summary>
        /// Verifies that all dispatched shared runs have a runtime instance and local run id.
        /// </summary>
        /// <param name="dispatchedRuns">The dispatched shared runs.</param>
        /// <param name="expectedCount">The expected dispatched run count.</param>
        private static void AssertDispatchedRuns(
            IReadOnlyList<AiSharedRunRecord> dispatchedRuns,
            int expectedCount)
        {
            Assert.Equal(
                expectedCount,
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
        }

        /// <summary>
        /// Verifies that all runtime status results completed successfully.
        /// </summary>
        /// <param name="statuses">The runtime run status results.</param>
        private static void AssertTerminalCompletedStatuses(
            IReadOnlyList<AiRuntimeQueueControlPlaneResult> statuses)
        {
            Assert.All(
                statuses,
                AssertCompletedRuntimeStatus);
        }

        /// <summary>
        /// Verifies that one runtime status result completed successfully.
        /// </summary>
        /// <param name="status">The runtime run status result.</param>
        private static void AssertCompletedRuntimeStatus(
            AiRuntimeQueueControlPlaneResult status)
        {
            Assert.True(
                status.Success,
                status.FailureReason ?? status.Message);

            Assert.Equal(
                "completed",
                status.RunState?.Status);

            Assert.False(
                string.IsNullOrWhiteSpace(status.ExecutionId ?? status.RunState?.ExecutionId),
                $"RunId='{status.RunId}' failed to expose an ExecutionId. " +
                $"Status='{status.RunState?.Status}', " +
                $"FailureReason='{status.FailureReason ?? status.RunState?.FailureReason}', " +
                $"Message='{status.Message}'.");
        }
    }
}
