using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.AI.McpServer.Tests.Integration.Auth;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Development.Http
{
    /// <summary>
    /// Contains MCP scenarios that validate HTTP runtime provider circuit-open behavior.
    /// </summary>
    /// <remarks>
    /// This class intentionally does not start a real runtime-instance HTTP host.
    /// Instead, it starts only the MCP control-plane host, manually registers one
    /// HTTP runtime instance in the control-plane registry, publishes its capacity
    /// descriptor, and injects an HTTP client that always fails for that runtime instance.
    ///
    /// The first dispatch attempt should fail with provider unavailable and open the
    /// HTTP circuit because the circuit breaker threshold is configured to one.
    /// The second dispatch attempt, in the same drain operation, should observe the
    /// open circuit and persist <c>http-circuit-open</c>.
    ///
    /// This avoids changing the generic HTTP runtime fixtures and prevents regressions
    /// in the existing successful HTTP provider scenarios.
    /// </remarks>
    public sealed class HttpRuntimeProviderCircuitOpenScenarioTests
    {
        private const string RequestedBy = "mcp-http-circuit-open-test";
        private const string Source = "mcp-http-circuit-open";
        private const string TenantId = "test-tenant";
        private const string WorkerId = "mcp-http-circuit-open-worker";
        private const string PumpRuntimeInstanceId = "mcp-http-circuit-open-pump";
        private const string RuntimeInstanceHostId = "runtime-http-circuit-open-host";
        private const string ControlPlaneRuntimeInstanceId = "mcp-control-plane-http-circuit-open";
        private const string FailureReason = "http-circuit-open";
        private const string ProviderUnavailableFailureReason = "http-provider-unavailable";
        private const string FailureMessage = "Simulated unreachable HTTP runtime endpoint.";

        private readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpRuntimeProviderCircuitOpenScenarioTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public HttpRuntimeProviderCircuitOpenScenarioTests(
            ITestOutputHelper output)
        {
            this.output =
                output;
        }

        /// <summary>
        /// Verifies that an HTTP circuit-open dispatch failure is requeued and persisted through the MCP control-plane path.
        /// </summary>
        [Fact]
        public async Task Submit_One_Run_Then_Drain_Should_Requeue_And_Persist_CircuitOpen_Failure()
        {
            await using var fixture =
                await CreateBrokenHttpRuntimeFixtureAsync()
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
                await DrainBrokenHttpRuntimeAsync(
                        mcp,
                        maxDispatches: 2)
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

            // A failed dispatch is requeued for a future admission attempt.
            // The shared-run store deliberately clears active runtime ownership so
            // the failed HTTP runtime is not retained as the current assignment.
            Assert.Null(
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

            output.WriteLine(
                $"HTTP circuit-open failure persisted and run ownership cleared for redispatch. SharedRunId='{run.SharedRunId}', AssignedRuntimeInstanceId='{run.AssignedRuntimeInstanceId}', FailureReason='{run.FailureReason}'.");
        }

        /// <summary>
        /// Creates an MCP control-plane host with a deliberately failing HTTP runtime client.
        /// </summary>
        /// <returns>The initialized broken HTTP runtime test fixture.</returns>
        private static async Task<BrokenHttpRuntimeMcpFixture> CreateBrokenHttpRuntimeFixtureAsync()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "http-circuit-open");

            var runtimeClients =
                new Dictionary<string, HttpClient>(
                    StringComparer.Ordinal)
                {
                    [RuntimeInstanceHostId] =
                        new HttpClient(
                            new BrokenRuntimeHttpMessageHandler())
                        {
                            BaseAddress = new Uri("http://localhost")
                        },

                    ["default"] =
                        new HttpClient(
                            new BrokenRuntimeHttpMessageHandler())
                        {
                            BaseAddress = new Uri("http://localhost")
                        }
                };

            var fixture =
                new BrokenHttpRuntimeMcpFixture(
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
        /// Creates MCP control-plane host settings for the HTTP circuit-open scenario.
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

                    ["AiHttpRuntimeInstanceProvider:EnableRetry"] = "false",
                    ["AiHttpRuntimeInstanceProvider:EnableCircuitBreaker"] = "true",
                    ["AiHttpRuntimeInstanceProvider:CircuitBreakerFailureThreshold"] = "1",
                    ["AiHttpRuntimeInstanceProvider:CircuitBreakerBreakDuration"] = "00:01:00",
                    ["AiHttpRuntimeInstanceProvider:DispatchTimeout"] = "00:00:02",

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
                    ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "control-plane-with-broken-http-runtime",
                    ["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-http-circuit-open",

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
            return $"mcp-http-circuit-open-pipeline-{Guid.NewGuid():N}";
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
        /// Drains the shared queue for the broken HTTP runtime provider scenario.
        /// </summary>
        /// <param name="mcp">The MCP test client.</param>
        /// <param name="maxDispatches">The maximum number of dispatches to perform.</param>
        /// <returns>The shared queue pump result.</returns>
        private static async Task<AiSharedQueuePumpResult> DrainBrokenHttpRuntimeAsync(
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
        private sealed class BrokenHttpRuntimeMcpFixture : IAsyncDisposable
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
            /// Initializes a new instance of the <see cref="BrokenHttpRuntimeMcpFixture"/> class.
            /// </summary>
            /// <param name="settings">The MCP host settings.</param>
            /// <param name="runtimeClientsByRuntimeInstanceId">The runtime HTTP clients keyed by runtime instance identifier.</param>
            /// <param name="controlPlaneId">The logical control-plane identifier.</param>
            /// <param name="rbacTenantId">The RBAC tenant identifier.</param>
            public BrokenHttpRuntimeMcpFixture(
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
            /// Initializes the MCP control-plane host, registers the broken runtime instance, publishes capacity, and creates the MCP test client.
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

                await RegisterBrokenRuntimeInstanceAsync()
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
            /// Registers a ready HTTP runtime instance whose HTTP client always fails.
            /// </summary>
            /// <returns>A task representing the asynchronous registration operation.</returns>
            private async Task RegisterBrokenRuntimeInstanceAsync()
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
                            HostName = "broken-http-runtime",
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
            /// Creates metadata used by the manual broken HTTP runtime registration and capacity descriptor.
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
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = TenantId,
                    ["hostType"] = "manual-broken-http-runtime",
                    ["deployment"] = "test-http-circuit-open"
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
        /// HTTP message handler that always simulates an unreachable runtime endpoint.
        /// </summary>
        private sealed class BrokenRuntimeHttpMessageHandler : HttpMessageHandler
        {
            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();

                throw new HttpRequestException(
                    FailureMessage);
            }
        }
    }
}