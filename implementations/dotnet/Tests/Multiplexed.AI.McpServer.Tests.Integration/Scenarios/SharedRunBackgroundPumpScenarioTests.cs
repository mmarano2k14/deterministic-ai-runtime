using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios
{
    /// <summary>
    /// Contains integration tests for shared queue submission and background pumping.
    /// </summary>
    public sealed class SharedRunBackgroundPumpScenarioTests
    {
        private const string RequestedBy = "mcp-background-pump-integration-test";
        private const string Source = "mcp-background-pump-test";
        private const string TenantId = "test-tenant";

        private readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance of the <see cref="SharedRunBackgroundPumpScenarioTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public SharedRunBackgroundPumpScenarioTests(
            ITestOutputHelper output)
        {
            this.output =
                output;
        }

        /// <summary>
        /// Verifies that local submission is added to the shared queue when queue-first mode
        /// is enabled and the background pump is disabled.
        /// </summary>
        [Fact]
        public async Task Submit_Local_Run_With_Queue_First_And_Pump_Disabled_Should_Add_Run_To_Shared_Queue()
        {
            var controlPlaneSettings =
                CreateLocalControlPlaneSettings(
                    enablePump: false,
                    queueFirst: true);

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            using var client =
                host.CreateClient();

            var mcp =
                new McpTestClient(
                    client);

            var pipelineName =
                $"mcp-queue-first-local-{Guid.NewGuid():N}";

            await SubmitRunsAsync(
                    mcp,
                    pipelineName,
                    count: 1)
                .ConfigureAwait(false);

            await AssertSharedQueueContainsPipelineAsync(
                    mcp,
                    pipelineName)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies that HTTP submission is added to the shared queue when queue-first mode
        /// is enabled and the background pump is disabled.
        /// </summary>
        [Fact]
        public async Task Submit_Http_Run_With_Queue_First_And_Pump_Disabled_Should_Add_Run_To_Shared_Queue()
        {
            var controlPlaneSettings =
                CreateHttpControlPlaneSettings(
                    enablePump: false,
                    queueFirst: true);

            var runtimeInstanceSettings =
                CreateHttpRuntimeInstanceSettings();

            await using var fixture =
                new GenericMcpRuntimeFixture(
                    controlPlaneSettings,
                    runtimeInstanceSettings);

            await fixture
                .InitializeAsync()
                .ConfigureAwait(false);

            var pipelineName =
                $"mcp-queue-first-http-{Guid.NewGuid():N}";

            await SubmitRunsAsync(
                    fixture.Mcp,
                    pipelineName,
                    count: 1)
                .ConfigureAwait(false);

            await AssertSharedQueueContainsPipelineAsync(
                    fixture.Mcp,
                    pipelineName)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies that a local run submitted through the shared queue is automatically
        /// dispatched and completed by the background pump.
        /// </summary>
        [Fact]
        public async Task Submit_One_Local_Run_With_Queue_First_Should_Dispatch_And_Complete_Through_Background_Pump()
        {
            var controlPlaneSettings =
                CreateLocalControlPlaneSettings(
                    enablePump: true,
                    queueFirst: true);

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            using var client =
                host.CreateClient();

            var mcp =
                new McpTestClient(
                    client);

            var pipelineName =
                $"mcp-background-pump-local-one-{Guid.NewGuid():N}";

            await SubmitRunsAsync(
                    mcp,
                    pipelineName,
                    count: 1)
                .ConfigureAwait(false);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                        mcp,
                        pipelineName,
                        expectedCount: 1,
                        timeout: TimeSpan.FromMinutes(1))
                    .ConfigureAwait(false);

            AssertLocalDispatchedRuns(
                dispatchedRuns,
                expectedCount: 1);

            await AssertRunsCompleteAsync(
                    mcp,
                    dispatchedRuns,
                    expectedCount: 1)
                .ConfigureAwait(false);

            output.WriteLine(
                $"Local shared queue pump dispatched and completed one run. PipelineKey='{pipelineName}'.");
        }

        /// <summary>
        /// Verifies that an HTTP run submitted through the shared queue is automatically
        /// dispatched and completed by the background pump.
        /// </summary>
        [Fact]
        public async Task Submit_One_Http_Run_With_Queue_First_Should_Dispatch_And_Complete_Through_Background_Pump()
        {
            var controlPlaneSettings =
                CreateHttpControlPlaneSettings(
                    enablePump: true,
                    queueFirst: true);

            var runtimeInstanceSettings =
                CreateHttpRuntimeInstanceSettings();

            await using var fixture =
                new GenericMcpRuntimeFixture(
                    controlPlaneSettings,
                    runtimeInstanceSettings);

            await fixture
                .InitializeAsync()
                .ConfigureAwait(false);

            var pipelineName =
                $"mcp-background-pump-http-one-{Guid.NewGuid():N}";

            await SubmitRunsAsync(
                    fixture.Mcp,
                    pipelineName,
                    count: 1)
                .ConfigureAwait(false);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                        fixture.Mcp,
                        pipelineName,
                        expectedCount: 1,
                        timeout: TimeSpan.FromMinutes(1))
                    .ConfigureAwait(false);

            AssertHttpDispatchedRuns(
                dispatchedRuns,
                expectedCount: 1);

            await AssertRunsCompleteAsync(
                    fixture.Mcp,
                    dispatchedRuns,
                    expectedCount: 1)
                .ConfigureAwait(false);

            output.WriteLine(
                $"HTTP shared queue pump dispatched and completed one run. PipelineKey='{pipelineName}'.");
        }

        /// <summary>
        /// Verifies that several local runs submitted through the shared queue are
        /// automatically dispatched and completed by the background pump.
        /// </summary>
        [Fact]
        public async Task Submit_Multiple_Local_Runs_With_Queue_First_Should_Dispatch_And_Complete_Through_Background_Pump()
        {
            var controlPlaneSettings =
                CreateLocalControlPlaneSettings(
                    enablePump: true,
                    queueFirst: true);

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            using var client =
                host.CreateClient();

            var mcp =
                new McpTestClient(
                    client);

            var pipelineName =
                $"mcp-background-pump-local-many-{Guid.NewGuid():N}";

            await SubmitRunsAsync(
                    mcp,
                    pipelineName,
                    count: 4)
                .ConfigureAwait(false);

            var queueItems = await mcp.ListSharedQueueAsync(includeTerminal: true);

            output.WriteLine(
                $"QueueItems='{queueItems.Count}', Matching='{queueItems.Count(x => x.PipelineKey == pipelineName)}'");

            var runs = await mcp.ListSharedRunsAsync(
                new AiSharedRuntimeControllerRequest
                {
                    Operation = AiSharedRuntimeControllerOperation.ListRuns,
                    IncludeCompleted = true,
                    IncludeFailed = true,
                    IncludeCancelled = true,
                    RequestedBy = RequestedBy,
                    Source = Source
                });

            foreach (var run in runs.Runs.Where(x => x.PipelineKey == pipelineName))
            {
                output.WriteLine(
                    $"Run SharedRunId='{run.SharedRunId}' Status='{run.Status}' Assigned='{run.AssignedRuntimeInstanceId}' LocalRunId='{run.LocalRunId}'");
            }

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                        mcp,
                        pipelineName,
                        expectedCount: 4,
                        timeout: TimeSpan.FromMinutes(1))
                    .ConfigureAwait(false);

            AssertLocalDispatchedRuns(
                dispatchedRuns,
                expectedCount: 4);

            await AssertRunsCompleteAsync(
                    mcp,
                    dispatchedRuns,
                    expectedCount: 4)
                .ConfigureAwait(false);

            output.WriteLine(
                $"Local shared queue pump dispatched and completed multiple runs. PipelineKey='{pipelineName}', Count='{dispatchedRuns.Count}'.");
        }

        /// <summary>
        /// Verifies that several HTTP runs submitted through the shared queue are
        /// automatically dispatched and completed by the background pump.
        /// </summary>
        [Fact]
        public async Task Submit_Multiple_Http_Runs_With_Queue_First_Should_Dispatch_And_Complete_Through_Background_Pump()
        {
            var controlPlaneSettings =
                CreateHttpControlPlaneSettings(
                    enablePump: true,
                    queueFirst: true);

            var runtimeInstanceSettings =
                CreateHttpRuntimeInstanceSettings();

            await using var fixture =
                new GenericMcpRuntimeFixture(
                    controlPlaneSettings,
                    runtimeInstanceSettings);

            await fixture
                .InitializeAsync()
                .ConfigureAwait(false);

            var pipelineName =
                $"mcp-background-pump-http-many-{Guid.NewGuid():N}";

            await SubmitRunsAsync(
                    fixture.Mcp,
                    pipelineName,
                    count: 4)
                .ConfigureAwait(false);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                        fixture.Mcp,
                        pipelineName,
                        expectedCount: 4,
                        timeout: TimeSpan.FromMinutes(1))
                    .ConfigureAwait(false);

            AssertHttpDispatchedRuns(
                dispatchedRuns,
                expectedCount: 4);

            await AssertRunsCompleteAsync(
                    fixture.Mcp,
                    dispatchedRuns,
                    expectedCount: 4)
                .ConfigureAwait(false);

            output.WriteLine(
                $"HTTP shared queue pump dispatched and completed multiple runs. PipelineKey='{pipelineName}', RuntimeInstanceId='runtime-http-1', Count='{dispatchedRuns.Count}'.");
        }

        /// <summary>
        /// Creates local control-plane settings for shared queue pump scenarios.
        /// </summary>
        /// <param name="enablePump">Whether the shared queue background pump should be enabled.</param>
        /// <param name="queueFirst">Whether submitted runs should be enqueued into the shared queue before dispatch.</param>
        /// <returns>The control-plane settings.</returns>
        private static Dictionary<string, string?> CreateLocalControlPlaneSettings(
            bool enablePump,
            bool queueFirst)
        {
            return GenericMcpServerTestSettings.CreateMcpSettings(
                new Dictionary<string, string?>
                {
                    ["AiMcpHost:Mode"] = "ControlPlaneWithLocalRuntimeInstances",
                    ["AiMcpHost:EnableSharedQueuePump"] = enablePump.ToString(),
                    ["AiSharedQueueBackgroundService:Enabled"] = enablePump.ToString(),
                    ["AiSharedQueuePump:Enabled"] = enablePump.ToString(),
                    ["AiSharedRuntimeController:SubmitMode"] = queueFirst
                        ? "QueueFirst"
                        : "DirectDispatch",
                    ["AiRuntimeInstanceRegistration:ProviderName"] = "local",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "local",
                    ["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "local",
                    ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = "mcp-control-plane-local",
                    ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "control-plane-with-local-runtime",
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-local",
                    ["AiLocalRuntimeInstancePool:Enabled"] = "true",
                    ["AiLocalRuntimeInstancePool:InstanceCount"] = "3",
                    ["AiLocalRuntimeInstancePool:WorkerCountPerInstance"] = "10",
                    ["AiLocalRuntimeInstancePool:MaxConcurrentRunsPerInstance"] = "5",
                    ["AiLocalRuntimeInstancePool:RuntimeInstanceIdPrefix"] = "mcp-runtime",
                    ["AiEngine:RuntimeInstanceId"] = "mcp-control-plane-local"
                });
        }

        /// <summary>
        /// Creates HTTP control-plane settings for shared queue pump scenarios.
        /// </summary>
        /// <param name="enablePump">Whether the shared queue background pump should be enabled.</param>
        /// <param name="queueFirst">Whether submitted runs should be enqueued into the shared queue before dispatch.</param>
        /// <returns>The control-plane settings.</returns>
        private static Dictionary<string, string?> CreateHttpControlPlaneSettings(
            bool enablePump,
            bool queueFirst)
        {
            return GenericMcpServerTestSettings.CreateMcpSettings(
                new Dictionary<string, string?>
                {
                    ["AiMcpHost:Mode"] = "ControlPlaneWithHttpRuntimeInstances",
                    ["AiMcpHost:EnableSharedQueuePump"] = enablePump.ToString(),
                    ["AiSharedQueueBackgroundService:Enabled"] = enablePump.ToString(),
                    ["AiSharedQueuePump:Enabled"] = enablePump.ToString(),
                    ["AiSharedRuntimeController:SubmitMode"] = queueFirst
                        ? "QueueFirst"
                        : "DirectDispatch"
                });
        }

        /// <summary>
        /// Creates HTTP runtime-instance settings for shared queue pump scenarios.
        /// </summary>
        /// <returns>The HTTP runtime-instance settings.</returns>
        private static Dictionary<string, string?> CreateHttpRuntimeInstanceSettings()
        {
            return GenericMcpServerTestSettings.CreateRuntimeInstanceSettings(
                new Dictionary<string, string?>
                {
                    ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = "runtime-http-1",
                    ["AiRuntimeInstanceRegistration:ProviderName"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:transport.endpoint"] = "http://localhost",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:runtime.instance.id"] = "runtime-http-1",
                    ["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:transport.endpoint"] = "http://localhost",
                    ["AiRuntimeInstanceRegistration:Metadata:runtime.instance.id"] = "runtime-http-1",
                    ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "runtime-instance-only",
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-http",
                    ["AiEngine:RuntimeInstanceId"] = "runtime-http-1",
                    ["AiEngine:PipelineBackgroundController:RuntimeInstanceId"] = "runtime-http-1",
                    ["AiEngine:RuntimeInstanceWorker:RuntimeInstanceId"] = "runtime-http-1"
                });
        }

        /// <summary>
        /// Submits a number of shared runtime runs for the specified pipeline.
        /// </summary>
        /// <param name="mcp">The MCP test client.</param>
        /// <param name="pipelineName">The pipeline key.</param>
        /// <param name="count">The number of runs to submit.</param>
        private static async Task SubmitRunsAsync(
            McpTestClient mcp,
            string pipelineName,
            int count)
        {
            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: 20,
                    flakyStepInterval: 0);

            var submitResults =
                await mcp.SubmitManyRunsAsync(
                        submitRequest,
                        count)
                    .ConfigureAwait(false);

            Assert.Equal(
                count,
                submitResults.Count);

            Assert.All(
                submitResults,
                result => Assert.True(
                    result.Success,
                    result.FailureReason ?? result.Message));
        }

        /// <summary>
        /// Verifies that the shared queue contains an item for the specified pipeline.
        /// </summary>
        /// <param name="mcp">The MCP test client.</param>
        /// <param name="pipelineName">The pipeline key.</param>
        private static async Task AssertSharedQueueContainsPipelineAsync(
            McpTestClient mcp,
            string pipelineName)
        {
            var queueItems =
                await mcp.ListSharedQueueAsync(
                        includeTerminal: true)
                    .ConfigureAwait(false);

            var queueItem =
                Assert.Single(
                    queueItems,
                    item => string.Equals(
                        item.PipelineKey,
                        pipelineName,
                        StringComparison.Ordinal));

            Assert.False(
                string.IsNullOrWhiteSpace(queueItem.SharedRunId));
        }

        /// <summary>
        /// Verifies that local shared runs were dispatched to local runtime instances.
        /// </summary>
        /// <param name="dispatchedRuns">The dispatched shared runs.</param>
        /// <param name="expectedCount">The expected number of dispatched runs.</param>
        private static void AssertLocalDispatchedRuns(
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

                    Assert.StartsWith(
                        "mcp-runtime-",
                        run.AssignedRuntimeInstanceId,
                        StringComparison.Ordinal);

                    Assert.False(
                        string.IsNullOrWhiteSpace(run.LocalRunId));
                });

            Assert.Equal(
                expectedCount,
                dispatchedRuns
                    .Select(run => run.LocalRunId)
                    .Distinct(StringComparer.Ordinal)
                    .Count());
        }

        /// <summary>
        /// Verifies that HTTP shared runs were dispatched to the HTTP runtime instance.
        /// </summary>
        /// <param name="dispatchedRuns">The dispatched shared runs.</param>
        /// <param name="expectedCount">The expected number of dispatched runs.</param>
        private static void AssertHttpDispatchedRuns(
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
                    Assert.Equal(
                        "runtime-http-1",
                        run.AssignedRuntimeInstanceId);

                    Assert.False(
                        string.IsNullOrWhiteSpace(run.LocalRunId));
                });

            Assert.Equal(
                expectedCount,
                dispatchedRuns
                    .Select(run => run.LocalRunId)
                    .Distinct(StringComparer.Ordinal)
                    .Count());
        }

        /// <summary>
        /// Verifies that dispatched runtime runs reach a terminal completed state.
        /// </summary>
        /// <param name="mcp">The MCP test client.</param>
        /// <param name="dispatchedRuns">The dispatched shared runs.</param>
        /// <param name="expectedCount">The expected number of completed runs.</param>
        private static async Task AssertRunsCompleteAsync(
            McpTestClient mcp,
            IReadOnlyList<AiSharedRunRecord> dispatchedRuns,
            int expectedCount)
        {
            var finalStatuses =
                await McpTestWaitHelpers.WaitForTerminalRuntimeRunStatusesAsync(
                        mcp,
                        dispatchedRuns,
                        timeout: TimeSpan.FromMinutes(2))
                    .ConfigureAwait(false);

            Assert.Equal(
                expectedCount,
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
                        string.IsNullOrWhiteSpace(
                            status.ExecutionId ?? status.RunState?.ExecutionId));
                });
        }

        /// <summary>
        /// Verifies that a local queue-first run remains queued while the background
        /// pump is disabled, then dispatches and completes after manual drain.
        /// </summary>
        [Fact]
        public async Task Submit_Local_Run_With_Queue_First_And_Pump_Disabled_Should_Dispatch_After_Manual_Drain()
        {
            var controlPlaneSettings =
               GenericMcpServerTestSettings.CreateMcpSettings(
                   new Dictionary<string, string?>
                   {
                       ["AiMcpHost:Mode"] = "ControlPlaneWithLocalRuntimeInstances",
                       ["AiSharedQueuePump:Enabled"] = "true",
                       ["AiMcpHost:EnableSharedQueuePump"] = "false",
                       ["AiSharedQueueBackgroundService:Enabled"] = "false",
                       ["AiSharedRuntimeController:SubmitMode"] = "QueueFirst",

                       ["AiRuntimeInstanceRegistration:ProviderName"] = "local",
                       ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "local",
                       ["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "local",
                       ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = "mcp-control-plane-local",
                       ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "control-plane-with-local-runtime",
                       ["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-local",

                       ["AiLocalRuntimeInstancePool:Enabled"] = "true",
                       ["AiLocalRuntimeInstancePool:InstanceCount"] = "3",
                       ["AiLocalRuntimeInstancePool:WorkerCountPerInstance"] = "10",
                       ["AiLocalRuntimeInstancePool:MaxConcurrentRunsPerInstance"] = "5",
                       ["AiLocalRuntimeInstancePool:RuntimeInstanceIdPrefix"] = "mcp-runtime",

                       ["AiEngine:RuntimeInstanceId"] = "mcp-control-plane-local"
                   });

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            using var client =
                host.CreateClient();

            var mcp =
                new McpTestClient(
                    client);

            var pipelineName =
                $"mcp-queue-first-local-manual-drain-{Guid.NewGuid():N}";

            await SubmitRunsAsync(
                    mcp,
                    pipelineName,
                    count: 1)
                .ConfigureAwait(false);

            await AssertSharedQueueContainsPipelineAsync(
                    mcp,
                    pipelineName)
                .ConfigureAwait(false);

            await Task.Delay(
                    TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);

            await AssertSharedQueueContainsPipelineAsync(
                    mcp,
                    pipelineName)
                .ConfigureAwait(false);

            var drainResult =
                 await mcp.DrainQueueAsync(
                         new AiSharedQueuePumpRequest
                         {
                             PumpRuntimeInstanceId = "mcp-runtime-1",
                             PumpWorkerId = "manual-test-drain",
                             MaxDispatches = 10,
                             RequestedBy = RequestedBy,
                             Source = Source,
                             Reason = "Manual test drain."
                         })
                     .ConfigureAwait(false);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason ?? "Drain queue failed.");

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                        mcp,
                        pipelineName,
                        expectedCount: 1,
                        timeout: TimeSpan.FromMinutes(1))
                    .ConfigureAwait(false);

            AssertLocalDispatchedRuns(
                dispatchedRuns,
                expectedCount: 1);

            await AssertRunsCompleteAsync(
                    mcp,
                    dispatchedRuns,
                    expectedCount: 1)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies that an HTTP queue-first run remains queued while the background
        /// pump is disabled, then dispatches and completes after manual drain.
        /// </summary>
        [Fact]
        public async Task Submit_Http_Run_With_Queue_First_And_Pump_Disabled_Should_Dispatch_After_Manual_Drain()
        {
            var controlPlaneSettings =
                 GenericMcpServerTestSettings.CreateMcpSettings(
                     new Dictionary<string, string?>
                     {
                         ["AiMcpHost:Mode"] = "ControlPlaneWithHttpRuntimeInstances",
                         ["AiSharedQueuePump:Enabled"] = "true",
                         ["AiMcpHost:EnableSharedQueuePump"] = "false",
                         ["AiSharedQueueBackgroundService:Enabled"] = "false",
                         ["AiSharedRuntimeController:SubmitMode"] = "QueueFirst",

                         ["AiRuntimeInstanceRegistration:ProviderName"] = "local",
                         ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "local",
                         ["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "local",
                         ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = "mcp-control-plane-http",
                         ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "control-plane-with-http-runtime",
                         ["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-http",

                         ["AiEngine:RuntimeInstanceId"] = "mcp-control-plane-http"
                     });

            var runtimeInstanceSettings =
                CreateHttpRuntimeInstanceSettings();

            await using var fixture =
                new GenericMcpRuntimeFixture(
                    controlPlaneSettings,
                    runtimeInstanceSettings);

            await fixture
                .InitializeAsync()
                .ConfigureAwait(false);

            var pipelineName =
                $"mcp-queue-first-http-manual-drain-{Guid.NewGuid():N}";

            await SubmitRunsAsync(
                    fixture.Mcp,
                    pipelineName,
                    count: 1)
                .ConfigureAwait(false);

            await AssertSharedQueueContainsPipelineAsync(
                    fixture.Mcp,
                    pipelineName)
                .ConfigureAwait(false);

            await Task.Delay(
                    TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);

            await AssertSharedQueueContainsPipelineAsync(
                    fixture.Mcp,
                    pipelineName)
                .ConfigureAwait(false);

            var drainResult =
                await fixture.Mcp.DrainQueueAsync(
                        new AiSharedQueuePumpRequest
                        {
                            PumpRuntimeInstanceId = "runtime-http-1",
                            PumpWorkerId = "manual-test-drain",
                            MaxDispatches = 10,
                            RequestedBy = RequestedBy,
                            Source = Source,
                            Reason = "Manual test drain."
                        })
                    .ConfigureAwait(false);

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason ?? "Drain queue failed.");

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                        fixture.Mcp,
                        pipelineName,
                        expectedCount: 1,
                        timeout: TimeSpan.FromMinutes(1))
                    .ConfigureAwait(false);

            AssertHttpDispatchedRuns(
                dispatchedRuns,
                expectedCount: 1);

            await AssertRunsCompleteAsync(
                    fixture.Mcp,
                    dispatchedRuns,
                    expectedCount: 1)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Creates a shared runtime controller submit request.
        /// </summary>
        /// <param name="pipelineName">The pipeline key.</param>
        /// <param name="stepCount">The number of steps.</param>
        /// <param name="flakyStepInterval">The flaky interval.</param>
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
    }
}
