using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios
{
    /// <summary>
    /// Contains heavy integration tests for queue-first shared run dispatch across
    /// multiple runtime instances.
    /// </summary>
    public sealed class SharedRunHeavyDispatchScenarioTests
    {
        private const string RequestedBy = "mcp-heavy-dispatch-integration-test";
        private const string Source = "mcp-heavy-dispatch-test";
        private const string TenantId = "test-tenant";

        private readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance of the <see cref="SharedRunHeavyDispatchScenarioTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public SharedRunHeavyDispatchScenarioTests(
            ITestOutputHelper output)
        {
            this.output =
                output;
        }

        /// <summary>
        /// Verifies that many local queue-first runs are dispatched across multiple
        /// local runtime instances and complete successfully.
        /// </summary>
        [Fact]
        public async Task Submit_50_Local_Queue_First_Runs_With_100_Steps_Should_Dispatch_Across_Local_Runtime_Instances_With_Worker_Capacity()
        {
            const int runCount = 50;
            const int stepCount = 100;
            const int expectedRuntimeInstanceCount = 3;

            var controlPlaneSettings =
                CreateHeavyLocalControlPlaneSettings();

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            using var client =
                host.CreateClient();

            var mcp =
                new McpTestClient(
                    client);

            await LogRuntimeInstancesAsync(
                    mcp)
                .ConfigureAwait(false);

            var pipelineName =
                $"mcp-heavy-local-queue-first-{Guid.NewGuid():N}";

            await SubmitRunsAsync(
                    mcp,
                    pipelineName,
                    count: runCount,
                    stepCount: stepCount,
                    flakyStepInterval: 0)
                .ConfigureAwait(false);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                        mcp,
                        pipelineName,
                        expectedCount: runCount,
                        timeout: TimeSpan.FromMinutes(5))
                    .ConfigureAwait(false);

            var participatingRuntimeInstances =
                AssertDistributedRuntimeParticipation(
                    dispatchedRuns,
                    expectedCount: runCount,
                    expectedRuntimeInstanceCount: expectedRuntimeInstanceCount,
                    expectedRuntimeInstancePrefix: "mcp-runtime-",
                    distributionLabel: "Local runtime distribution");

            await AssertRunsCompleteAsync(
                    mcp,
                    dispatchedRuns,
                    expectedCount: runCount)
                .ConfigureAwait(false);

            await AssertSharedQueueContainsExpectedPipelineRunCountAsync(
                    mcp,
                    pipelineName,
                    expectedCount: runCount)
                .ConfigureAwait(false);

            output.WriteLine(
                $"Heavy local QueueFirst dispatch completed. PipelineKey='{pipelineName}', Runs='{runCount}', StepsPerRun='{stepCount}', RuntimeInstances='{string.Join(", ", participatingRuntimeInstances)}'.");
        }

        /// <summary>
        /// Verifies that many HTTP queue-first runs are dispatched across multiple
        /// runtime instances hosted inside one RuntimeInstanceOnly HTTP host.
        /// </summary>
        /// <remarks>
        /// This test validates the model where a single HTTP runtime host owns an
        /// internal runtime instance pool. The control plane must see and dispatch to
        /// the child runtime instances, not to the parent HTTP host identity.
        /// </remarks>
        [Fact]
        public async Task Submit_50_Http_Queue_First_Runs_With_100_Steps_Should_Dispatch_Across_RuntimeInstanceOnly_Http_Pool()
        {
            const int runCount = 50;
            const int stepCount = 100;
            const int expectedRuntimeInstanceCount = 3;

            var controlPlaneSettings =
                CreateHeavyHttpControlPlaneSettings();

            var runtimeInstanceSettings =
                CreateHeavyHttpRuntimeInstanceHostSettings();

            await using var fixture =
                new GenericMcpRuntimeFixture(
                    controlPlaneSettings,
                    runtimeInstanceSettings);

            await fixture
                .InitializeAsync()
                .ConfigureAwait(false);

            await LogRuntimeInstancesAsync(
                    fixture.Mcp)
                .ConfigureAwait(false);

            var pipelineName =
                $"mcp-heavy-http-queue-first-{Guid.NewGuid():N}";

            await SubmitRunsAsync(
                    fixture.Mcp,
                    pipelineName,
                    count: runCount,
                    stepCount: stepCount,
                    flakyStepInterval: 0)
                .ConfigureAwait(false);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                        fixture.Mcp,
                        pipelineName,
                        expectedCount: runCount,
                        timeout: TimeSpan.FromMinutes(2))
                    .ConfigureAwait(false);

            var participatingRuntimeInstances =
                AssertDistributedRuntimeParticipation(
                    dispatchedRuns,
                    expectedCount: runCount,
                    expectedRuntimeInstanceCount: expectedRuntimeInstanceCount,
                    expectedRuntimeInstancePrefix: "runtime-http-",
                    distributionLabel: "HTTP runtime distribution");

            await AssertRunsCompleteAsync(
                    fixture.Mcp,
                    dispatchedRuns,
                    expectedCount: runCount)
                .ConfigureAwait(false);

            await AssertSharedQueueContainsExpectedPipelineRunCountAsync(
                    fixture.Mcp,
                    pipelineName,
                    expectedCount: runCount)
                .ConfigureAwait(false);

            output.WriteLine(
                $"Heavy HTTP QueueFirst dispatch completed. PipelineKey='{pipelineName}', Runs='{runCount}', StepsPerRun='{stepCount}', RuntimeInstances='{string.Join(", ", participatingRuntimeInstances)}'.");
        }

        /// <summary>
        /// Creates heavy local control-plane settings.
        /// </summary>
        /// <returns>The heavy local control-plane settings.</returns>
        private static Dictionary<string, string?> CreateHeavyLocalControlPlaneSettings()
        {
            return GenericMcpServerTestSettings.CreateMcpSettings(
                new Dictionary<string, string?>
                {
                    ["AiMcpHost:Mode"] = "ControlPlaneWithLocalRuntimeInstances",
                    ["AiMcpHost:EnableSharedQueuePump"] = "true",

                    ["AiSharedQueueBackgroundService:Enabled"] = "true",
                    ["AiSharedQueuePump:Enabled"] = "true",
                    ["AiSharedRuntimeController:SubmitMode"] = "QueueFirst",

                    ["AiRuntimeInstanceRegistration:ProviderName"] = "local",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "local",
                    ["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "local",
                    ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = "mcp-control-plane-local",
                    ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "control-plane-with-local-runtime",
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-local-heavy-dispatch",

                    ["AiLocalRuntimeInstancePool:Enabled"] = "true",
                    ["AiLocalRuntimeInstancePool:InstanceCount"] = "3",
                    ["AiLocalRuntimeInstancePool:WorkerCountPerInstance"] = "30",
                    ["AiLocalRuntimeInstancePool:MaxConcurrentRunsPerInstance"] = "10",
                    ["AiLocalRuntimeInstancePool:RuntimeInstanceIdPrefix"] = "mcp-runtime",

                    ["AiEngine:RuntimeInstanceId"] = "mcp-control-plane-local",
                    ["AiEngine:PipelineBackgroundController:MaxConcurrentRuns"] = "10",
                    ["AiEngine:PipelineBackgroundController:QueueCapacity"] = "500",
                    ["AiEngine:PipelineBackgroundController:Distributed:Enabled"] = "true",
                    ["AiEngine:PipelineBackgroundController:Distributed:WorkerCount"] = "30",
                    ["AiEngine:PipelineBackgroundController:MaxLocalWorkersPerExecution"] = "5"
                });
        }

        /// <summary>
        /// Creates heavy HTTP control-plane settings.
        /// </summary>
        /// <returns>The heavy HTTP control-plane settings.</returns>
        private static Dictionary<string, string?> CreateHeavyHttpControlPlaneSettings()
        {
            return GenericMcpServerTestSettings.CreateMcpSettings(
                new Dictionary<string, string?>
                {
                    ["AiMcpHost:Mode"] = "ControlPlaneWithHttpRuntimeInstances",
                    ["AiMcpHost:EnableSharedQueuePump"] = "true",

                    ["AiSharedQueueBackgroundService:Enabled"] = "true",
                    ["AiSharedQueuePump:Enabled"] = "true",
                    ["AiSharedRuntimeController:SubmitMode"] = "QueueFirst",

                    ["AiRuntimeInstanceRegistration:ProviderName"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = "mcp-control-plane-http",
                    ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "control-plane-with-http-runtime",
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-http-heavy-dispatch",

                    ["AiEngine:RuntimeInstanceId"] = "mcp-control-plane-http"
                });
        }

        /// <summary>
        /// Creates heavy HTTP runtime-instance host settings.
        /// </summary>
        /// <remarks>
        /// The host identity is <c>runtime-http-host</c>, but the dispatchable runtime
        /// instances are expected to be created by the local runtime instance pool:
        /// <c>runtime-http-1</c>, <c>runtime-http-2</c>, and <c>runtime-http-3</c>.
        /// </remarks>
        /// <returns>The heavy HTTP runtime-instance host settings.</returns>
        private static Dictionary<string, string?> CreateHeavyHttpRuntimeInstanceHostSettings()
        {
            return GenericMcpServerTestSettings.CreateRuntimeInstanceSettings(
                new Dictionary<string, string?>
                {
                    ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = "runtime-http-host",
                    ["AiRuntimeInstanceRegistration:ProviderName"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:transport.endpoint"] = "http://localhost",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:runtime.instance.id"] = "runtime-http-host",
                    ["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:transport.endpoint"] = "http://localhost",
                    ["AiRuntimeInstanceRegistration:Metadata:runtime.instance.id"] = "runtime-http-host",
                    ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "runtime-instance-only",
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-http-heavy-runtime-pool",

                    ["AiLocalRuntimeInstancePool:Enabled"] = "true",
                    ["AiLocalRuntimeInstancePool:InstanceCount"] = "3",
                    ["AiLocalRuntimeInstancePool:WorkerCountPerInstance"] = "30",
                    ["AiLocalRuntimeInstancePool:MaxConcurrentRunsPerInstance"] = "10",
                    ["AiLocalRuntimeInstancePool:RuntimeInstanceIdPrefix"] = "runtime-http",

                    ["AiEngine:RuntimeInstanceId"] = "runtime-http-host",
                    ["AiEngine:PipelineBackgroundController:RuntimeInstanceId"] = "runtime-http-host",
                    ["AiEngine:PipelineBackgroundController:MaxConcurrentRuns"] = "10",
                    ["AiEngine:PipelineBackgroundController:QueueCapacity"] = "500",
                    ["AiEngine:PipelineBackgroundController:Distributed:Enabled"] = "true",
                    ["AiEngine:PipelineBackgroundController:Distributed:WorkerCount"] = "30",
                    ["AiEngine:PipelineBackgroundController:MaxLocalWorkersPerExecution"] = "5",
                    ["AiEngine:RuntimeInstanceWorker:RuntimeInstanceId"] = "runtime-http-host"
                });
        }

        /// <summary>
        /// Submits a number of shared runtime runs for the specified pipeline.
        /// </summary>
        /// <param name="mcp">The MCP test client.</param>
        /// <param name="pipelineName">The pipeline key.</param>
        /// <param name="count">The number of runs to submit.</param>
        /// <param name="stepCount">The number of pipeline steps.</param>
        /// <param name="flakyStepInterval">The flaky step interval.</param>
        private static async Task SubmitRunsAsync(
            McpTestClient mcp,
            string pipelineName,
            int count,
            int stepCount,
            int flakyStepInterval)
        {
            var submitRequest =
                CreateSubmitRequest(
                    pipelineName,
                    stepCount: stepCount,
                    flakyStepInterval: flakyStepInterval);

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
        /// Verifies distributed runtime participation and returns the participating
        /// runtime instance ids.
        /// </summary>
        /// <param name="dispatchedRuns">The dispatched shared runs.</param>
        /// <param name="expectedCount">The expected number of dispatched runs.</param>
        /// <param name="expectedRuntimeInstanceCount">The maximum expected runtime instance count.</param>
        /// <param name="expectedRuntimeInstancePrefix">The expected runtime instance id prefix.</param>
        /// <param name="distributionLabel">The distribution log label.</param>
        /// <returns>The participating runtime instance ids.</returns>
        private string[] AssertDistributedRuntimeParticipation(
            IReadOnlyList<AiSharedRunRecord> dispatchedRuns,
            int expectedCount,
            int expectedRuntimeInstanceCount,
            string expectedRuntimeInstancePrefix,
            string distributionLabel)
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
                        expectedRuntimeInstancePrefix,
                        run.AssignedRuntimeInstanceId,
                        StringComparison.Ordinal);

                    Assert.False(
                        string.IsNullOrWhiteSpace(run.LocalRunId));
                });

            var distribution =
                dispatchedRuns
                    .GroupBy(
                        run => run.AssignedRuntimeInstanceId,
                        StringComparer.Ordinal)
                    .Select(group => $"{group.Key}={group.Count()}")
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();

            output.WriteLine(
                $"{distributionLabel}: {string.Join(", ", distribution)}");

            var participatingRuntimeInstances =
                dispatchedRuns
                    .Select(run => run.AssignedRuntimeInstanceId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray();

            Assert.True(
                participatingRuntimeInstances.Length > 1,
                $"Expected more than one runtime instance to participate, but only found: {string.Join(", ", participatingRuntimeInstances)}.");

            Assert.True(
                participatingRuntimeInstances.Length <= expectedRuntimeInstanceCount,
                $"Expected at most {expectedRuntimeInstanceCount} runtime instances, but found: {string.Join(", ", participatingRuntimeInstances)}.");

            Assert.Equal(
                expectedCount,
                dispatchedRuns
                    .Select(run => run.LocalRunId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .Count());

            return participatingRuntimeInstances;
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
                        timeout: TimeSpan.FromMinutes(10))
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

            Assert.Equal(
                expectedCount,
                finalStatuses
                    .Select(status => status.ExecutionId ?? status.RunState?.ExecutionId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .Count());
        }

        /// <summary>
        /// Verifies that the shared queue contains the expected number of items for
        /// the specified pipeline.
        /// </summary>
        /// <param name="mcp">The MCP test client.</param>
        /// <param name="pipelineName">The pipeline key.</param>
        /// <param name="expectedCount">The expected number of queue items.</param>
        private static async Task AssertSharedQueueContainsExpectedPipelineRunCountAsync(
            McpTestClient mcp,
            string pipelineName,
            int expectedCount)
        {
            var queueItems =
                await mcp.ListSharedQueueAsync(
                        includeTerminal: true)
                    .ConfigureAwait(false);

            var matchingQueueItems =
                queueItems
                    .Where(item => string.Equals(
                        item.PipelineKey,
                        pipelineName,
                        StringComparison.Ordinal))
                    .ToArray();

            Assert.Equal(
                expectedCount,
                matchingQueueItems.Length);
        }

        /// <summary>
        /// Writes runtime instance visibility to the test output.
        /// </summary>
        /// <param name="mcp">The MCP test client.</param>
        private async Task LogRuntimeInstancesAsync(
            McpTestClient mcp)
        {
            var instances =
                await mcp.ListRuntimeInstancesAsync()
                    .ConfigureAwait(false);

            foreach (var instance in instances.OrderBy(x => x.RuntimeInstanceId, StringComparer.Ordinal))
            {
                output.WriteLine(
                    $"RuntimeInstance Id='{instance.RuntimeInstanceId}', Role='{instance.Role}', Provider='{instance.Role}', Status='{instance.Status}', CanAcceptRun='{instance.CanAcceptRun}', Workers='{instance.WorkerCount}', ActiveWorkers='{instance.ActiveWorkerCount}', AvailableWorkers='{instance.AvailableWorkerCount}', Slots='{instance.AvailableRunSlots}'.");
            }
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