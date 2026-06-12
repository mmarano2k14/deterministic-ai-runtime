using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.Runtime.ControlPlane.Admission.Reservations;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue.Redis;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios
{
    /// <summary>
    /// Contains heavy integration tests for queue-first shared run dispatch across
    /// multiple runtime instances.
    /// </summary>
    /// <remarks>
    /// These scenarios validate the heavy dispatch path with Redis-backed shared run,
    /// shared queue, and admission reservation stores.
    ///
    /// Each scenario creates a unique logical control-plane identifier and passes it
    /// consistently to every host participating in that scenario. This prevents Redis
    /// registry, capacity, shared run, shared queue, and reservation data from leaking
    /// across tests.
    /// </remarks>
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

            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "heavy-local-dispatch");

            var controlPlaneSettings =
                CreateHeavyLocalControlPlaneSettings(
                    controlPlaneId);

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            using var client =
                host.CreateClient();

            AssertRedisStores(
                host.Services);

            var mcp =
                new McpTestClient(
                    client);

            await LogRuntimeInstancesAsync(
                    mcp)
                .ConfigureAwait(false);

            var pipelineName =
                $"mcp-heavy-local-queue-first-{Guid.NewGuid():N}";

            var expectedSharedRunIds =
                await SubmitRunsAsync(
                        mcp,
                        pipelineName,
                        count: runCount,
                        stepCount: stepCount,
                        flakyStepInterval: 0)
                    .ConfigureAwait(false);

            Assert.Equal(
                runCount,
                expectedSharedRunIds.Count);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                        mcp,
                        pipelineName,
                        expectedSharedRunIds,
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
                $"Heavy local QueueFirst dispatch completed. ControlPlaneId='{controlPlaneId}', PipelineKey='{pipelineName}', Runs='{runCount}', StepsPerRun='{stepCount}', RuntimeInstances='{string.Join(", ", participatingRuntimeInstances)}'.");
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

            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "heavy-http-dispatch");

            var controlPlaneSettings =
                CreateHeavyHttpControlPlaneSettings(
                    controlPlaneId);

            var runtimeInstanceSettings =
                CreateHeavyHttpRuntimeInstanceHostSettings(
                    controlPlaneId);

            await using var fixture =
                new GenericMcpRuntimeFixture(
                    controlPlaneSettings,
                    runtimeInstanceSettings);

            await fixture
                .InitializeAsync()
                .ConfigureAwait(false);

            AssertRedisStores(
                fixture.Services);

            await LogRuntimeInstancesAsync(
                    fixture.Mcp)
                .ConfigureAwait(false);

            var pipelineName =
                $"mcp-heavy-http-queue-first-{Guid.NewGuid():N}";

            var expectedSharedRunIds =
                await SubmitRunsAsync(
                        fixture.Mcp,
                        pipelineName,
                        count: runCount,
                        stepCount: stepCount,
                        flakyStepInterval: 0)
                    .ConfigureAwait(false);

            Assert.Equal(
                runCount,
                expectedSharedRunIds.Count);

            var dispatchedRuns =
                await McpTestWaitHelpers.WaitForDispatchedRunsAsync(
                        fixture.Mcp,
                        pipelineName,
                        expectedSharedRunIds,
                        expectedCount: runCount,
                        timeout: TimeSpan.FromMinutes(5))
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
                $"Heavy HTTP QueueFirst dispatch completed. ControlPlaneId='{controlPlaneId}', PipelineKey='{pipelineName}', Runs='{runCount}', StepsPerRun='{stepCount}', RuntimeInstances='{string.Join(", ", participatingRuntimeInstances)}'.");
        }

        /// <summary>
        /// Verifies that the HTTP control-plane host uses Redis-backed control-plane stores.
        /// </summary>
        [Fact]
        public async Task ControlPlaneWithHttpRuntimeInstances_ShouldUseRedisControlPlaneStores()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "heavy-http-store-check");

            await using var fixture =
                new GenericMcpRuntimeFixture(
                    CreateHeavyHttpControlPlaneSettings(controlPlaneId),
                    CreateHeavyHttpRuntimeInstanceHostSettings(controlPlaneId));

            await fixture
                .InitializeAsync()
                .ConfigureAwait(false);

            AssertRedisStores(
                fixture.Services);
        }

        /// <summary>
        /// Verifies that Redis-backed control-plane stores replaced the default in-memory stores.
        /// </summary>
        /// <param name="services">The service provider to inspect.</param>
        private void AssertRedisStores(
            IServiceProvider services)
        {
            ArgumentNullException.ThrowIfNull(services);

            var sharedRunStore =
                services.GetRequiredService<IAiSharedRunStore>();

            var sharedQueue =
                services.GetRequiredService<IAiSharedQueue>();

            var reservationStore =
                services.GetRequiredService<IAiRuntimeAdmissionReservationStore>();

            output.WriteLine(
                $"Redis store assert: IAiSharedRunStore='{sharedRunStore.GetType().FullName}'.");

            output.WriteLine(
                $"Redis store assert: IAiSharedQueue='{sharedQueue.GetType().FullName}'.");

            output.WriteLine(
                $"Redis store assert: IAiRuntimeAdmissionReservationStore='{reservationStore.GetType().FullName}'.");

            Assert.IsType<RedisAiSharedRunStore>(
                sharedRunStore);

            Assert.IsType<RedisAiSharedQueue>(
                sharedQueue);

            Assert.IsType<RedisAiRuntimeAdmissionReservationStore>(
                reservationStore);
        }

        /// <summary>
        /// Creates heavy local control-plane settings for a single isolated scenario.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier shared by the scenario.</param>
        /// <returns>The heavy local control-plane settings.</returns>
        private static Dictionary<string, string?> CreateHeavyLocalControlPlaneSettings(
            string controlPlaneId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                controlPlaneId);

            var runtimeInstanceIdPrefix =
                $"mcp-runtime-{Guid.NewGuid():N}";

            var deployment =
                $"test-local-heavy-dispatch-{Guid.NewGuid():N}";

            var controlPlaneRuntimeInstanceId =
                $"mcp-control-plane-local-{Guid.NewGuid():N}";

            return GenericMcpServerTestSettings.CreateMcpSettings(
                controlPlaneId,
                new Dictionary<string, string?>
                {
                    ["AiMcpHost:Mode"] = "ControlPlaneWithLocalRuntimeInstances",
                    ["AiMcpHost:EnableSharedQueuePump"] = "true",

                    ["AiSharedQueueBackgroundService:Enabled"] = "true",
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
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = deployment,

                    ["AiLocalRuntimeInstancePool:Enabled"] = "true",
                    ["AiLocalRuntimeInstancePool:InstanceCount"] = "3",
                    ["AiLocalRuntimeInstancePool:WorkerCountPerInstance"] = "30",
                    ["AiLocalRuntimeInstancePool:MaxConcurrentRunsPerInstance"] = "10",
                    ["AiLocalRuntimeInstancePool:RuntimeInstanceIdPrefix"] = runtimeInstanceIdPrefix,

                    ["AiEngine:ControlPlane:ControlPlaneId"] = controlPlaneId,
                    ["AiEngine:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiEngine:PipelineBackgroundController:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiEngine:PipelineBackgroundController:MaxConcurrentRuns"] = "10",
                    ["AiEngine:PipelineBackgroundController:QueueCapacity"] = "500",
                    ["AiEngine:PipelineBackgroundController:Distributed:Enabled"] = "true",
                    ["AiEngine:PipelineBackgroundController:Distributed:WorkerCount"] = "30",
                    ["AiEngine:PipelineBackgroundController:MaxLocalWorkersPerExecution"] = "5",
                    ["AiEngine:RuntimeInstanceWorker:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,

                    ["AiSharedQueueBackgroundService:WaitForRuntimeReadiness"] = "true",
                    ["AiSharedQueueBackgroundService:RuntimeReadinessPollInterval"] = "00:00:00.100",
                    ["AiSharedQueueBackgroundService:RuntimeReadinessTimeout"] = "00:01:00",

                    ["AiRuntimeInstanceRegistration:HeartbeatInterval"] = "00:00:02",
                    ["AiRuntimeInstanceRegistration:RegistryTtl"] = "00:00:30",
                    ["AiRuntimeInstanceRegistration:CapacityTtl"] = "00:00:30"
                });
        }

        /// <summary>
        /// Creates heavy HTTP control-plane settings for a single isolated scenario.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier shared by the scenario.</param>
        /// <returns>The heavy HTTP control-plane settings.</returns>
        private static Dictionary<string, string?> CreateHeavyHttpControlPlaneSettings(
            string controlPlaneId)
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
                    ["AiMcpHost:EnableSharedQueuePump"] = "true",

                    ["AiSharedQueueBackgroundService:Enabled"] = "true",
                    ["AiSharedQueueBackgroundService:WaitForRuntimeReadiness"] = "true",
                    ["AiSharedQueueBackgroundService:RuntimeReadinessPollInterval"] = "00:00:00.100",
                    ["AiSharedQueueBackgroundService:RuntimeReadinessTimeout"] = "00:01:00",

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
                    ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "control-plane-with-http-runtime",
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-http-heavy-dispatch",

                    ["AiEngine:ControlPlane:ControlPlaneId"] = controlPlaneId,
                    ["AiEngine:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiEngine:PipelineBackgroundController:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId,
                    ["AiEngine:RuntimeInstanceWorker:RuntimeInstanceId"] = controlPlaneRuntimeInstanceId
                });
        }

        /// <summary>
        /// Creates heavy HTTP runtime-instance host settings.
        /// </summary>
        /// <remarks>
        /// The host identity is unique for every scenario, but the dispatchable runtime
        /// instances are expected to be created by the local runtime instance pool:
        /// <c>runtime-http-*</c>.
        /// </remarks>
        /// <param name="controlPlaneId">The logical control-plane identifier shared by the scenario.</param>
        /// <returns>The heavy HTTP runtime-instance host settings.</returns>
        private static Dictionary<string, string?> CreateHeavyHttpRuntimeInstanceHostSettings(
            string controlPlaneId)
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
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-http-heavy-runtime-pool",

                    ["AiLocalRuntimeInstancePool:Enabled"] = "true",
                    ["AiLocalRuntimeInstancePool:InstanceCount"] = "3",
                    ["AiLocalRuntimeInstancePool:WorkerCountPerInstance"] = "30",
                    ["AiLocalRuntimeInstancePool:MaxConcurrentRunsPerInstance"] = "10",
                    ["AiLocalRuntimeInstancePool:RuntimeInstanceIdPrefix"] = "runtime-http",

                    ["AiEngine:ControlPlane:ControlPlaneId"] = controlPlaneId,
                    ["AiEngine:RuntimeInstanceId"] = runtimeInstanceHostId,
                    ["AiEngine:PipelineBackgroundController:RuntimeInstanceId"] = runtimeInstanceHostId,
                    ["AiEngine:PipelineBackgroundController:MaxConcurrentRuns"] = "10",
                    ["AiEngine:PipelineBackgroundController:QueueCapacity"] = "500",
                    ["AiEngine:PipelineBackgroundController:Distributed:Enabled"] = "true",
                    ["AiEngine:PipelineBackgroundController:Distributed:WorkerCount"] = "30",
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
        /// <param name="stepCount">The number of pipeline steps.</param>
        /// <param name="flakyStepInterval">The flaky step interval.</param>
        /// <returns>The submitted shared run ids.</returns>
        private static async Task<IReadOnlySet<string>> SubmitRunsAsync(
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

            var submittedSharedRunIds =
                submitResults
                    .Select(ExtractSharedRunId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(
                count,
                submittedSharedRunIds.Count);

            return submittedSharedRunIds;
        }

        /// <summary>
        /// Extracts the shared run id from a submit result.
        /// </summary>
        /// <param name="submitResult">The submit result.</param>
        /// <returns>The shared run id.</returns>
        private static string ExtractSharedRunId(
            object submitResult)
        {
            ArgumentNullException.ThrowIfNull(submitResult);

            var resultType =
                submitResult.GetType();

            var directSharedRunId =
                resultType.GetProperty("SharedRunId")?.GetValue(submitResult) as string;

            if (!string.IsNullOrWhiteSpace(directSharedRunId))
            {
                return directSharedRunId;
            }

            var runId =
                resultType.GetProperty("RunId")?.GetValue(submitResult) as string;

            if (!string.IsNullOrWhiteSpace(runId))
            {
                return runId;
            }

            var sharedRun =
                resultType.GetProperty("SharedRun")?.GetValue(submitResult);

            if (sharedRun is not null)
            {
                var sharedRunId =
                    sharedRun
                        .GetType()
                        .GetProperty("SharedRunId")
                        ?.GetValue(sharedRun) as string;

                if (!string.IsNullOrWhiteSpace(sharedRunId))
                {
                    return sharedRunId;
                }
            }

            var run =
                resultType.GetProperty("Run")?.GetValue(submitResult);

            if (run is not null)
            {
                var sharedRunId =
                    run
                        .GetType()
                        .GetProperty("SharedRunId")
                        ?.GetValue(run) as string;

                if (!string.IsNullOrWhiteSpace(sharedRunId))
                {
                    return sharedRunId;
                }
            }

            throw new InvalidOperationException(
                $"Could not extract SharedRunId from submit result type '{resultType.FullName}'.");
        }

        /// <summary>
        /// Verifies distributed runtime participation and returns the participating
        /// runtime instance ids.
        /// </summary>
        /// <param name="dispatchedRuns">The dispatched shared runs.</param>
        /// <param name="expectedCount">The expected number of dispatched runs.</param>
        /// <param name="expectedRuntimeInstanceCount">The maximum expected runtime instance count.</param>
        /// <param name="expectedRuntimeInstancePrefix">The expected logical runtime instance id prefix.</param>
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

                    AssertMatchesRuntimeInstanceId(
                        run.AssignedRuntimeInstanceId!,
                        expectedRuntimeInstancePrefix);

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
        /// Verifies that a runtime instance id matches either the legacy logical format
        /// or the current host-scoped format.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance id to verify.</param>
        /// <param name="expectedRuntimeInstancePrefix">The expected logical runtime instance id prefix.</param>
        private static void AssertMatchesRuntimeInstanceId(
            string runtimeInstanceId,
            string expectedRuntimeInstancePrefix)
        {
            var isLegacyRuntimeInstanceId =
                runtimeInstanceId.StartsWith(
                    expectedRuntimeInstancePrefix,
                    StringComparison.Ordinal);

            var hostScopedPattern =
                $"^host-[a-f0-9]+:{Regex.Escape(expectedRuntimeInstancePrefix)}";

            var isHostScopedRuntimeInstanceId =
                Regex.IsMatch(
                    runtimeInstanceId,
                    hostScopedPattern,
                    RegexOptions.CultureInvariant);

            Assert.True(
                isLegacyRuntimeInstanceId || isHostScopedRuntimeInstanceId,
                $"Runtime instance id '{runtimeInstanceId}' does not match expected logical prefix '{expectedRuntimeInstancePrefix}' or host-scoped runtime id pattern '{hostScopedPattern}'.");
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

            var failedStatuses =
                finalStatuses
                    .Where(status => !string.Equals(
                        status.RunState?.Status,
                        "completed",
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();

            if (failedStatuses.Length > 0)
            {
                var failureDump =
                    string.Join(
                        Environment.NewLine,
                        failedStatuses.Select(status =>
                            $"RunId='{status.RunId}', " +
                            $"RuntimeInstanceId='{status.RuntimeInstanceId}', " +
                            $"Status='{status.RunState?.Status}', " +
                            $"ExecutionId='{status.ExecutionId ?? status.RunState?.ExecutionId}', " +
                            $"Success='{status.Success}', " +
                            $"FailureReason='{status.FailureReason}', " +
                            $"Message='{status.Message}', " +
                            $"RunStateFailureReason='{status.RunState?.FailureReason}', " +
                            $"RunStateMessage='{status.RunState?.FailureReason}'"));

                Assert.Fail(
                    $"Expected all runtime runs to complete, but '{failedStatuses.Length}' out of '{finalStatuses.Count}' failed." +
                    Environment.NewLine +
                    failureDump);
            }

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
