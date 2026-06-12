using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.Runtime.ControlPlane.Admission.Reservations;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue.Redis;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios
{
    /// <summary>
    /// Contains MCP integration tests for runtime scale-out request orchestration.
    /// </summary>
    /// <remarks>
    /// These scenarios validate the flow:
    ///
    /// <list type="number">
    /// <item><description>MCP submits a shared run.</description></item>
    /// <item><description>Admission finds no available runtime capacity.</description></item>
    /// <item><description>The shared run is marked as <see cref="AiSharedRunStatus.ScaleOutRequested" />.</description></item>
    /// <item><description>A Redis-backed scale-out request is created.</description></item>
    /// <item><description>The scale-out watcher observes the request.</description></item>
    /// <item><description>The simulated provider fulfills the request.</description></item>
    /// </list>
    ///
    /// This does not create a real Kubernetes pod yet.
    /// Kubernetes will be plugged in later through <see cref="IAiRuntimeScaleOutProvider" />.
    /// </remarks>
    public sealed class SharedRunScaleOutScenarioTests
    {
        /// <summary>
        /// Actor used by scale-out scenario tests.
        /// </summary>
        private const string RequestedBy = "mcp-scaleout-integration-test";

        /// <summary>
        /// Source used by scale-out scenario tests.
        /// </summary>
        private const string Source = "mcp-scaleout-test";

        /// <summary>
        /// Tenant used by scale-out scenario tests.
        /// </summary>
        private const string TenantId = "test-tenant";

        /// <summary>
        /// The test output helper.
        /// </summary>
        private readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance of the <see cref="SharedRunScaleOutScenarioTests" /> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public SharedRunScaleOutScenarioTests(
            ITestOutputHelper output)
        {
            this.output =
                output;
        }

        /// <summary>
        /// Verifies that a real MCP control-plane host creates and fulfills a Redis-backed
        /// scale-out request when admission requests additional runtime capacity.
        /// </summary>
        [Fact]
        public async Task ControlPlaneWithHttpRuntimeInstances_With_No_Runtime_Capacity_Should_Fulfill_Redis_ScaleOut_Request()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "scaleout-request");

            var controlPlaneSettings =
                GenericMcpServerTestSettings.CreateScaleOutOnlyControlPlaneSettings(
                    controlPlaneId);

            await using var host =
                new GenericMcpServerTestHost(
                    controlPlaneSettings);

            using var client =
                host.CreateClient();

            AssertRedisStoresAndPublisher(
                host.Services);

            var mcp =
                new McpTestClient(
                    client);

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var pipelineName =
                $"mcp-scaleout-request-{Guid.NewGuid():N}";

            var expectedSharedRunIds =
                await SubmitRunsAsync(
                        mcp,
                        pipelineName,
                        count: 1,
                        stepCount: 3,
                        flakyStepInterval: 0)
                    .ConfigureAwait(false);

            var sharedRunId =
                Assert.Single(
                    expectedSharedRunIds);

            var sharedRunStore =
                host.Services.GetRequiredService<IAiSharedRunStore>();

            var sharedRun =
                await sharedRunStore
                    .GetAsync(
                        sharedRunId)
                    .ConfigureAwait(false);

            Assert.NotNull(
                sharedRun);

            Assert.Equal(
                AiSharedRunStatus.ScaleOutRequested,
                sharedRun.Status);

            Assert.Equal(
                controlPlaneId,
                sharedRun.ControlPlaneId);

            Assert.Equal(
                pipelineName,
                sharedRun.PipelineKey);

            var expectedScaleOutRequestId =
                $"scale-out-{sharedRunId}";

            var scaleOutRequest =
                await WaitForScaleOutRequestStatusAsync(
                        scaleOutRequestStore,
                        expectedScaleOutRequestId,
                        AiRuntimeScaleOutRequestStatus.Fulfilled,
                        TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);

            Assert.Equal(
                expectedScaleOutRequestId,
                scaleOutRequest.RequestId);

            Assert.Equal(
                sharedRunId,
                scaleOutRequest.SharedRunId);

            Assert.Equal(
                controlPlaneId,
                scaleOutRequest.ControlPlaneId);

            Assert.Equal(
                TenantId,
                scaleOutRequest.TenantId);

            Assert.Equal(
                pipelineName,
                scaleOutRequest.PipelineKey);

            Assert.Equal(
                AiRuntimeScaleOutRequestStatus.Fulfilled,
                scaleOutRequest.Status);

            Assert.Equal(
                0,
                scaleOutRequest.AvailableInstanceCount);

            Assert.Equal(
                0,
                scaleOutRequest.CurrentInstanceCount);

            Assert.Equal(
                3,
                scaleOutRequest.MaxInstanceCount);

            Assert.Equal(
                1,
                scaleOutRequest.RequestedTargetInstanceCount);

            Assert.Equal(
                "mcp-scaleout-watcher",
                scaleOutRequest.ObservedBy);

            Assert.Equal(
                "mcp-scaleout-watcher",
                scaleOutRequest.FulfilledBy);

            Assert.False(
                string.IsNullOrWhiteSpace(
                    scaleOutRequest.FulfilledRuntimeInstanceId));

            Assert.StartsWith(
                "simulated-mcp-runtime-",
                scaleOutRequest.FulfilledRuntimeInstanceId,
                StringComparison.Ordinal);

            output.WriteLine(
                $"Redis scale-out request fulfilled by watcher. ControlPlaneId='{controlPlaneId}', SharedRunId='{sharedRunId}', RequestId='{scaleOutRequest.RequestId}', RuntimeInstanceId='{scaleOutRequest.FulfilledRuntimeInstanceId}', PipelineKey='{pipelineName}'.");
        }

        /// <summary>
        /// Verifies that Redis-backed control-plane stores, the store-backed scale-out publisher,
        /// the scale-out provider, and the watcher hosted service are registered correctly.
        /// </summary>
        /// <param name="services">The service provider to inspect.</param>
        private void AssertRedisStoresAndPublisher(
            IServiceProvider services)
        {
            ArgumentNullException.ThrowIfNull(
                services);

            var sharedRunStore =
                services.GetRequiredService<IAiSharedRunStore>();

            var sharedQueue =
                services.GetRequiredService<IAiSharedQueue>();

            var reservationStore =
                services.GetRequiredService<IAiRuntimeAdmissionReservationStore>();

            var scaleOutRequestStore =
                services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var scaleOutPublisher =
                services.GetRequiredService<IAiRuntimeScaleOutRequestPublisher>();

            var scaleOutProvider =
                services.GetRequiredService<IAiRuntimeScaleOutProvider>();

            var watcherOptions =
                services.GetRequiredService<IOptions<AiRuntimeScaleOutRequestWatcherOptions>>().Value;

            var hostedServices =
                services.GetServices<IHostedService>().ToArray();

            output.WriteLine(
                $"Redis scale-out assert: IAiSharedRunStore='{sharedRunStore.GetType().FullName}'.");

            output.WriteLine(
                $"Redis scale-out assert: IAiSharedQueue='{sharedQueue.GetType().FullName}'.");

            output.WriteLine(
                $"Redis scale-out assert: IAiRuntimeAdmissionReservationStore='{reservationStore.GetType().FullName}'.");

            output.WriteLine(
                $"Redis scale-out assert: IAiRuntimeScaleOutRequestStore='{scaleOutRequestStore.GetType().FullName}'.");

            output.WriteLine(
                $"Redis scale-out assert: IAiRuntimeScaleOutRequestPublisher='{scaleOutPublisher.GetType().FullName}'.");

            output.WriteLine(
                $"Redis scale-out assert: IAiRuntimeScaleOutProvider='{scaleOutProvider.GetType().FullName}'.");

            output.WriteLine(
                $"Redis scale-out assert: Watcher.Enabled='{watcherOptions.Enabled}', WatcherId='{watcherOptions.WatcherId}', ControlPlaneId='{watcherOptions.ControlPlaneId}', Interval='{watcherOptions.Interval}', MaxRequestsPerCycle='{watcherOptions.MaxRequestsPerCycle}'.");

            output.WriteLine(
                "Redis scale-out assert: IHostedService registrations: " +
                string.Join(
                    " | ",
                    hostedServices.Select(service => service.GetType().FullName)));

            Assert.IsType<RedisAiSharedRunStore>(
                sharedRunStore);

            Assert.IsType<RedisAiSharedQueue>(
                sharedQueue);

            Assert.IsType<RedisAiRuntimeAdmissionReservationStore>(
                reservationStore);

            Assert.IsType<RedisAiRuntimeScaleOutRequestStore>(
                scaleOutRequestStore);

            Assert.IsType<StoreBackedAiRuntimeScaleOutRequestPublisher>(
                scaleOutPublisher);

            Assert.IsType<SimulatedAiRuntimeScaleOutProvider>(
                scaleOutProvider);

            Assert.True(
                watcherOptions.Enabled,
                "Scale-out watcher options should be enabled for this scenario.");

            Assert.Equal(
                "mcp-scaleout-watcher",
                watcherOptions.WatcherId);

            Assert.Contains(
                hostedServices,
                service => service.GetType() == typeof(AiRuntimeScaleOutRequestWatcherHostedService));
        }

        /// <summary>
        /// Waits until a scale-out request reaches the expected status.
        /// </summary>
        /// <param name="store">The scale-out request store.</param>
        /// <param name="requestId">The scale-out request identifier.</param>
        /// <param name="expectedStatus">The expected scale-out request status.</param>
        /// <param name="timeout">The maximum wait duration.</param>
        /// <returns>The matching scale-out request record.</returns>
        private static async Task<AiRuntimeScaleOutRequestRecord> WaitForScaleOutRequestStatusAsync(
            IAiRuntimeScaleOutRequestStore store,
            string requestId,
            AiRuntimeScaleOutRequestStatus expectedStatus,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(
                store);

            ArgumentException.ThrowIfNullOrWhiteSpace(
                requestId);

            var deadline =
                DateTimeOffset.UtcNow.Add(
                    timeout);

            AiRuntimeScaleOutRequestRecord? last =
                null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                last =
                    await store
                        .GetAsync(
                            requestId)
                        .ConfigureAwait(false);

                if (last is not null &&
                    last.Status == expectedStatus)
                {
                    return last;
                }

                await Task
                    .Delay(
                        TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Scale-out request '{requestId}' did not reach status '{expectedStatus}' within '{timeout}'. LastStatus='{last?.Status.ToString() ?? "missing"}'.");
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
                await mcp
                    .SubmitManyRunsAsync(
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
            ArgumentNullException.ThrowIfNull(
                submitResult);

            var resultType =
                submitResult.GetType();

            var directSharedRunId =
                resultType
                    .GetProperty("SharedRunId")
                    ?.GetValue(submitResult) as string;

            if (!string.IsNullOrWhiteSpace(
                    directSharedRunId))
            {
                return directSharedRunId;
            }

            var runId =
                resultType
                    .GetProperty("RunId")
                    ?.GetValue(submitResult) as string;

            if (!string.IsNullOrWhiteSpace(
                    runId))
            {
                return runId;
            }

            var sharedRun =
                resultType
                    .GetProperty("SharedRun")
                    ?.GetValue(submitResult);

            if (sharedRun is not null)
            {
                var sharedRunId =
                    sharedRun
                        .GetType()
                        .GetProperty("SharedRunId")
                        ?.GetValue(sharedRun) as string;

                if (!string.IsNullOrWhiteSpace(
                        sharedRunId))
                {
                    return sharedRunId;
                }
            }

            var run =
                resultType
                    .GetProperty("Run")
                    ?.GetValue(submitResult);

            if (run is not null)
            {
                var sharedRunId =
                    run
                        .GetType()
                        .GetProperty("SharedRunId")
                        ?.GetValue(run) as string;

                if (!string.IsNullOrWhiteSpace(
                        sharedRunId))
                {
                    return sharedRunId;
                }
            }

            throw new InvalidOperationException(
                $"Could not extract SharedRunId from submit result type '{resultType.FullName}'.");
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