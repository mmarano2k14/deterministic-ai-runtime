using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.AI.McpServer.Tests.Integration.Auth;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using System.Net;
using System.Text;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Http
{
    /// <summary>
    /// Contains MCP scenarios that validate exhausted HTTP runtime provider retry behavior.
    /// </summary>
    /// <remarks>
    /// This class intentionally does not start a real runtime-instance HTTP host.
    /// Instead, it starts only the MCP control-plane host, manually registers one
    /// HTTP runtime instance in the control-plane registry, publishes its capacity
    /// descriptor, and injects an HTTP client that always returns HTTP 500.
    ///
    /// This validates that exhausted HTTP retry does not mark the shared run as
    /// dispatched, requeues the queue item, and persists the final dispatch failure
    /// reason.
    /// </remarks>
    public sealed class HttpRuntimeProviderRetryExhaustedScenarioTests
    {
        private const string RequestedBy = "mcp-http-retry-exhausted-test";
        private const string Source = "mcp-http-retry-exhausted";
        private const string TenantId = "test-tenant";
        private const string WorkerId = "mcp-http-retry-exhausted-worker";
        private const string PumpRuntimeInstanceId = "mcp-http-retry-exhausted-pump";
        private const string RuntimeInstanceHostId = "runtime-http-retry-exhausted-host";
        private const string ControlPlaneRuntimeInstanceId = "mcp-control-plane-http-retry-exhausted";
        private const string FailureReason = "http-command-failed";

        private readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpRuntimeProviderRetryExhaustedScenarioTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public HttpRuntimeProviderRetryExhaustedScenarioTests(
            ITestOutputHelper output)
        {
            this.output =
                output;
        }

        /// <summary>
        /// Verifies that exhausted HTTP retry requeues the shared run and persists the final HTTP failure.
        /// </summary>
        [Fact]
        public async Task Submit_One_Run_Then_Drain_Should_Requeue_And_Persist_HttpFailure_When_Retry_Is_Exhausted()
        {
            var handler =
                new AlwaysFailingHttpMessageHandler();

            await using var fixture =
                await CreateRetryExhaustedHttpRuntimeFixtureAsync(
                        handler)
                    .ConfigureAwait(false);

            var mcp =
                fixture.Mcp;

            var instances =
                await mcp.ListRuntimeInstancesAsync()
                    .ConfigureAwait(false);

            foreach (var instance in instances.OrderBy(x => x.RuntimeInstanceId, StringComparer.Ordinal))
            {
                output.WriteLine(
                    $"Before drain RuntimeInstance Id='{instance.RuntimeInstanceId}', Role='{instance.Role}', Status='{instance.Status}', CanAcceptRun='{instance.CanAcceptRun}', Workers='{instance.WorkerCount}', AvailableWorkers='{instance.AvailableWorkerCount}', Slots='{instance.AvailableRunSlots}'.");
            }

            Assert.Contains(
                instances,
                instance =>
                    string.Equals(
                        instance.RuntimeInstanceId,
                        RuntimeInstanceHostId,
                        StringComparison.Ordinal));

            var pipelineName =
                CreatePipelineName();

            var submitRequest =
                CreateSubmitRequest(
                    pipelineName);

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
                await DrainRetryExhaustedHttpRuntimeAsync(
                        mcp,
                        maxDispatches: 1)
                    .ConfigureAwait(false);

            output.WriteLine(
                $"Drain Success='{drainResult.Success}', FailureReason='{drainResult.FailureReason}'.");

            Assert.True(
                drainResult.Success,
                drainResult.FailureReason);

            var listResult =
                await ListAllSharedRunsAsync(
                        mcp)
                    .ConfigureAwait(false);

            Assert.True(
                listResult.Success,
                listResult.FailureReason ?? listResult.Message);

            foreach (var item in listResult.Runs.OrderBy(x => x.SubmittedAtUtc))
            {
                output.WriteLine(
                    $"SharedRun Id='{item.SharedRunId}', Status='{item.Status}', AssignedRuntimeInstanceId='{item.AssignedRuntimeInstanceId}', LocalRunId='{item.LocalRunId}', ExecutionId='{item.ExecutionId}', FailureReason='{item.FailureReason}', Reason='{item.Reason}', PipelineKey='{item.PipelineKey}'.");
            }

            var queueItems =
                await mcp.ListSharedQueueAsync(
                        includeTerminal: true)
                    .ConfigureAwait(false);

            foreach (var item in queueItems.OrderBy(x => x.EnqueuedAtUtc))
            {
                output.WriteLine(
                    $"QueueItem SharedRunId='{item.SharedRunId}', Status='{item.Status}', ClaimedByRuntimeInstanceId='{item.ClaimedByRuntimeInstanceId}', Reason='{item.Reason}', PipelineKey='{item.PipelineKey}'.");
            }

            var run =
                listResult.Runs.Single(item =>
                    string.Equals(
                        item.PipelineKey,
                        pipelineName,
                        StringComparison.Ordinal));

            Assert.Equal(
                AiSharedRunStatus.QueuedGlobally,
                run.Status);

            Assert.Equal(
                RuntimeInstanceHostId,
                run.AssignedRuntimeInstanceId);

            Assert.Equal(
                FailureReason,
                run.FailureReason);

            Assert.Null(
                run.LocalRunId);

            Assert.Null(
                run.ExecutionId);

            var queueItem =
                queueItems.Single(item =>
                    string.Equals(
                        item.SharedRunId,
                        run.SharedRunId,
                        StringComparison.Ordinal));

            Assert.Equal(
                AiSharedQueueItemStatus.Pending,
                queueItem.Status);

            Assert.Equal(
                FailureReason,
                queueItem.Reason);

            Assert.Equal(
                2,
                handler.CallCount);

            output.WriteLine(
                $"HTTP retry exhausted failure persisted. SharedRunId='{run.SharedRunId}', RuntimeInstanceId='{run.AssignedRuntimeInstanceId}', FailureReason='{run.FailureReason}', HttpCallCount='{handler.CallCount}'.");
        }

        /// <summary>
        /// Creates an MCP control-plane host with an HTTP runtime client that always returns HTTP 500.
        /// </summary>
        /// <param name="handler">The always failing HTTP message handler.</param>
        /// <returns>The initialized retry-exhausted HTTP runtime test fixture.</returns>
        private static async Task<RetryExhaustedHttpRuntimeMcpFixture> CreateRetryExhaustedHttpRuntimeFixtureAsync(
            AlwaysFailingHttpMessageHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);

            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "http-retry-exhausted");

            var runtimeClient =
                new HttpClient(
                    handler)
                {
                    BaseAddress = new Uri("http://localhost")
                };

            var runtimeClients =
                new Dictionary<string, HttpClient>(
                    StringComparer.Ordinal)
                {
                    [RuntimeInstanceHostId] = runtimeClient,
                    ["default"] = runtimeClient
                };

            var fixture =
                new RetryExhaustedHttpRuntimeMcpFixture(
                    CreateHttpControlPlaneSettings(
                        controlPlaneId),
                    runtimeClients,
                    controlPlaneId,
                    TenantId);

            await fixture
                .InitializeAsync()
                .ConfigureAwait(false);

            return fixture;
        }

        /// <summary>
        /// Creates MCP control-plane host settings for the HTTP retry-exhausted scenario.
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
                    ["AiMcpHost:EnableSharedQueuePump"] = "false",

                    ["AiSharedQueueBackgroundService:Enabled"] = "false",
                    ["AiSharedQueueBackgroundService:WaitForRuntimeReadiness"] = "false",

                    ["AiSharedQueuePump:Enabled"] = "true",
                    ["AiSharedRuntimeController:SubmitMode"] = "QueueFirst",

                    ["AiHttpRuntimeInstanceProvider:EnableRetry"] = "true",
                    ["AiHttpRuntimeInstanceProvider:MaxRetryAttempts"] = "1",
                    ["AiHttpRuntimeInstanceProvider:RetryBaseDelay"] = "00:00:00.010",
                    ["AiHttpRuntimeInstanceProvider:RetryMaxDelay"] = "00:00:00.050",
                    ["AiHttpRuntimeInstanceProvider:RetryTimeouts"] = "false",
                    ["AiHttpRuntimeInstanceProvider:EnableCircuitBreaker"] = "true",
                    ["AiHttpRuntimeInstanceProvider:CircuitBreakerFailureThreshold"] = "5",
                    ["AiHttpRuntimeInstanceProvider:CircuitBreakerBreakDuration"] = "00:01:00",
                    ["AiHttpRuntimeInstanceProvider:DispatchTimeout"] = "00:00:05",

                    ["AiRuntimeInstanceRegistration:ControlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = ControlPlaneRuntimeInstanceId,
                    ["AiRuntimeInstanceRegistration:ProviderName"] = "http",

                    ["AiRuntimeInstanceRegistration:ProviderMetadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:transport.endpoint"] = "http://localhost",
                    ["AiRuntimeInstanceRegistration:ProviderMetadata:runtime.instance.id"] = RuntimeInstanceHostId,

                    ["AiRuntimeInstanceRegistration:Metadata:controlPlaneId"] = controlPlaneId,
                    ["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:transport.name"] = "http",
                    ["AiRuntimeInstanceRegistration:Metadata:transport.endpoint"] = "http://localhost",
                    ["AiRuntimeInstanceRegistration:Metadata:runtime.instance.id"] = RuntimeInstanceHostId,
                    ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "control-plane-with-retry-exhausted-http-runtime",
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-http-retry-exhausted",

                    ["AiEngine:ControlPlane:ControlPlaneId"] = controlPlaneId,
                    ["AiEngine:RuntimeInstanceId"] = ControlPlaneRuntimeInstanceId
                });
        }

        /// <summary>
        /// Creates a unique pipeline name for one test scenario.
        /// </summary>
        /// <returns>The unique pipeline name.</returns>
        private static string CreatePipelineName()
        {
            return $"mcp-http-retry-exhausted-pipeline-{Guid.NewGuid():N}";
        }

        /// <summary>
        /// Creates a shared runtime submit request for a test pipeline.
        /// </summary>
        /// <param name="pipelineName">The pipeline key.</param>
        /// <returns>The shared runtime controller request.</returns>
        private static AiSharedRuntimeControllerRequest CreateSubmitRequest(
            string pipelineName)
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
                    stepCount: 5,
                    flakyStepInterval: 0)
            };
        }

        /// <summary>
        /// Drains the shared queue for the retry-exhausted HTTP runtime provider scenario.
        /// </summary>
        /// <param name="mcp">The MCP test client.</param>
        /// <param name="maxDispatches">The maximum number of dispatches to perform.</param>
        /// <returns>The shared queue pump result.</returns>
        private static async Task<AiSharedQueuePumpResult> DrainRetryExhaustedHttpRuntimeAsync(
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
        /// Provides an MCP control-plane-only fixture with injected runtime HTTP clients.
        /// </summary>
        private sealed class RetryExhaustedHttpRuntimeMcpFixture : IAsyncDisposable
        {
            private readonly IReadOnlyDictionary<string, string?> settings;
            private readonly IReadOnlyDictionary<string, HttpClient> runtimeClientsByRuntimeInstanceId;
            private readonly string controlPlaneId;
            private readonly string? rbacTenantId;

            /// <summary>
            /// Gets the MCP control-plane host.
            /// </summary>
            public GenericMcpServerTestHost? Host { get; private set; }

            /// <summary>
            /// Gets the MCP control-plane HTTP client.
            /// </summary>
            public HttpClient? Client { get; private set; }

            /// <summary>
            /// Gets the MCP test client.
            /// </summary>
            public McpTestClient Mcp { get; private set; } = default!;

            /// <summary>
            /// Initializes a new instance of the <see cref="RetryExhaustedHttpRuntimeMcpFixture"/> class.
            /// </summary>
            /// <param name="settings">The MCP host settings.</param>
            /// <param name="runtimeClientsByRuntimeInstanceId">The runtime HTTP clients keyed by runtime instance identifier.</param>
            /// <param name="controlPlaneId">The logical control-plane identifier.</param>
            /// <param name="rbacTenantId">The RBAC tenant identifier.</param>
            public RetryExhaustedHttpRuntimeMcpFixture(
                IReadOnlyDictionary<string, string?> settings,
                IReadOnlyDictionary<string, HttpClient> runtimeClientsByRuntimeInstanceId,
                string controlPlaneId,
                string? rbacTenantId)
            {
                this.settings =
                    settings ?? throw new ArgumentNullException(nameof(settings));

                this.runtimeClientsByRuntimeInstanceId =
                    runtimeClientsByRuntimeInstanceId
                    ?? throw new ArgumentNullException(nameof(runtimeClientsByRuntimeInstanceId));

                this.controlPlaneId =
                    controlPlaneId ?? throw new ArgumentNullException(nameof(controlPlaneId));

                this.rbacTenantId =
                    rbacTenantId;
            }

            /// <summary>
            /// Initializes the MCP control-plane host, registers the retry-exhausted runtime instance, publishes capacity, and creates the MCP test client.
            /// </summary>
            /// <returns>A task representing the asynchronous initialization operation.</returns>
            public async Task InitializeAsync()
            {
                Host =
                    new GenericMcpServerTestHost(
                        settings,
                        runtimeClientsByRuntimeInstanceId);

                Client =
                    Host.CreateClient();

                await RegisterRetryExhaustedRuntimeInstanceAsync()
                    .ConfigureAwait(false);

                Mcp =
                    await McpRbacTestClientHelper
                        .CreateConfiguredClientAsync(
                            Host,
                            Client,
                            McpRbacTestContextFactory.DefaultUserId,
                            rbacTenantId,
                            tenantGroupId: null)
                        .ConfigureAwait(false);
            }

            /// <summary>
            /// Registers a ready HTTP runtime instance whose HTTP client always returns HTTP 500.
            /// </summary>
            /// <returns>A task representing the asynchronous registration operation.</returns>
            private async Task RegisterRetryExhaustedRuntimeInstanceAsync()
            {
                if (Host is null)
                {
                    throw new InvalidOperationException(
                        "The MCP control-plane host has not been initialized.");
                }

                var now =
                    DateTimeOffset.UtcNow;

                var metadata =
                    CreateRuntimeMetadata();

                var registry =
                    Host.Services.GetRequiredService<IAiRuntimeInstanceRegistry>();

                await registry
                    .RegisterAsync(
                        new AiRuntimeInstanceRegistration
                        {
                            RuntimeInstanceId = RuntimeInstanceHostId,
                            HostId = RuntimeInstanceHostId,
                            RuntimeId = RuntimeInstanceHostId,
                            ControlPlaneHostId = ControlPlaneRuntimeInstanceId,
                            ControlPlaneId = controlPlaneId,
                            HostName = "retry-exhausted-http-runtime",
                            WorkerCount = 1,
                            MaxConcurrentRuns = 1,
                            QueueCapacity = 10,
                            RuntimeVersion = "test",
                            RegisteredAtUtc = now,
                            Metadata = metadata
                        })
                    .ConfigureAwait(false);

                var capacityStore =
                    Host.Services.GetRequiredService<IAiRuntimeInstanceCapacityStore>();

                await capacityStore
                    .PublishAsync(
                        new AiRuntimeInstanceCapacityDescriptor
                        {
                            RuntimeInstanceId = RuntimeInstanceHostId,
                            ControlPlaneId = controlPlaneId,
                            ControlPlaneHostId = ControlPlaneRuntimeInstanceId,
                            Role = AiRuntimeInstanceRole.Runtime,
                            Status = AiRuntimeInstanceStatus.Ready,
                            WorkerCount = 1,
                            ActiveWorkerCount = 0,
                            AvailableWorkerCount = 1,
                            MaxWorkersPerRun = 1,
                            MinWorkersRequiredPerRun = 1,
                            QueuedRunCount = 0,
                            RunningRunCount = 0,
                            ActiveRunCount = 0,
                            MaxConcurrentRuns = 1,
                            MaxRunSlots = 1,
                            AvailableRunSlots = 1,
                            ReservedRunSlots = 0,
                            EffectiveAvailableRunSlots = 1,
                            IsQueuePaused = false,
                            CanAcceptRun = true,
                            LastHeartbeatAtUtc = now,
                            Metadata = metadata
                        })
                    .ConfigureAwait(false);

                await registry
                    .HeartbeatAsync(
                        RuntimeInstanceHostId,
                        queuedRunCount: 0,
                        runningRunCount: 0,
                        activeRunCount: 0,
                        availableRunSlots: 1,
                        activeWorkerCount: 0,
                        availableWorkerCount: 1,
                        maxLocalWorkersPerExecution: 1,
                        isQueuePaused: false,
                        canAcceptRun: true,
                        status: AiRuntimeInstanceStatus.Ready)
                    .ConfigureAwait(false);
            }

            /// <summary>
            /// Creates metadata used by the manual retry-exhausted HTTP runtime registration and capacity descriptor.
            /// </summary>
            /// <returns>The runtime metadata.</returns>
            private Dictionary<string, string> CreateRuntimeMetadata()
            {
                return new Dictionary<string, string>
                {
                    ["controlPlaneId"] = controlPlaneId,
                    ["provider.name"] = "http",
                    ["transport.name"] = "http",
                    ["transport.endpoint"] = "http://localhost",
                    ["runtime.instance.id"] = RuntimeInstanceHostId,
                    ["tenantId"] = TenantId,
                    ["hostType"] = "manual-retry-exhausted-http-runtime",
                    ["deployment"] = "test-http-retry-exhausted"
                };
            }

            /// <inheritdoc />
            public async ValueTask DisposeAsync()
            {
                Client?.Dispose();

                foreach (var runtimeClient in runtimeClientsByRuntimeInstanceId.Values.Distinct())
                {
                    runtimeClient.Dispose();
                }

                if (Host is not null)
                {
                    await Host
                        .DisposeAsync()
                        .ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// HTTP message handler that always returns HTTP 500 to exhaust retry attempts.
        /// </summary>
        private sealed class AlwaysFailingHttpMessageHandler : HttpMessageHandler
        {
            private int callCount;

            /// <summary>
            /// Gets the number of HTTP calls received by this handler.
            /// </summary>
            public int CallCount =>
                Volatile.Read(
                    ref callCount);

            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Interlocked.Increment(
                    ref callCount);

                return Task.FromResult(
                    new HttpResponseMessage(
                        HttpStatusCode.InternalServerError)
                    {
                        Content =
                            new StringContent(
                                "persistent transient runtime failure",
                                Encoding.UTF8,
                                "text/plain")
                    });
            }
        }
    }
}