using System.Text.RegularExpressions;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Development
{
    /// <summary>
    /// Contains integration tests for shared queue submission and background pumping.
    /// </summary>
    /// <remarks>
    /// These scenarios validate queue-first submission with both automatic background
    /// pumping and explicit manual drain.
    ///
    /// Each test creates a unique logical control-plane identifier and passes it to
    /// every host participating in that test. This prevents Redis-backed registry,
    /// capacity, shared run, shared queue, and admission reservation state from leaking
    /// across test executions.
    /// </remarks>
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
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "background-pump-local-disabled");

            var controlPlaneSettings =
                CreateLocalControlPlaneSettings(
                    controlPlaneId,
                    enablePump: false,
                    queueFirst: true);

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            using var client =
                host.CreateClient();

            var mcp =
            await McpRbacTestClientHelper
                .CreateConfiguredClientAsync(
                    host,
                    client,
                    RequestedBy, 
                    tenantId: TenantId)
                .ConfigureAwait(false);

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
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "background-pump-http-disabled");

            var controlPlaneSettings =
                CreateHttpControlPlaneSettings(
                    controlPlaneId,
                    enablePump: false,
                    queueFirst: true);

            var runtimeInstanceSettings =
                CreateHttpRuntimeInstanceSettings(
                    controlPlaneId,
                    deployment: "test-http-pump-disabled");

            await using var fixture =
                new GenericMcpRuntimeFixture(
                    controlPlaneSettings,
                    runtimeInstanceSettings,
                    rbacTenantId: TenantId);

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
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "background-pump-local-one");

            var controlPlaneSettings =
                CreateLocalControlPlaneSettings(
                    controlPlaneId,
                    enablePump: true,
                    queueFirst: true);

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            using var client =
                host.CreateClient();

            var mcp =
            await McpRbacTestClientHelper
                .CreateConfiguredClientAsync(
                    host,
                    client,
                    RequestedBy,
                    tenantId: TenantId)
                .ConfigureAwait(false);

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
                $"Local shared queue pump dispatched and completed one run. ControlPlaneId='{controlPlaneId}', PipelineKey='{pipelineName}'.");
        }

        /// <summary>
        /// Verifies that an HTTP run submitted through the shared queue is automatically
        /// dispatched and completed by the background pump.
        /// </summary>
        [Fact]
        public async Task Submit_One_Http_Run_With_Queue_First_Should_Dispatch_And_Complete_Through_Background_Pump()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "background-pump-http-one");

            var controlPlaneSettings =
                CreateHttpControlPlaneSettings(
                    controlPlaneId,
                    enablePump: true,
                    queueFirst: true);

            var runtimeInstanceSettings =
                CreateHttpRuntimeInstanceSettings(
                    controlPlaneId,
                    deployment: "test-http-pump-one");

            await using var fixture =
                new GenericMcpRuntimeFixture(
                    controlPlaneSettings,
                    runtimeInstanceSettings,
                    rbacTenantId: TenantId);

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
                $"HTTP shared queue pump dispatched and completed one run. ControlPlaneId='{controlPlaneId}', PipelineKey='{pipelineName}'.");
        }

        /// <summary>
        /// Verifies that several local runs submitted through the shared queue are
        /// automatically dispatched and completed by the background pump.
        /// </summary>
        [Fact]
        public async Task Submit_Multiple_Local_Runs_With_Queue_First_Should_Dispatch_And_Complete_Through_Background_Pump()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "background-pump-local-many");

            var controlPlaneSettings =
                CreateLocalControlPlaneSettings(
                    controlPlaneId,
                    enablePump: true,
                    queueFirst: true);

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            using var client =
                host.CreateClient();

            var mcp =
            await McpRbacTestClientHelper
                .CreateConfiguredClientAsync(
                    host,
                    client,
                    RequestedBy,
                    tenantId: TenantId)
                .ConfigureAwait(false);

            var pipelineName =
                $"mcp-background-pump-local-many-{Guid.NewGuid():N}";

            await SubmitRunsAsync(
                    mcp,
                    pipelineName,
                    count: 4)
                .ConfigureAwait(false);

            var queueItems =
                await mcp.ListSharedQueueAsync(
                        includeTerminal: true)
                    .ConfigureAwait(false);

            output.WriteLine(
                $"QueueItems='{queueItems.Count}', Matching='{queueItems.Count(x => x.PipelineKey == pipelineName)}'");

            var runs =
                await mcp.ListSharedRunsAsync(
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
                $"Local shared queue pump dispatched and completed multiple runs. ControlPlaneId='{controlPlaneId}', PipelineKey='{pipelineName}', Count='{dispatchedRuns.Count}'.");
        }

        /// <summary>
        /// Verifies that several HTTP runs submitted through the shared queue are
        /// automatically dispatched and completed by the background pump.
        /// </summary>
        [Fact]
        public async Task Submit_Multiple_Http_Runs_With_Queue_First_Should_Dispatch_And_Complete_Through_Background_Pump()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "background-pump-http-many");

            var controlPlaneSettings =
                CreateHttpControlPlaneSettings(
                    controlPlaneId,
                    enablePump: true,
                    queueFirst: true);

            var runtimeInstanceSettings =
                CreateHttpRuntimeInstanceSettings(
                    controlPlaneId,
                    deployment: "test-http-pump-many");

            await using var fixture =
                new GenericMcpRuntimeFixture(
                    controlPlaneSettings,
                    runtimeInstanceSettings,
                    rbacTenantId: TenantId);

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
                $"HTTP shared queue pump dispatched and completed multiple runs. ControlPlaneId='{controlPlaneId}', PipelineKey='{pipelineName}', Count='{dispatchedRuns.Count}'.");
        }

        /// <summary>
        /// Verifies that a local queue-first run remains queued while the background
        /// pump is disabled, then dispatches and completes after manual drain.
        /// </summary>
        [Fact]
        public async Task Submit_Local_Run_With_Queue_First_And_Pump_Disabled_Should_Dispatch_After_Manual_Drain()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "background-pump-local-manual-drain");

            var controlPlaneSettings =
                CreateLocalControlPlaneSettings(
                    controlPlaneId,
                    enablePump: false,
                    queueFirst: true,
                    enableManualDrainPump: true,
                    deployment: "test-local-manual-drain");

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            using var client =
                host.CreateClient();

            var mcp =
            await McpRbacTestClientHelper
                .CreateConfiguredClientAsync(
                    host,
                    client,
                    RequestedBy,
                    tenantId: TenantId)
                .ConfigureAwait(false);

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
                            PumpRuntimeInstanceId = "mcp-manual-drain-pump",
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
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "background-pump-http-manual-drain");

            var controlPlaneSettings =
                CreateHttpControlPlaneSettings(
                    controlPlaneId,
                    enablePump: false,
                    queueFirst: true,
                    enableManualDrainPump: true,
                    deployment: "test-http-manual-drain");

            var runtimeInstanceSettings =
                CreateHttpRuntimeInstanceSettings(
                    controlPlaneId,
                    deployment: "test-http-manual-drain-runtime");

            await using var fixture =
                new GenericMcpRuntimeFixture(
                    controlPlaneSettings,
                    runtimeInstanceSettings,
                    rbacTenantId: TenantId);

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
                            PumpRuntimeInstanceId = "mcp-http-manual-drain-pump",
                            PumpWorkerId = "manual-test-drain",
                            MaxDispatches = 10,
                            RequestedBy = RequestedBy,
                            Source = Source,
                            Reason = "Manual HTTP provider test drain."
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
        /// Creates local control-plane settings for shared queue pump scenarios.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier shared by the scenario.</param>
        /// <param name="enablePump">Whether the shared queue background pump should be enabled.</param>
        /// <param name="queueFirst">Whether submitted runs should be enqueued into the shared queue before dispatch.</param>
        /// <param name="enableManualDrainPump">Whether the manual drain pump tool should remain enabled while the background service is disabled.</param>
        /// <param name="instanceCount">The number of local runtime instances.</param>
        /// <param name="workerCountPerInstance">The number of workers per local runtime instance.</param>
        /// <param name="maxConcurrentRunsPerInstance">The maximum concurrent runs per local runtime instance.</param>
        /// <param name="maxConcurrentRuns">The control-plane background controller maximum concurrent runs.</param>
        /// <param name="queueCapacity">The control-plane background controller queue capacity.</param>
        /// <param name="distributedWorkerCount">The distributed worker count.</param>
        /// <param name="maxLocalWorkersPerExecution">The maximum local workers per execution.</param>
        /// <param name="deployment">The deployment metadata value.</param>
        /// <returns>The control-plane settings.</returns>
        private static Dictionary<string, string?> CreateLocalControlPlaneSettings(
            string controlPlaneId,
            bool enablePump,
            bool queueFirst,
            bool enableManualDrainPump = false,
            int instanceCount = 3,
            int workerCountPerInstance = 10,
            int maxConcurrentRunsPerInstance = 5,
            int maxConcurrentRuns = 5,
            int queueCapacity = 500,
            int distributedWorkerCount = 10,
            int maxLocalWorkersPerExecution = 5,
            string deployment = "test-local")
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
                    ["AiMcpHost:EnableSharedQueuePump"] = enablePump.ToString(),

                    ["AiSharedQueueBackgroundService:Enabled"] = enablePump.ToString(),
                    ["AiSharedQueueBackgroundService:WaitForRuntimeReadiness"] = enablePump.ToString(),
                    ["AiSharedQueueBackgroundService:RuntimeReadinessPollInterval"] = "00:00:00.100",
                    ["AiSharedQueueBackgroundService:RuntimeReadinessTimeout"] = "00:01:00",

                    ["AiSharedQueuePump:Enabled"] = (enablePump || enableManualDrainPump).ToString(),

                    ["AiSharedRuntimeController:SubmitMode"] = queueFirst
                        ? "QueueFirst"
                        : "DirectDispatch",

                    ["AiRuntimeInstanceRegistration:ControlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:ProviderName"] = "local",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "local",
                    ["AiRuntimeInstanceRegistration:Metadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "local",
                    ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "control-plane-with-local-runtime",
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = deployment,

                    ["AiLocalRuntimeInstancePool:Enabled"] = "true",
                    ["AiLocalRuntimeInstancePool:InstanceCount"] = instanceCount.ToString(),
                    ["AiLocalRuntimeInstancePool:WorkerCountPerInstance"] = workerCountPerInstance.ToString(),
                    ["AiLocalRuntimeInstancePool:MaxConcurrentRunsPerInstance"] = maxConcurrentRunsPerInstance.ToString(),
                    ["AiLocalRuntimeInstancePool:RuntimeInstanceIdPrefix"] = "mcp-runtime",

                    ["AiEngine:ControlPlane:ControlPlaneId"] = controlPlaneId,
                    ["AiEngine:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiEngine:PipelineBackgroundController:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiEngine:PipelineBackgroundController:MaxConcurrentRuns"] = maxConcurrentRuns.ToString(),
                    ["AiEngine:PipelineBackgroundController:QueueCapacity"] = queueCapacity.ToString(),
                    ["AiEngine:PipelineBackgroundController:Distributed:Enabled"] = "true",
                    ["AiEngine:PipelineBackgroundController:Distributed:WorkerCount"] = distributedWorkerCount.ToString(),
                    ["AiEngine:PipelineBackgroundController:MaxLocalWorkersPerExecution"] = maxLocalWorkersPerExecution.ToString(),
                    ["AiEngine:RuntimeInstanceWorker:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,

                    ["AiRuntimeInstanceRegistration:HeartbeatInterval"] = "00:00:02",
                    ["AiRuntimeInstanceRegistration:RegistryTtl"] = "00:00:30",
                    ["AiRuntimeInstanceRegistration:CapacityTtl"] = "00:00:30"
                });
        }

        /// <summary>
        /// Creates HTTP control-plane settings for shared queue pump scenarios.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier shared by the scenario.</param>
        /// <param name="enablePump">Whether the shared queue background pump should be enabled.</param>
        /// <param name="queueFirst">Whether submitted runs should be enqueued into the shared queue before dispatch.</param>
        /// <param name="enableManualDrainPump">Whether the manual drain pump tool should remain enabled while the background service is disabled.</param>
        /// <param name="deployment">The deployment metadata value.</param>
        /// <returns>The control-plane settings.</returns>
        private static Dictionary<string, string?> CreateHttpControlPlaneSettings(
            string controlPlaneId,
            bool enablePump,
            bool queueFirst,
            bool enableManualDrainPump = false,
            string deployment = "test-http")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                controlPlaneId);

            var controlPlaneRuntimeInstanceId =
                $"mcp-control-plane-http-{Guid.NewGuid():N}";

            return GenericMcpServerTestSettings.CreateMcpSettings(
                controlPlaneId,
                new Dictionary<string, string?>
                {
                    ["AiMcpHost:Mode"] = "ControlPlaneWithHttpRuntimeInstances",
                    ["AiMcpHost:EnableSharedQueuePump"] = enablePump.ToString(),

                    ["AiSharedQueueBackgroundService:Enabled"] = enablePump.ToString(),
                    ["AiSharedQueueBackgroundService:WaitForRuntimeReadiness"] = enablePump.ToString(),
                    ["AiSharedQueueBackgroundService:RuntimeReadinessPollInterval"] = "00:00:00.100",
                    ["AiSharedQueueBackgroundService:RuntimeReadinessTimeout"] = "00:01:00",

                    ["AiSharedQueuePump:Enabled"] = (enablePump || enableManualDrainPump).ToString(),

                    ["AiSharedRuntimeController:SubmitMode"] = queueFirst
                        ? "QueueFirst"
                        : "DirectDispatch",

                    ["AiRuntimeInstanceRegistration:ControlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:ProviderName"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "control-plane-with-http-runtime",
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = deployment,

                    ["AiEngine:ControlPlane:ControlPlaneId"] = controlPlaneId,
                    ["AiEngine:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiEngine:PipelineBackgroundController:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiEngine:RuntimeInstanceWorker:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId
                });
        }

        /// <summary>
        /// Creates HTTP runtime-instance settings for shared queue pump scenarios.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier shared by the scenario.</param>
        /// <param name="deployment">The deployment metadata value.</param>
        /// <returns>The HTTP runtime-instance settings.</returns>
        private static Dictionary<string, string?> CreateHttpRuntimeInstanceSettings(
            string controlPlaneId,
            string deployment)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                controlPlaneId);

            var runtimeInstanceHostId =
                $"runtime-http-host-{Guid.NewGuid():N}";

            const int runtimePort = 5002;
            const string runtimeEndpoint = "http://localhost:5002";

            return GenericMcpServerTestSettings.CreateRuntimeInstanceSettings(
                controlPlaneId,
                runtimeInstanceHostId,
                runtimePort,
                new Dictionary<string, string?>
                {
                    ["AiRuntimeInstanceRegistration:ControlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = runtimeInstanceHostId,
                    ["AiRuntimeInstanceRegistration:ProviderName"] = "http",

                    ["AiRuntimeInstanceRegistration:ProviderMetadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:transport.endpoint"] = runtimeEndpoint,
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:runtime.instance.id"] = runtimeInstanceHostId,

                    ["AiRuntimeInstanceRegistration:Metadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:transport.endpoint"] = runtimeEndpoint,
                    ["AiRuntimeInstanceRegistration:Metadata:runtime.instance.id"] = runtimeInstanceHostId,
                    ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "runtime-instance-only",
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = deployment,

                    ["AiLocalRuntimeInstancePool:Enabled"] = "true",
                    ["AiLocalRuntimeInstancePool:InstanceCount"] = "1",
                    ["AiLocalRuntimeInstancePool:WorkerCountPerInstance"] = "10",
                    ["AiLocalRuntimeInstancePool:MaxConcurrentRunsPerInstance"] = "5",
                    ["AiLocalRuntimeInstancePool:RuntimeInstanceIdPrefix"] = "runtime-http",

                    ["AiEngine:ControlPlane:ControlPlaneId"] = controlPlaneId,
                    ["AiEngine:RuntimeInstanceId"] = runtimeInstanceHostId,
                    ["AiEngine:PipelineBackgroundController:RuntimeInstanceId"] = runtimeInstanceHostId,
                    ["AiEngine:PipelineBackgroundController:MaxConcurrentRuns"] = "5",
                    ["AiEngine:PipelineBackgroundController:QueueCapacity"] = "500",
                    ["AiEngine:PipelineBackgroundController:Distributed:Enabled"] = "true",
                    ["AiEngine:PipelineBackgroundController:Distributed:WorkerCount"] = "10",
                    ["AiEngine:PipelineBackgroundController:MaxLocalWorkersPerExecution"] = "5",
                    ["AiEngine:RuntimeInstanceWorker:RuntimeInstanceId"] = runtimeInstanceHostId
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

                    AssertRuntimeInstanceIdMatchesLogicalRuntimePrefix(
                        run.AssignedRuntimeInstanceId,
                        "mcp-runtime-");

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
                    Assert.False(
                        string.IsNullOrWhiteSpace(run.AssignedRuntimeInstanceId));

                    AssertRuntimeInstanceIdMatchesLogicalRuntimePrefix(
                        run.AssignedRuntimeInstanceId,
                        "runtime-http-");

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
        /// Verifies that a runtime instance id matches either the current host-scoped
        /// format or the legacy logical runtime instance format.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <param name="expectedRuntimeIdPrefix">The expected logical runtime id prefix.</param>
        private static void AssertRuntimeInstanceIdMatchesLogicalRuntimePrefix(
            string? runtimeInstanceId,
            string expectedRuntimeIdPrefix)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(runtimeInstanceId));

            if (runtimeInstanceId.StartsWith(
                    expectedRuntimeIdPrefix,
                    StringComparison.Ordinal))
            {
                return;
            }

            var hostScopedPattern =
                $"^host-[a-f0-9]+:{Regex.Escape(expectedRuntimeIdPrefix)}";

            Assert.True(
                Regex.IsMatch(
                    runtimeInstanceId,
                    hostScopedPattern,
                    RegexOptions.CultureInvariant),
                $"Runtime instance id '{runtimeInstanceId}' does not match expected logical prefix '{expectedRuntimeIdPrefix}' or host-scoped runtime id pattern '{hostScopedPattern}'.");
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
