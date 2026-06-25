using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.ExecutionAssistance;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Redis;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay.Models;
using Multiplexed.Abstractions.AI.Execution.Persistence.Snapshot;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.DI.Engine;
using Multiplexed.AI.Observability.Ledger;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.ControlPlane.Admission.Reservations;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.ExecutionAssistance;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Local;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.AI.Runtime.ControlPlane.SharedController;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Dispatch;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue.Redis;
using Multiplexed.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.Observability.Ledger.DI;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.AI.Tests.Integration.Fixtures;
using Multiplexed.AI.Tests.Integration.Infrastructure;
using Multiplexed.AI.Tests.Integration.Runtime.Execution.Fixtures;
using Multiplexed.AI.Tests.Integration.Runtime.Execution.MultipleInstance.Worker.Chaos;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.Tests.Integration.Runtime.ControlPlane.SharedController
{
    /// <summary>
    /// Heavy multi-instance real execution integration tests for the shared queue control-plane.
    /// </summary>
    /// <remarks>
    /// This test validates the complete Kubernetes-like path without Kubernetes:
    ///
    /// SharedController
    /// -> QueueGlobally
    /// -> Redis shared run store
    /// -> Redis shared queue
    /// -> multiple runtime instances
    /// -> concurrent shared queue pumps
    /// -> RemoteAiSharedRunDispatcher
    /// -> InMemoryAiSharedRuntimeInstanceRegistry
    /// -> LocalAiSharedRuntimeInstance per runtime instance
    /// -> real local runtime queue per instance
    /// -> real distributed DAG workers per instance
    /// -> flaky steps
    /// -> retry
    /// -> throttling / provider concurrency
    /// -> retention compact + evict
    /// -> Mongo snapshots
    /// -> replay restore
    /// -> ledger / timeline / metrics.
    /// </remarks>
    [Collection("redis")]
    public sealed class AiSharedQueueMultiInstanceRealExecutionHeavyIntegrationTests : IAsyncLifetime
    {
        private readonly ITestOutputHelper _output;

        private readonly string _runKeyPrefix =
            $"test:ai:shared-runs:real-heavy:{Guid.NewGuid():N}";

        private readonly string _queueKeyPrefix =
            $"test:ai:shared-queue:real-heavy:{Guid.NewGuid():N}";

        private IConnectionMultiplexer? _connection;

        public AiSharedQueueMultiInstanceRealExecutionHeavyIntegrationTests(
            ITestOutputHelper output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        public async Task InitializeAsync()
        {
            _connection = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        }

        public async Task DisposeAsync()
        {
            if (_connection is null)
            {
                return;
            }

            var database = _connection.GetDatabase();

            var server = _connection.GetServer(
                _connection.GetEndPoints().First());

            var runKeys = server.Keys(
                    database: database.Database,
                    pattern: $"{_runKeyPrefix}*")
                .ToArray();

            var queueKeys = server.Keys(
                    database: database.Database,
                    pattern: $"{_queueKeyPrefix}*")
                .ToArray();

            var keys = runKeys
                .Concat(queueKeys)
                .ToArray();

            if (keys.Length > 0)
            {
                await database.KeyDeleteAsync(keys);
            }

            await _connection.CloseAsync();
            await _connection.DisposeAsync();
        }

        [RedisFact]
        public async Task SharedQueue_Should_Run_Real_MultiInstance_Executions_With_All_Features_Enabled()
        {
            await RunHeavyScenarioAsync(
                HeavyScenario.AllFeaturesHeavy());
        }

        [RedisFact]
        public async Task SharedQueue_Should_Run_Real_MultiInstance_Executions_Stress_With_All_Features_Enabled_100_Steps()
        {
            await RunHeavyScenarioAsync(
                HeavyScenario.AllFeaturesStress(
                    runCount: 9,
                    stepCount: 100));
        }

        [RedisFact(Skip = "Temporarily disabled: heavy stress scenario is too long after tenant-aware/process-host hardening. Needs tenant/capacity/pipeline consistency review.")]
        public async Task SharedQueue_Should_Run_Real_MultiInstance_Executions_Stress_With_All_Features_Enabled_500_Steps()
        {
            await RunHeavyScenarioAsync(
               HeavyScenario.AllFeaturesStress(
                   runCount: 2,
                   expectedMinimumParticipatingInstances: 2,
                   runtimeInstanceCount: 2,
                   workerCount: 30,
                   executionTimeout: TimeSpan.FromMinutes(3),
                   stepCount: 250,
                   enableRetention: true));

            /*
            await RunHeavyScenarioAsync(
                HeavyScenario.AllFeaturesStress(
                    runCount: 2,
                    expectedMinimumParticipatingInstances: 2,
                    runtimeInstanceCount:2,
                    workerCount:50,
                    executionTimeout: TimeSpan.FromMinutes(3),
                    stepCount: 500));
            */
        }

        [RedisFact(Skip = "Temporarily disabled: heavy stress scenario is too long after tenant-aware/process-host hardening. Needs tenant/capacity/pipeline consistency review.")]
        public async Task MultiInstance_Executions_Stress_50_NominalSteps_10_runs_3_instances_10_workers()
        {
            await RunHeavyScenarioAsync(
                HeavyScenario.AllFeaturesStress(
                    runCount: 10,
                    expectedMinimumParticipatingInstances: 3,
                    runtimeInstanceCount: 3,
                    workerCount: 10,
                    executionTimeout: TimeSpan.FromMinutes(3),
                    stepCount: 50,
                    enableRetention: true));
        }

        [RedisFact]
        public async Task MultiInstance_Executions_Stress_250_NominalSteps_2_runs_2_instances_10_workers()
        {
            await RunHeavyScenarioAsync(
                HeavyScenario.AllFeaturesStress(
                    runCount: 2,
                    expectedMinimumParticipatingInstances: 2,
                    runtimeInstanceCount: 2,
                    workerCount: 10,
                    executionTimeout: TimeSpan.FromMinutes(3),
                    stepCount: 250,
                    enableRetention: true));
        }

        [RedisFact(Skip = "Temporarily disabled: heavy stress scenario is too long after tenant-aware/process-host hardening. Needs tenant/capacity/pipeline consistency review.")]
        public async Task MultiInstance_Executions_Stress_250_NominalSteps_2_runs_2_instances_30_workers()
        {
            await RunHeavyScenarioAsync(
                HeavyScenario.AllFeaturesStress(
                    runCount: 2,
                    expectedMinimumParticipatingInstances: 2,
                    runtimeInstanceCount: 2,
                    workerCount: 30,
                    executionTimeout: TimeSpan.FromMinutes(5),
                    stepCount: 250,
                    enableRetention: true));
        }

        [RedisFact(Skip = "Temporarily disabled: heavy stress scenario is too long after tenant-aware/process-host hardening. Needs tenant/capacity/pipeline consistency review.")]
        public async Task MultiInstance_Executions_Stress_1000_NominalSteps_2_runs_2_instances_10_workers()
        {
            await RunHeavyScenarioAsync(
                HeavyScenario.AllFeaturesStress(
                    runCount: 2,
                    expectedMinimumParticipatingInstances: 2,
                    runtimeInstanceCount: 2,
                    workerCount: 10,
                    executionTimeout: TimeSpan.FromMinutes(8),
                    stepCount: 500,
                    enableRetention: true));
        }

        [RedisFact(Skip = "Temporarily disabled: heavy stress scenario is too long after tenant-aware/process-host hardening. Needs tenant/capacity/pipeline consistency review.")]
        public async Task MultiInstance_Executions_Stress_1000_NominalSteps_20_runs_3_instances_10_workers()
        {
            await RunHeavyScenarioAsync(
                HeavyScenario.AllFeaturesStress(
                    runCount: 20,
                    expectedMinimumParticipatingInstances: 3,
                    runtimeInstanceCount: 3,
                    workerCount: 10,
                    executionTimeout: TimeSpan.FromMinutes(5),
                    stepCount: 50,
                    enableRetention: true));
        }

        /// <summary>
        /// Validates that an idle runtime instance can manually assist an existing execution
        /// owned by another primary runtime instance.
        /// </summary>
        /// <remarks>
        /// This test does not validate automatic idle detection yet.
        /// It validates the core cross-instance assistance path:
        ///
        /// SharedRun
        /// -> primary runtime instance
        /// -> local run
        /// -> ExecutionId
        /// -> helper runtime instance receives assistance lease
        /// -> helper advances the same existing ExecutionId.
        /// </remarks>
        [RedisFact]
        public async Task MultiInstance_ExecutionAssistance_Manual_OneLargeRun_Should_Allow_Helper_Instance_To_Assist()
        {
            var scenario = HeavyScenario.AllFeaturesStress(
                runCount: 1,
                expectedMinimumParticipatingInstances: 1,
                runtimeInstanceCount: 2,
                workerCount: 5,
                executionTimeout: TimeSpan.FromMinutes(5),
                stepCount: 500,
                enableRetention: true);

            var store = CreateRunStore();
            var queue = CreateQueue();

            var sharedRuntimeInstanceRegistry =
                new InMemoryAiSharedRuntimeInstanceRegistry();

            var controller = new AiSharedRuntimeController(
                new QueueGloballyAdmissionController(
                    runtimeInstanceCount: scenario.RuntimeInstanceCount),
                store,
                queue,
                new NeverCalledSharedRunDispatcher(),
                new NoopAiRuntimeScaleOutRequestPublisher(),
                new StaticAiControlPlaneIdResolver("test-control-plane"),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                Options.Create(new AiSharedRuntimeControllerOptions()),
                new NoopAiControlPlaneObserver(),
                 new FakeExecutionContextSnapshotProvider(
                    AiExecutionContextSnapshotTestFactory.Create(
                        tenantId: "tenant-1")));

            var runtimeInstances = new List<RuntimeInstanceHarness>();

            try
            {
                var capacityStore = new TestRuntimeInstanceCapacityStore();

                for (var index = 1; index <= scenario.RuntimeInstanceCount; index++)
                {
                    var runtimeInstanceId = $"runtime-instance-{index}";

                    var harness = await CreateRuntimeInstanceHarnessAsync(
                        scenario,
                        runtimeInstanceId,
                        sharedRuntimeInstanceRegistry);

                    runtimeInstances.Add(harness);

                    await capacityStore.PublishAsync(
                       new AiRuntimeInstanceCapacityDescriptor
                       {
                           RuntimeInstanceId = runtimeInstanceId,
                           Metadata = new Dictionary<string, string>
                           {
                               [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = "local"
                           }
                       });

                }

                PublishPipelineDefinitionToAllRuntimeInstances(
                    runtimeInstances,
                    scenario);

                var primaryRuntimeInstance = runtimeInstances[0];
                var helperRuntimeInstance = runtimeInstances[1];

                foreach (var runtimeInstance in runtimeInstances)
                {
                    await runtimeInstance.BackgroundController.StartAsync();
                }

               
                var providerRouter =
                    new AiRuntimeInstanceProviderRouter(
                        new IAiRuntimeInstanceProvider[]
                        {
                        new LocalAiRuntimeInstanceProvider(
                            sharedRuntimeInstanceRegistry)
                        });

                var providerCapabilityResolver =
                    new AiRuntimeInstanceProviderCapabilityResolver(
                        capacityStore,
                        providerRouter);

                var remoteSharedRunDispatcher =
                    new RemoteAiSharedRunDispatcher(
                        providerCapabilityResolver,
                        new InMemoryAiRuntimeInstanceRegistry(),
                        NullLogger<RemoteAiSharedRunDispatcher>.Instance);

                var sharedRunId =
                    $"shared-run-{scenario.ScenarioId}-00000";

                var submit = await controller.SubmitRunAsync(
                    new AiSharedRuntimeControllerRequest
                    {
                        Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                        RequestedSharedRunId = sharedRunId,
                        RunRequest = new AiRuntimePipelineRunRequest
                        {
                            PipelineName = scenario.PipelineName,
                            PipelineDefinition = scenario.PipelineDefinition,
                            Input = new
                            {
                                candidateId = $"{scenario.CandidateIdPrefix}-00000",
                                source = scenario.Name,
                                runIndex = 0,
                                runCount = scenario.RunCount,
                                stepCount = scenario.StepCount,
                                sharedQueue = true,
                                multiInstance = true,
                                realExecution = true,
                                manualExecutionAssistance = true,
                                retry = true,
                                retention = true,
                                compaction = true,
                                eviction = true,
                                throttling = true,
                                snapshot = true,
                                replay = true,
                                ledger = true,
                                timeline = true,
                                metrics = true
                            }
                        },
                        TenantId = "tenant-real-heavy",
                        PipelineKey = scenario.PipelineName,
                        CorrelationId = $"correlation-{sharedRunId}",
                        RequestedBy = "integration-test",
                        Source = "multi-instance-execution-assistance-test",
                        Reason = "Queue one large run for manual cross-instance execution assistance.",
                        Metadata = new Dictionary<string, string>
                        {
                            ["scenario"] = scenario.Name,
                            ["scenario.id"] = scenario.ScenarioId,
                            ["run.index"] = "0",
                            ["run.count"] = scenario.RunCount.ToString(),
                            ["step.count"] = scenario.StepCount.ToString(),
                            ["manual.execution.assistance"] = "true"
                        }
                    });

                Assert.True(
                    submit.Success,
                    submit.FailureReason ?? $"Submit failed for shared run '{sharedRunId}'.");

                var primaryPumpResult = await RunRuntimeInstancePumpUntilEmptyAsync(
                    primaryRuntimeInstance,
                    queue,
                    store,
                    remoteSharedRunDispatcher,
                    scenario.MaxDispatchesPerPumpCycle);

                Assert.Equal(1, primaryPumpResult.SuccessfulDispatchCount);
                Assert.Equal(0, primaryPumpResult.FailedDispatchCount);

                var dispatchedRuns = await WaitForSharedRunsDispatchedAsync(
                    store,
                    $"shared-run-{scenario.ScenarioId}-",
                    expectedCount: 1,
                    scenario.SharedDispatchTimeout);

                var dispatchedRun = dispatchedRuns.Single();

                Assert.Equal(AiSharedRunStatus.Dispatched, dispatchedRun.Status);
                Assert.Equal(primaryRuntimeInstance.RuntimeInstanceId, dispatchedRun.AssignedRuntimeInstanceId);
                Assert.False(string.IsNullOrWhiteSpace(dispatchedRun.LocalRunId));

                var executionId = await WaitForRuntimeRunExecutionStartedAsync(
                    primaryRuntimeInstance,
                    runtimeInstances,
                    dispatchedRun.LocalRunId!,
                    TimeSpan.FromSeconds(30));

                var assistanceLease = new AiExecutionAssistanceLease
                {
                    LeaseId = Guid.NewGuid().ToString("N"),
                    ExecutionId = executionId,
                    PrimaryRuntimeInstanceId = primaryRuntimeInstance.RuntimeInstanceId,
                    HelperRuntimeInstanceId = helperRuntimeInstance.RuntimeInstanceId,
                    MaxWorkers = 1,
                    Status = AiExecutionAssistanceStatus.Granted,
                    GrantedAtUtc = DateTimeOffset.UtcNow,
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
                    Reason = "Manual integration test assistance lease.",
                    Metadata = new Dictionary<string, string>
                    {
                        ["scenario"] = scenario.Name,
                        ["scenario.id"] = scenario.ScenarioId,
                        ["manual.execution.assistance"] = "true"
                    }
                };

                await helperRuntimeInstance.ExecutionAssistanceStore.RegisterAsync(
                    assistanceLease);

                var assistanceTask = helperRuntimeInstance.ExecutionAssistancePump.PumpAsync(
                    assistanceLease);

                var record = await WaitForRuntimeRunExecutionCompletedAsync(
                    primaryRuntimeInstance,
                    runtimeInstances,
                    dispatchedRun.LocalRunId!,
                    scenario.ExecutionTimeout);

                var assistanceResult = await assistanceTask;

                Assert.Equal(AiExecutionStatus.Completed, record.Status);
                Assert.Equal(executionId, record.ExecutionId);

                Assert.True(
                    assistanceResult.Success,
                    assistanceResult.FailureReason ?? "Manual assistance pump failed.");

                Assert.Equal(executionId, assistanceResult.ExecutionId);
                Assert.Equal(helperRuntimeInstance.RuntimeInstanceId, assistanceResult.HelperRuntimeInstanceId);
                Assert.Equal(1, assistanceResult.StartedWorkerCount);

                var storedLease = await helperRuntimeInstance.ExecutionAssistanceStore.GetAsync(
                    assistanceLease.LeaseId);

                Assert.NotNull(storedLease);
                Assert.Equal(AiExecutionAssistanceStatus.Released, storedLease!.Status);

                var helperWorkerCycles =
                    helperRuntimeInstance.Metrics.Worker.GetCyclesByRuntimeInstance();

                Assert.NotEmpty(helperWorkerCycles);

                _output.WriteLine("");
                _output.WriteLine("MANUAL EXECUTION ASSISTANCE TEST");
                _output.WriteLine("------------------------------------------------------------");
                _output.WriteLine($"ExecutionId:     {executionId}");
                _output.WriteLine($"PrimaryInstance: {primaryRuntimeInstance.RuntimeInstanceId}");
                _output.WriteLine($"HelperInstance:  {helperRuntimeInstance.RuntimeInstanceId}");
                _output.WriteLine($"LeaseId:         {assistanceLease.LeaseId}");
                _output.WriteLine($"HelperWorkers:   {assistanceResult.StartedWorkerCount}");
            }
            finally
            {
                foreach (var runtimeInstance in runtimeInstances)
                {
                    await runtimeInstance.DisposeAsync();
                }
            }
        }

        /// <summary>
        /// Validates that multiple idle runtime instances can manually assist one existing
        /// execution owned by a primary runtime instance.
        /// </summary>
        /// <remarks>
        /// This test validates the core multi-helper assistance path:
        ///
        /// SharedRun
        /// -> primary runtime instance
        /// -> local run
        /// -> ExecutionId
        /// -> multiple helper runtime instances receive assistance leases
        /// -> helpers advance the same existing ExecutionId.
        /// </remarks>
        [RedisFact(Skip = "Temporarily disabled: heavy stress scenario is too long after tenant-aware/process-host hardening. Needs tenant/capacity/pipeline consistency review.")]
        public async Task MultiInstance_ExecutionAssistance_Manual_OneLargeRun_Should_Allow_Multiple_Helper_Instances_To_Assist()
        {
            var scenario = HeavyScenario.AllFeaturesStress(
                runCount: 1,
                expectedMinimumParticipatingInstances: 1,
                runtimeInstanceCount: 4,
                workerCount: 1,
                executionTimeout: TimeSpan.FromMinutes(5),
                stepCount: 500,
                enableRetention: true);

            var store = CreateRunStore();
            var queue = CreateQueue();

            var sharedRuntimeInstanceRegistry =
                new InMemoryAiSharedRuntimeInstanceRegistry();

            var controller = new AiSharedRuntimeController(
                new QueueGloballyAdmissionController(
                    runtimeInstanceCount: scenario.RuntimeInstanceCount),
                store,
                queue,
                new NeverCalledSharedRunDispatcher(),
                new NoopAiRuntimeScaleOutRequestPublisher(),
                new StaticAiControlPlaneIdResolver("test-control-plane"),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                Options.Create(new AiSharedRuntimeControllerOptions()),
                new NoopAiControlPlaneObserver(),
                new FakeExecutionContextSnapshotProvider(
                    AiExecutionContextSnapshotTestFactory.Create(
                        tenantId: "tenant-1")));

            var runtimeInstances = new List<RuntimeInstanceHarness>();

            try
            {
                var capacityStore = new TestRuntimeInstanceCapacityStore();

                for (var index = 1; index <= scenario.RuntimeInstanceCount; index++)
                {
                    var runtimeInstanceId = $"runtime-instance-{index}";

                    var harness = await CreateRuntimeInstanceHarnessAsync(
                        scenario,
                        runtimeInstanceId,
                        sharedRuntimeInstanceRegistry);

                    runtimeInstances.Add(harness);

                    await capacityStore.PublishAsync(
                       new AiRuntimeInstanceCapacityDescriptor
                       {
                           RuntimeInstanceId = runtimeInstanceId,
                           Metadata = new Dictionary<string, string>
                           {
                               [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = "local"
                           }
                       });

                }

                PublishPipelineDefinitionToAllRuntimeInstances(
                    runtimeInstances,
                    scenario);

                var primaryRuntimeInstance = runtimeInstances[0];
                var helperRuntimeInstances = runtimeInstances
                    .Skip(1)
                    .ToArray();

                foreach (var runtimeInstance in runtimeInstances)
                {
                    await runtimeInstance.BackgroundController.StartAsync();
                }

                var providerRouter =
                    new AiRuntimeInstanceProviderRouter(
                        new IAiRuntimeInstanceProvider[]
                        {
                            new LocalAiRuntimeInstanceProvider(
                                sharedRuntimeInstanceRegistry)
                        });

                var providerCapabilityResolver =
                    new AiRuntimeInstanceProviderCapabilityResolver(
                        capacityStore,
                        providerRouter);

                var remoteSharedRunDispatcher =
                    new RemoteAiSharedRunDispatcher(
                        providerCapabilityResolver,
                        new InMemoryAiRuntimeInstanceRegistry(),
                        NullLogger<RemoteAiSharedRunDispatcher>.Instance);

                var sharedRunId =
                    $"shared-run-{scenario.ScenarioId}-00000";

                var submit = await controller.SubmitRunAsync(
                    new AiSharedRuntimeControllerRequest
                    {
                        Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                        RequestedSharedRunId = sharedRunId,
                        RunRequest = new AiRuntimePipelineRunRequest
                        {
                            PipelineName = scenario.PipelineName,
                            PipelineDefinition = scenario.PipelineDefinition,
                            Input = new
                            {
                                candidateId = $"{scenario.CandidateIdPrefix}-00000",
                                source = scenario.Name,
                                runIndex = 0,
                                runCount = scenario.RunCount,
                                stepCount = scenario.StepCount,
                                sharedQueue = true,
                                multiInstance = true,
                                realExecution = true,
                                manualExecutionAssistance = true,
                                multiHelperExecutionAssistance = true,
                                retry = true,
                                retention = true,
                                compaction = true,
                                eviction = true,
                                throttling = true,
                                snapshot = true,
                                replay = true,
                                ledger = true,
                                timeline = true,
                                metrics = true
                            }
                        },
                        TenantId = "tenant-real-heavy",
                        PipelineKey = scenario.PipelineName,
                        CorrelationId = $"correlation-{sharedRunId}",
                        RequestedBy = "integration-test",
                        Source = "multi-instance-execution-assistance-test",
                        Reason = "Queue one large run for manual multi-helper cross-instance execution assistance.",
                        Metadata = new Dictionary<string, string>
                        {
                            ["scenario"] = scenario.Name,
                            ["scenario.id"] = scenario.ScenarioId,
                            ["run.index"] = "0",
                            ["run.count"] = scenario.RunCount.ToString(),
                            ["step.count"] = scenario.StepCount.ToString(),
                            ["manual.execution.assistance"] = "true",
                            ["multi.helper.execution.assistance"] = "true"
                        }
                    });

                Assert.True(
                    submit.Success,
                    submit.FailureReason ?? $"Submit failed for shared run '{sharedRunId}'.");

                var primaryPumpResult = await RunRuntimeInstancePumpUntilEmptyAsync(
                    primaryRuntimeInstance,
                    queue,
                    store,
                    remoteSharedRunDispatcher,
                    scenario.MaxDispatchesPerPumpCycle);

                Assert.Equal(1, primaryPumpResult.SuccessfulDispatchCount);
                Assert.Equal(0, primaryPumpResult.FailedDispatchCount);

                var dispatchedRuns = await WaitForSharedRunsDispatchedAsync(
                    store,
                    $"shared-run-{scenario.ScenarioId}-",
                    expectedCount: 1,
                    scenario.SharedDispatchTimeout);

                var dispatchedRun = dispatchedRuns.Single();

                Assert.Equal(AiSharedRunStatus.Dispatched, dispatchedRun.Status);
                Assert.Equal(primaryRuntimeInstance.RuntimeInstanceId, dispatchedRun.AssignedRuntimeInstanceId);
                Assert.False(string.IsNullOrWhiteSpace(dispatchedRun.LocalRunId));

                var executionId = await WaitForRuntimeRunExecutionStartedAsync(
                    primaryRuntimeInstance,
                    runtimeInstances,
                    dispatchedRun.LocalRunId!,
                    TimeSpan.FromSeconds(30));

                var assistanceTasks = new List<Task<AiExecutionAssistancePumpResult>>();
                var assistanceLeases = new List<(RuntimeInstanceHarness Helper, AiExecutionAssistanceLease Lease)>();

                foreach (var helperRuntimeInstance in helperRuntimeInstances)
                {
                    var assistanceLease = new AiExecutionAssistanceLease
                    {
                        LeaseId = Guid.NewGuid().ToString("N"),
                        ExecutionId = executionId,
                        PrimaryRuntimeInstanceId = primaryRuntimeInstance.RuntimeInstanceId,
                        HelperRuntimeInstanceId = helperRuntimeInstance.RuntimeInstanceId,
                        MaxWorkers = 1,
                        Status = AiExecutionAssistanceStatus.Granted,
                        GrantedAtUtc = DateTimeOffset.UtcNow,
                        ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
                        Reason = "Manual multi-helper integration test assistance lease.",
                        Metadata = new Dictionary<string, string>
                        {
                            ["scenario"] = scenario.Name,
                            ["scenario.id"] = scenario.ScenarioId,
                            ["manual.execution.assistance"] = "true",
                            ["multi.helper.execution.assistance"] = "true"
                        }
                    };

                    await helperRuntimeInstance.ExecutionAssistanceStore.RegisterAsync(
                        assistanceLease);

                    assistanceLeases.Add((helperRuntimeInstance, assistanceLease));

                    assistanceTasks.Add(
                        helperRuntimeInstance.ExecutionAssistancePump.PumpAsync(
                            assistanceLease));
                }

                var record = await WaitForRuntimeRunExecutionCompletedAsync(
                    primaryRuntimeInstance,
                    runtimeInstances,
                    dispatchedRun.LocalRunId!,
                    scenario.ExecutionTimeout);

                var assistanceResults = await Task.WhenAll(assistanceTasks);

                Assert.Equal(AiExecutionStatus.Completed, record.Status);
                Assert.Equal(executionId, record.ExecutionId);

                Assert.Equal(helperRuntimeInstances.Length, assistanceResults.Length);

                foreach (var assistanceResult in assistanceResults)
                {
                    Assert.True(
                        assistanceResult.Success,
                        assistanceResult.FailureReason ?? "Manual assistance pump failed.");

                    Assert.Equal(executionId, assistanceResult.ExecutionId);
                    Assert.Equal(1, assistanceResult.StartedWorkerCount);
                }

                foreach (var (helperRuntimeInstance, lease) in assistanceLeases)
                {
                    var storedLease = await helperRuntimeInstance.ExecutionAssistanceStore.GetAsync(
                        lease.LeaseId);

                    Assert.NotNull(storedLease);
                    Assert.Equal(AiExecutionAssistanceStatus.Released, storedLease!.Status);
                }

                _output.WriteLine("");
                _output.WriteLine("MANUAL MULTI-HELPER EXECUTION ASSISTANCE TEST");
                _output.WriteLine("------------------------------------------------------------");
                _output.WriteLine($"ExecutionId:     {executionId}");
                _output.WriteLine($"PrimaryInstance: {primaryRuntimeInstance.RuntimeInstanceId}");
                _output.WriteLine($"HelperInstances: {string.Join(", ", helperRuntimeInstances.Select(x => x.RuntimeInstanceId))}");
                _output.WriteLine($"HelperWorkers:   {assistanceResults.Sum(x => x.StartedWorkerCount)}");

                foreach (var result in assistanceResults)
                {
                    _output.WriteLine(
                        $"HelperResult:    {result.HelperRuntimeInstanceId} | Lease={result.LeaseId} | Status={result.Status}");
                }
            }
            finally
            {
                foreach (var runtimeInstance in runtimeInstances)
                {
                    await runtimeInstance.DisposeAsync();
                }
            }
        }

        [RedisFact(Skip = "Temporarily disabled: heavy stress scenario is too long after tenant-aware/process-host hardening. Needs tenant/capacity/pipeline consistency review.")]
        public async Task MultiInstance_ExecutionAssistance_Baseline_OneLargeRun_500Steps_OneInstance_5Workers()
        {
            await RunHeavyScenarioAsync(
                HeavyScenario.AllFeaturesStress(
                    runCount: 1,
                    expectedMinimumParticipatingInstances: 1,
                    runtimeInstanceCount: 1,
                    workerCount: 5,
                    executionTimeout: TimeSpan.FromMinutes(5),
                    stepCount: 500,
                    enableRetention: true));
        }

        [RedisFact]
        public async Task MultiInstance_ExecutionAssistance_Baseline_OneLargeRun_500Steps_OneInstance_1Worker()
        {
            await RunHeavyScenarioAsync(
                HeavyScenario.AllFeaturesStress(
                    runCount: 1,
                    expectedMinimumParticipatingInstances: 1,
                    runtimeInstanceCount: 1,
                    workerCount: 1,
                    executionTimeout: TimeSpan.FromMinutes(8),
                    stepCount: 500,
                    enableRetention: true));
        }

        [RedisFact]
        public async Task MultiInstance_ExecutionAssistance_Baseline_OneLargeRun_500Steps_OneInstance_10Worker()
        {
            await RunHeavyScenarioAsync(
                HeavyScenario.AllFeaturesStress(
                    runCount: 1,
                    expectedMinimumParticipatingInstances: 1,
                    runtimeInstanceCount: 1,
                    workerCount: 10,
                    executionTimeout: TimeSpan.FromMinutes(8),
                    stepCount: 500,
                    enableRetention: true));
        }

        private async Task RunHeavyScenarioAsync(
            HeavyScenario scenario)
        {
            ArgumentNullException.ThrowIfNull(scenario);

            var store = CreateRunStore();
            var queue = CreateQueue();

            var sharedRuntimeInstanceRegistry =
                new InMemoryAiSharedRuntimeInstanceRegistry();

            var controller = new AiSharedRuntimeController(
                new QueueGloballyAdmissionController(
                    runtimeInstanceCount: scenario.RuntimeInstanceCount),
                store,
                queue,
                new NeverCalledSharedRunDispatcher(),
                new NoopAiRuntimeScaleOutRequestPublisher(),
                new StaticAiControlPlaneIdResolver("test-control-plane"),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                Options.Create(new AiSharedRuntimeControllerOptions()),
                new NoopAiControlPlaneObserver(),
                new FakeExecutionContextSnapshotProvider(
                    AiExecutionContextSnapshotTestFactory.Create(
                        tenantId: "tenant-1")));

            var runtimeInstances = new List<RuntimeInstanceHarness>();

            try
            {
                var capacityStore = new TestRuntimeInstanceCapacityStore();

                for (var index = 1; index <= scenario.RuntimeInstanceCount; index++)
                {
                    var runtimeInstanceId = $"runtime-instance-{index}";

                    var harness = await CreateRuntimeInstanceHarnessAsync(
                        scenario,
                        runtimeInstanceId,
                        sharedRuntimeInstanceRegistry);

                    runtimeInstances.Add(harness);

                    await capacityStore.PublishAsync(
                    new AiRuntimeInstanceCapacityDescriptor
                    {
                        RuntimeInstanceId = runtimeInstanceId,
                        Metadata = new Dictionary<string, string>
                        {
                            [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = "local"
                        }
                    });
                }


                var providerRouter =
                new AiRuntimeInstanceProviderRouter(
                    new IAiRuntimeInstanceProvider[]
                    {
                        new LocalAiRuntimeInstanceProvider(
                            sharedRuntimeInstanceRegistry)
                    });

                var providerCapabilityResolver =
                    new AiRuntimeInstanceProviderCapabilityResolver(
                        capacityStore,
                        providerRouter);

                var remoteSharedRunDispatcher =
                    new RemoteAiSharedRunDispatcher(
                        providerCapabilityResolver,
                        new InMemoryAiRuntimeInstanceRegistry(),
                        NullLogger<RemoteAiSharedRunDispatcher>.Instance);

                var sharedRunIdPrefix =
                    $"shared-run-{scenario.ScenarioId}-";

                for (var runIndex = 0; runIndex < scenario.RunCount; runIndex++)
                {
                    var sharedRunId =
                        $"{sharedRunIdPrefix}{runIndex:D5}";

                    var submit = await controller.SubmitRunAsync(
                        new AiSharedRuntimeControllerRequest
                        {
                            Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                            RequestedSharedRunId = sharedRunId,
                            RunRequest = new AiRuntimePipelineRunRequest
                            {
                                PipelineName = scenario.PipelineName,
                                PipelineDefinition = scenario.PipelineDefinition,
                                Input = new
                                {
                                    candidateId = $"{scenario.CandidateIdPrefix}-{runIndex:D5}",
                                    source = scenario.Name,
                                    runIndex,
                                    runCount = scenario.RunCount,
                                    stepCount = scenario.StepCount,
                                    sharedQueue = true,
                                    multiInstance = true,
                                    realExecution = true,
                                    retry = true,
                                    retention = true,
                                    compaction = true,
                                    eviction = true,
                                    throttling = true,
                                    snapshot = true,
                                    replay = true,
                                    ledger = true,
                                    timeline = true,
                                    metrics = true
                                }
                            },
                            TenantId = "tenant-real-heavy",
                            PipelineKey = scenario.PipelineName,
                            CorrelationId = $"correlation-{sharedRunId}",
                            RequestedBy = "integration-test",
                            Source = "multi-instance-real-heavy-test",
                            Reason = "Queue globally for heavy multi-instance real execution.",
                            Metadata = new Dictionary<string, string>
                            {
                                ["scenario"] = scenario.Name,
                                ["scenario.id"] = scenario.ScenarioId,
                                ["run.index"] = runIndex.ToString(),
                                ["run.count"] = scenario.RunCount.ToString(),
                                ["step.count"] = scenario.StepCount.ToString(),
                                ["features"] = "flaky,retry,retention,compact,evict,throttling,snapshot,replay,ledger,timeline,metrics"
                            }
                        });

                    Assert.True(
                        submit.Success,
                        submit.FailureReason ?? $"Submit failed for shared run '{sharedRunId}'.");

                    Assert.NotNull(submit.Run);
                    Assert.Equal(
                        AiSharedRunStatus.QueuedGlobally,
                        submit.Run.Status);
                }

                var queuedRuns = (await store.ListAsync(
                        includeCancelled: true,
                        includeCompleted: true,
                        includeFailed: true))
                    .Where(run => run.SharedRunId.StartsWith(
                        sharedRunIdPrefix,
                        StringComparison.Ordinal))
                    .ToArray();

                Assert.Equal(scenario.RunCount, queuedRuns.Length);

                Assert.All(
                    queuedRuns,
                    run => Assert.Equal(AiSharedRunStatus.QueuedGlobally, run.Status));

                foreach (var runtimeInstance in runtimeInstances)
                {
                    await runtimeInstance.BackgroundController.StartAsync();
                }

                var queuedItems = (await queue.ListAsync(
                        includeTerminal: true))
                    .Where(item => item.SharedRunId.StartsWith(
                        sharedRunIdPrefix,
                        StringComparison.Ordinal))
                    .ToArray();

                Assert.Equal(scenario.RunCount, queuedItems.Length);

                Assert.All(
                    queuedItems,
                    item => Assert.Equal(AiSharedQueueItemStatus.Pending, item.Status));

                var pumpTasks = runtimeInstances
                    .Select(runtimeInstance =>
                        RunRuntimeInstancePumpUntilEmptyAsync(
                            runtimeInstance,
                            queue,
                            store,
                            remoteSharedRunDispatcher,
                            scenario.MaxDispatchesPerPumpCycle))
                    .ToArray();

                var pumpResults = await Task.WhenAll(
                    pumpTasks);

                var successfulDispatchCount = pumpResults.Sum(
                    result => result.SuccessfulDispatchCount);

                var failedDispatchCount = pumpResults.Sum(
                    result => result.FailedDispatchCount);

                Assert.Equal(0, failedDispatchCount);
                Assert.Equal(scenario.RunCount, successfulDispatchCount);

                var finalRuns = await WaitForSharedRunsDispatchedAsync(
                    store,
                    sharedRunIdPrefix,
                    scenario.RunCount,
                    scenario.SharedDispatchTimeout);

                Assert.All(
                    finalRuns,
                    run =>
                    {
                        Assert.Equal(AiSharedRunStatus.Dispatched, run.Status);
                        Assert.False(string.IsNullOrWhiteSpace(run.AssignedRuntimeInstanceId));
                        Assert.False(string.IsNullOrWhiteSpace(run.LocalRunId));
                    });

                var duplicateDispatches = finalRuns
                    .GroupBy(run => run.SharedRunId, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .ToArray();

                Assert.Empty(duplicateDispatches);

                var finalQueueItems = (await queue.ListAsync(
                        includeTerminal: true))
                    .Where(item => item.SharedRunId.StartsWith(
                        sharedRunIdPrefix,
                        StringComparison.Ordinal))
                    .ToArray();

                Assert.Equal(scenario.RunCount, finalQueueItems.Length);

                Assert.All(
                    finalQueueItems,
                    item => Assert.Equal(AiSharedQueueItemStatus.Dispatched, item.Status));

                var runsByInstance = finalRuns
                    .GroupBy(run => run.AssignedRuntimeInstanceId ?? string.Empty, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.ToArray(),
                        StringComparer.Ordinal);

                Assert.True(
                    runsByInstance.Count >= scenario.ExpectedMinimumParticipatingInstances,
                    $"Expected at least '{scenario.ExpectedMinimumParticipatingInstances}' runtime instances to receive runs, " +
                    $"but only '{runsByInstance.Count}' participated.");

                var completedExecutions = new List<CompletedExecutionInfo>();

                foreach (var run in finalRuns.OrderBy(run => run.SharedRunId, StringComparer.Ordinal))
                {
                    Assert.False(string.IsNullOrWhiteSpace(run.AssignedRuntimeInstanceId));
                    Assert.False(string.IsNullOrWhiteSpace(run.LocalRunId));

                    var runtimeInstance = runtimeInstances.Single(
                        instance => string.Equals(
                            instance.RuntimeInstanceId,
                            run.AssignedRuntimeInstanceId,
                            StringComparison.Ordinal));

                    var record = await WaitForRuntimeRunExecutionCompletedAsync(
                        runtimeInstance,
                        runtimeInstances,
                        run.LocalRunId!,
                        scenario.ExecutionTimeout);

                    var executionId = record.ExecutionId;

                    Assert.False(string.IsNullOrWhiteSpace(executionId));

                    var state = await runtimeInstance.DagStore.GetStateAsync(
                        executionId);

                    Assert.NotNull(state);

                    await runtimeInstance.Resolver.WarmAsync(
                        executionId,
                        state!,
                        CancellationToken.None);

                    await AssertRequiredStepsResolvedAsync(
                        scenario,
                        executionId,
                        state!,
                        runtimeInstance.Resolver);

                    AssertNoStaleClaims(
                        state!);

                    AssertRetentionDidNotBreakState(
                        scenario,
                        state!);

                    await AssertSnapshotExistsAsync(
                        runtimeInstance,
                        executionId,
                        scenario.SnapshotTimeout);

                    var ledgerEntries = await runtimeInstance.Ledger.GetByExecutionAsync(
                        executionId);

                    var timelineEvents = runtimeInstance.Timeline.Get(
                        executionId);

                    Assert.NotEmpty(ledgerEntries);
                    Assert.NotEmpty(timelineEvents);

                    AssertLedgerCategoryContainsAny(
                        ledgerEntries,
                        AiDecisionLedgerCategory.Execution,
                        "execution.");

                    AssertLedgerCategoryContainsAny(
                        ledgerEntries,
                        AiDecisionLedgerCategory.Run,
                        "run.");

                    AssertLedgerCategoryContainsAny(
                        ledgerEntries,
                        AiDecisionLedgerCategory.Claim,
                        "claim.");

                    AssertLedgerCategoryContainsAny(
                        ledgerEntries,
                        AiDecisionLedgerCategory.Step,
                        "step.");

                    if (scenario.ExpectedRetriedSteps.Count > 0)
                    {
                        AssertLedgerCategoryContainsAny(
                            ledgerEntries,
                            AiDecisionLedgerCategory.Retry,
                            "retry.");
                    }

                    if (scenario.RetentionPolicies.Count > 0)
                    {
                        AssertLedgerCategoryContainsAny(
                            ledgerEntries,
                            AiDecisionLedgerCategory.Retention,
                            "retention.");
                    }

                    completedExecutions.Add(
                        new CompletedExecutionInfo(
                            run.SharedRunId,
                            run.AssignedRuntimeInstanceId!,
                            run.LocalRunId!,
                            executionId,
                            runtimeInstance,
                            record));
                }

                Assert.Equal(
                    scenario.RunCount,
                    completedExecutions.Count);

                foreach (var runtimeInstance in runtimeInstances)
                {
                    AssertDistributedWorkerMetrics(
                        runtimeInstance,
                        scenario);
                }

                var replaySamples = SelectReplaySamples(
                    completedExecutions,
                    scenario.ReplaySampleCount);

                Assert.NotEmpty(replaySamples);

                foreach (var sample in replaySamples)
                {
                    await ValidateReplayAsync(
                        scenario,
                        sample);
                }

                PrintHeavySummary(
                    scenario,
                    completedExecutions,
                    replaySamples);
            }
            finally
            {
                foreach (var runtimeInstance in runtimeInstances)
                {
                    await runtimeInstance.DisposeAsync();
                }
            }
        }

        private async Task<RuntimeInstanceHarness> CreateRuntimeInstanceHarnessAsync(
            HeavyScenario scenario,
            string runtimeInstanceId,
            IAiSharedRuntimeInstanceRegistry sharedRuntimeInstanceRegistry)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentNullException.ThrowIfNull(sharedRuntimeInstanceRegistry);

            var host = await AiDagExecutionEngineFixture.CreateAsync(
                CreateOptions(
                    scenario,
                    runtimeInstanceId),
                configureServices: services =>
                {
                    var finalizedHook = new DistributedChaosRunFinalizedHook();

                    services.AddSingleton(finalizedHook);
                    services.AddSingleton<IAiRuntimePipelineRunLifecycleHook>(
                        finalizedHook);

                    services.AddInMemoryAiDecisionLedger();

                    services.AddAiControlPlane();

                    services.AddAiStepsFromAssemblies(
                        typeof(AiRuntimeAssemblyMarker).Assembly,
                        typeof(AiSharedQueueMultiInstanceRealExecutionHeavyIntegrationTests).Assembly,
                        typeof(DistributedChaosFlakyProviderStep).Assembly);
                });

            var queueControlPlane =
                host.ServiceProvider.GetRequiredService<IAiRuntimeQueueControlPlane>();

            await sharedRuntimeInstanceRegistry.RegisterAsync(
                new LocalAiSharedRuntimeInstance(
                    runtimeInstanceId,
                    queueControlPlane));

            return new RuntimeInstanceHarness(
                runtimeInstanceId,
                host,
                host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>(),
                queueControlPlane,
                host.ServiceProvider.GetRequiredService<IAiSharedRunDispatcher>(),
                host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>(),
                host.ServiceProvider.GetRequiredService<IAiExecutionStepResolver>(),
                host.ServiceProvider.GetRequiredService<IAiExecutionReplayService>(),
                host.ServiceProvider.GetRequiredService<IAiExecutionSnapshotStore<ExecutionContextSnapshot>>(),
                host.ServiceProvider.GetRequiredService<IAiDecisionLedger>(),
                host.ServiceProvider.GetRequiredService<IAiTraceTimeline>(),
                host.ServiceProvider.GetRequiredService<IAiRuntimeMetrics>(),
                host.ServiceProvider.GetRequiredService<IAiExecutionAssistanceStore>(),
                host.ServiceProvider.GetRequiredService<AiExecutionAssistancePump>(),
                host.ServiceProvider.GetRequiredService<IRuntimeAiPipelineDefinitionProvider>());
        }

        private async Task<AiSharedQueuePumpResult> RunRuntimeInstancePumpUntilEmptyAsync(
            RuntimeInstanceHarness runtimeInstance,
            IAiSharedQueue queue,
            IAiSharedRunStore store,
            IAiSharedRunDispatcher sharedRunDispatcher,
            int maxDispatchesPerPumpCycle)
        {
            ArgumentNullException.ThrowIfNull(runtimeInstance);
            ArgumentNullException.ThrowIfNull(queue);
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(sharedRunDispatcher);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDispatchesPerPumpCycle);

            var queueDispatcher = new AiSharedQueueDispatcher(
                queue,
                store,
                sharedRunDispatcher,
                new FakeRunAdmissionController(
                    assignedRuntimeInstanceId: runtimeInstance.RuntimeInstanceId), 
                new InMemoryAiRuntimeAdmissionReservationStore(),
                new InMemoryAiRuntimeInstanceRegistry(),
                new FakeRuntimeScaleOutRequestPublisher(),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                new FakeExecutionContextAccessor(),
                NullLogger<AiSharedQueueDispatcher>.Instance);

            var pump = new AiSharedQueuePump(
                queueDispatcher,
                Options.Create(new AiSharedQueuePumpOptions
                {
                    Enabled = true,
                    MaxDispatchesPerCycle = maxDispatchesPerPumpCycle,
                    DefaultClaimTtl = TimeSpan.FromSeconds(30),
                    StopCycleWhenNoItemAvailable = true,
                    StopCycleOnDispatchFailure = true,
                    WorkerId = $"{runtimeInstance.RuntimeInstanceId}-shared-queue-worker",
                    Source = "multi-instance-real-heavy-test"
                }),
                NullLogger<AiSharedQueuePump>.Instance);

            var startedAtUtc = DateTimeOffset.UtcNow;

            var attemptedDispatchCount = 0;
            var successfulDispatchCount = 0;
            var failedDispatchCount = 0;
            var stoppedBecauseNoItemAvailable = false;

            var cycles = 0;

            while (true)
            {
                cycles++;

                var result = await pump.PumpOnceAsync(
                    new AiSharedQueuePumpRequest
                    {
                        PumpRuntimeInstanceId = runtimeInstance.RuntimeInstanceId,
                        PumpWorkerId = $"{runtimeInstance.RuntimeInstanceId}-shared-queue-worker",
                        MaxDispatches = maxDispatchesPerPumpCycle,
                        ClaimTtl = TimeSpan.FromSeconds(30),
                        CorrelationId = $"correlation-{runtimeInstance.RuntimeInstanceId}-{cycles}",
                        RequestedBy = "integration-test",
                        Source = "multi-instance-real-heavy-test",
                        Reason = "Runtime instance consumes shared queue and dispatches through remote shared runtime registry.",
                        Metadata = new Dictionary<string, string>
                        {
                            ["runtime.instance.id"] = runtimeInstance.RuntimeInstanceId,
                            ["cycle"] = cycles.ToString(),
                            ["real.dispatch"] = "true",
                            ["remote.dispatch"] = "true"
                        }
                    });

                Assert.True(
                    result.Success,
                    result.FailureReason ?? $"Pump failed for runtime instance '{runtimeInstance.RuntimeInstanceId}'.");

                failedDispatchCount += result.FailedDispatchCount;
                attemptedDispatchCount += result.AttemptedDispatchCount;
                successfulDispatchCount += result.SuccessfulDispatchCount;
                stoppedBecauseNoItemAvailable = result.StoppedBecauseNoItemAvailable;

                if (result.StoppedBecauseNoItemAvailable)
                {
                    return new AiSharedQueuePumpResult
                    {
                        Success = true,
                        RuntimeInstanceId = runtimeInstance.RuntimeInstanceId,
                        AttemptedDispatchCount = attemptedDispatchCount,
                        SuccessfulDispatchCount = successfulDispatchCount,
                        FailedDispatchCount = failedDispatchCount,
                        StoppedBecauseNoItemAvailable = stoppedBecauseNoItemAvailable,
                        StartedAtUtc = startedAtUtc,
                        CompletedAtUtc = DateTimeOffset.UtcNow
                    };
                }

                Assert.True(
                    cycles <= 10_000,
                    $"Pump for runtime instance '{runtimeInstance.RuntimeInstanceId}' did not finish.");
            }
        }

        private static async Task<AiExecutionRecord> WaitForRuntimeRunExecutionCompletedAsync(
            RuntimeInstanceHarness expectedRuntimeInstance,
            IReadOnlyCollection<RuntimeInstanceHarness> allRuntimeInstances,
            string localRunId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(expectedRuntimeInstance);
            ArgumentNullException.ThrowIfNull(allRuntimeInstances);
            ArgumentException.ThrowIfNullOrWhiteSpace(localRunId);

            var deadline = DateTimeOffset.UtcNow.Add(timeout);

            string? executionId = null;
            string? lastRunStatus = null;
            AiExecutionStatus? lastExecutionStatus = null;
            string? runtimeInstanceWhereRunWasFound = null;

            var localRunWasSeen = false;
            var localRunDisappearedAfterBeingSeen = false;

            while (DateTimeOffset.UtcNow < deadline)
            {
                foreach (var runtimeInstance in allRuntimeInstances)
                {
                    var result = await runtimeInstance.QueueControlPlane.GetRunStatusAsync(
                        new AiRuntimeQueueControlPlaneRequest
                        {
                            Operation = AiRuntimeQueueControlPlaneOperation.GetRunStatus,
                            RunId = localRunId,
                            RequestedBy = "integration-test",
                            Source = "multi-instance-real-heavy-test"
                        });

                    if (result.RunState is null)
                    {
                        if (localRunWasSeen)
                        {
                            localRunDisappearedAfterBeingSeen = true;
                        }

                        continue;
                    }

                    localRunWasSeen = true;
                    runtimeInstanceWhereRunWasFound = runtimeInstance.RuntimeInstanceId;
                    lastRunStatus = result.RunState.Status;

                    if (!string.IsNullOrWhiteSpace(result.RunState.ExecutionId))
                    {
                        executionId = result.RunState.ExecutionId;
                    }

                    if (string.Equals(lastRunStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(lastRunStatus, "cancelled", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(lastRunStatus, "canceled", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Local run '{localRunId}' reached terminal failure status '{lastRunStatus}' " +
                            $"on runtime instance '{runtimeInstance.RuntimeInstanceId}'. " +
                            $"FailureReason='{result.RunState.FailureReason}'.");
                    }

                    if (!string.IsNullOrWhiteSpace(executionId))
                    {
                        var record = await runtimeInstance.DagStore.GetRecordAsync(
                            executionId);

                        if (record is not null)
                        {
                            lastExecutionStatus = record.Status;

                            if (record.Status == AiExecutionStatus.Completed)
                            {
                                return record;
                            }

                            if (record.Status == AiExecutionStatus.Failed ||
                                record.Status == AiExecutionStatus.Cancelled)
                            {
                                throw new InvalidOperationException(
                                    $"Execution '{executionId}' reached terminal status '{record.Status}' " +
                                    $"on runtime instance '{runtimeInstance.RuntimeInstanceId}'.");
                            }
                        }
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            var stepStatusSummary = "none";
            var stepClaimSummary = "none";
            var stepRetrySummary = "none";

            if (!string.IsNullOrWhiteSpace(executionId))
            {
                foreach (var runtimeInstance in allRuntimeInstances)
                {
                    var state = await runtimeInstance.DagStore.GetStateAsync(
                        executionId);

                    if (state is null)
                    {
                        continue;
                    }

                    stepStatusSummary = string.Join(
                        ", ",
                        state.Steps.Values
                            .GroupBy(step => step.Status)
                            .OrderBy(group => group.Key.ToString(), StringComparer.Ordinal)
                            .Select(group => $"{group.Key}={group.Count()}"));

                    stepClaimSummary = string.Join(
                        ", ",
                        state.Steps.Values
                            .GroupBy(step =>
                            {
                                if (string.IsNullOrWhiteSpace(step.ClaimToken))
                                {
                                    return "unclaimed";
                                }

                                if (step.LeaseExpiresAtUtc.HasValue &&
                                    step.LeaseExpiresAtUtc.Value <= DateTimeOffset.UtcNow)
                                {
                                    return "expired-claim";
                                }

                                return "active-claim";
                            })
                            .OrderBy(group => group.Key, StringComparer.Ordinal)
                            .Select(group => $"{group.Key}={group.Count()}"));

                    stepRetrySummary = string.Join(
                        ", ",
                        state.Steps.Values
                            .Where(step => step.RetryState is not null)
                            .GroupBy(step => step.RetryState!.RetryCount)
                            .OrderBy(group => group.Key)
                            .Select(group => $"retry-{group.Key}={group.Count()}"));

                    if (string.IsNullOrWhiteSpace(stepRetrySummary))
                    {
                        stepRetrySummary = "none";
                    }

                    break;
                }
            }

            throw new TimeoutException(
                $"Local run '{localRunId}' did not complete within '{timeout}'. " +
                $"ExpectedRuntimeInstance='{expectedRuntimeInstance.RuntimeInstanceId}', " +
                $"RuntimeInstanceWhereRunWasFound='{runtimeInstanceWhereRunWasFound ?? "none"}', " +
                $"LocalRunWasSeen='{localRunWasSeen}', " +
                $"LocalRunDisappearedAfterBeingSeen='{localRunDisappearedAfterBeingSeen}', " +
                $"LastRunStatus='{lastRunStatus ?? "none"}', " +
                $"ExecutionId='{executionId ?? "none"}', " +
                $"LastExecutionStatus='{lastExecutionStatus?.ToString() ?? "none"}', " +
                $"StepStatusSummary='{stepStatusSummary}', " +
                $"StepClaimSummary='{stepClaimSummary}', " +
                $"StepRetrySummary='{stepRetrySummary}'.");
        }

        /// <summary>
        /// Publishes the scenario pipeline definition to every runtime instance.
        /// </summary>
        /// <param name="runtimeInstances">The runtime instances.</param>
        /// <param name="scenario">The heavy test scenario.</param>
        private static void PublishPipelineDefinitionToAllRuntimeInstances(
            IEnumerable<RuntimeInstanceHarness> runtimeInstances,
            HeavyScenario scenario)
        {
            ArgumentNullException.ThrowIfNull(runtimeInstances);
            ArgumentNullException.ThrowIfNull(scenario);

            foreach (var runtimeInstance in runtimeInstances)
            {
                runtimeInstance.PipelineDefinitionProvider.Upsert(
                    scenario.PipelineDefinition);
            }
        }

        /// <summary>
        /// Waits until a local runtime run exposes an execution identifier.
        /// </summary>
        /// <param name="expectedRuntimeInstance">The expected primary runtime instance.</param>
        /// <param name="allRuntimeInstances">All runtime instances participating in the test.</param>
        /// <param name="localRunId">The local runtime run identifier.</param>
        /// <param name="timeout">The maximum wait duration.</param>
        /// <returns>The execution identifier associated with the local run.</returns>
        private static async Task<string> WaitForRuntimeRunExecutionStartedAsync(
            RuntimeInstanceHarness expectedRuntimeInstance,
            IReadOnlyCollection<RuntimeInstanceHarness> allRuntimeInstances,
            string localRunId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(expectedRuntimeInstance);
            ArgumentNullException.ThrowIfNull(allRuntimeInstances);
            ArgumentException.ThrowIfNullOrWhiteSpace(localRunId);

            var deadline = DateTimeOffset.UtcNow.Add(timeout);

            string? lastRunStatus = null;
            string? runtimeInstanceWhereRunWasFound = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                foreach (var runtimeInstance in allRuntimeInstances)
                {
                    var result = await runtimeInstance.QueueControlPlane.GetRunStatusAsync(
                        new AiRuntimeQueueControlPlaneRequest
                        {
                            Operation = AiRuntimeQueueControlPlaneOperation.GetRunStatus,
                            RunId = localRunId,
                            RequestedBy = "integration-test",
                            Source = "multi-instance-execution-assistance-test"
                        });

                    if (result.RunState is null)
                    {
                        continue;
                    }

                    runtimeInstanceWhereRunWasFound = runtimeInstance.RuntimeInstanceId;
                    lastRunStatus = result.RunState.Status;

                    if (!string.IsNullOrWhiteSpace(result.RunState.ExecutionId))
                    {
                        return result.RunState.ExecutionId;
                    }

                    if (string.Equals(lastRunStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(lastRunStatus, "cancelled", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(lastRunStatus, "canceled", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Local run '{localRunId}' reached terminal failure status '{lastRunStatus}' " +
                            $"on runtime instance '{runtimeInstance.RuntimeInstanceId}'. " +
                            $"FailureReason='{result.RunState.FailureReason}'.");
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }

            throw new TimeoutException(
                $"Local run '{localRunId}' did not expose an execution id within '{timeout}'. " +
                $"ExpectedRuntimeInstance='{expectedRuntimeInstance.RuntimeInstanceId}', " +
                $"RuntimeInstanceWhereRunWasFound='{runtimeInstanceWhereRunWasFound ?? "none"}', " +
                $"LastRunStatus='{lastRunStatus ?? "none"}'.");
        }

        private async Task<AiSharedRunRecord[]> WaitForSharedRunsDispatchedAsync(
            IAiSharedRunStore store,
            string sharedRunIdPrefix,
            int expectedCount,
            TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);

            AiSharedRunRecord[] lastRuns = Array.Empty<AiSharedRunRecord>();

            while (DateTimeOffset.UtcNow < deadline)
            {
                lastRuns = (await store.ListAsync(
                        includeCancelled: true,
                        includeCompleted: true,
                        includeFailed: true))
                    .Where(run => run.SharedRunId.StartsWith(
                        sharedRunIdPrefix,
                        StringComparison.Ordinal))
                    .ToArray();

                if (lastRuns.Length == expectedCount &&
                    lastRuns.All(run => run.Status == AiSharedRunStatus.Dispatched))
                {
                    return lastRuns;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            throw new TimeoutException(
                $"Expected '{expectedCount}' shared runs to be dispatched within '{timeout}', " +
                $"but found '{lastRuns.Length}' matching runs. " +
                $"Statuses: {string.Join(", ", lastRuns.GroupBy(run => run.Status).Select(group => $"{group.Key}={group.Count()}"))}");
        }

        private async Task AssertSnapshotExistsAsync(
            RuntimeInstanceHarness runtimeInstance,
            string executionId,
            TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);

            while (DateTimeOffset.UtcNow < deadline)
            {
                var snapshot = await runtimeInstance.SnapshotStore.GetAsync(
                    executionId);

                if (snapshot is not null)
                {
                    Assert.Equal(executionId, snapshot.ExecutionId);
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            throw new TimeoutException(
                $"Snapshot for execution '{executionId}' was not found within '{timeout}'.");
        }

        private async Task ValidateReplayAsync(
            HeavyScenario scenario,
            CompletedExecutionInfo execution)
        {
            var stateBeforeReplay = await execution.RuntimeInstance.DagStore.GetStateAsync(
                execution.ExecutionId);

            var recordBeforeReplay = await execution.RuntimeInstance.DagStore.GetRecordAsync(
                execution.ExecutionId);

            Assert.NotNull(stateBeforeReplay);
            Assert.NotNull(recordBeforeReplay);

            await execution.RuntimeInstance.Resolver.WarmAsync(
                execution.ExecutionId,
                stateBeforeReplay!,
                CancellationToken.None);

            var beforeFingerprint = await CreateReplayFingerprintAsync(
                scenario,
                execution.ExecutionId,
                recordBeforeReplay!,
                stateBeforeReplay!,
                execution.RuntimeInstance.Resolver);

            await CleanupDagExecutionAsync(
                execution.RuntimeInstance,
                execution.ExecutionId);

            Assert.Null(await execution.RuntimeInstance.DagStore.GetRecordAsync(execution.ExecutionId));
            Assert.Null(await execution.RuntimeInstance.DagStore.GetStateAsync(execution.ExecutionId));

            var replayResult = await execution.RuntimeInstance.ReplayService.ReplayAsync(
                new AiExecutionReplayRequest
                {
                    ExecutionId = execution.ExecutionId,
                    Mode = AiExecutionReplayMode.ResumeIncomplete,
                    IncludeLedgerEvents = true,
                    IncludeTimeline = true,
                    IncludeStepDetails = true,
                    ValidatePayloadReferences = true
                });

            Assert.NotNull(replayResult);
            Assert.True(replayResult.ReplayValid);
            Assert.True(replayResult.ExecutionFound);
            Assert.True(replayResult.SnapshotFound);
            Assert.True(replayResult.FingerprintFound);
            Assert.True(replayResult.FingerprintMatches);
            Assert.True(replayResult.DependencyGraphValid);
            Assert.True(replayResult.StepStateValid);
            Assert.True(replayResult.PayloadReferencesValid);

            var restoredRecord = await execution.RuntimeInstance.DagStore.GetRecordAsync(
                execution.ExecutionId);

            var restoredState = await execution.RuntimeInstance.DagStore.GetStateAsync(
                execution.ExecutionId);

            Assert.NotNull(restoredRecord);
            Assert.NotNull(restoredState);

            await execution.RuntimeInstance.Resolver.WarmAsync(
                execution.ExecutionId,
                restoredState!,
                CancellationToken.None);

            var afterFingerprint = await CreateReplayFingerprintAsync(
                scenario,
                execution.ExecutionId,
                restoredRecord!,
                restoredState!,
                execution.RuntimeInstance.Resolver);

            Assert.Equal(beforeFingerprint.Status, afterFingerprint.Status);
            Assert.Equal(beforeFingerprint.IsTerminal, afterFingerprint.IsTerminal);
            Assert.Equal(beforeFingerprint.CompletedSteps, afterFingerprint.CompletedSteps);
            Assert.Equal(beforeFingerprint.StepStatuses, afterFingerprint.StepStatuses);
            Assert.Equal(beforeFingerprint.RetryCounts, afterFingerprint.RetryCounts);
            Assert.Equal(beforeFingerprint.RequiredResolvedSteps, afterFingerprint.RequiredResolvedSteps);

            var ledgerAfterReplay = await execution.RuntimeInstance.Ledger.GetByExecutionAsync(
                execution.ExecutionId);

            AssertLedgerCategoryContainsAny(
                ledgerAfterReplay,
                AiDecisionLedgerCategory.Replay,
                "replay.");
        }

        private static async Task CleanupDagExecutionAsync(
            RuntimeInstanceHarness runtimeInstance,
            string executionId)
        {
            await runtimeInstance.DagStore.DeleteExecutionBundleAsync(
                executionId);
        }

        private void AssertDistributedWorkerMetrics(
            RuntimeInstanceHarness runtimeInstance,
            HeavyScenario scenario)
        {
            var workerCycles = runtimeInstance.Metrics.Worker.GetCyclesByRuntimeInstance();

            Assert.NotEmpty(workerCycles);

            _output.WriteLine("");
            _output.WriteLine($"WORKER METRICS FOR {runtimeInstance.RuntimeInstanceId}");
            _output.WriteLine("------------------------------------------------------------");

            foreach (var item in workerCycles.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                _output.WriteLine(
                    $"{item.Key,-80} | Cycles={item.Value}");
            }
        }

        private static async Task AssertRequiredStepsResolvedAsync(
            HeavyScenario scenario,
            string executionId,
            AiExecutionState state,
            IAiExecutionStepResolver resolver)
        {
            foreach (var stepName in scenario.RequiredResolvedSteps)
            {
                var step = await resolver.GetStepAsync(
                    executionId,
                    stepName,
                    state,
                    CancellationToken.None);

                Assert.NotNull(step);
                Assert.Equal(AiStepExecutionStatus.Completed, step!.Status);
            }

            foreach (var stepName in scenario.ExpectedRetriedSteps)
            {
                var step = await resolver.GetStepAsync(
                    executionId,
                    stepName,
                    state,
                    CancellationToken.None);

                Assert.NotNull(step);
                Assert.Equal(AiStepExecutionStatus.Completed, step!.Status);

                Assert.True(
                    step.RetryState?.RetryCount >= 1,
                    $"Expected step '{stepName}' to have RetryCount >= 1.");
            }
        }

        private static void AssertNoStaleClaims(
            AiExecutionState state)
        {
            Assert.All(
                state.Steps.Values,
                step =>
                {
                    Assert.Null(step.ClaimedBy);
                    Assert.Null(step.ClaimToken);
                    Assert.Null(step.ClaimedAtUtc);
                    Assert.Null(step.LeaseExpiresAtUtc);
                });
        }

        private static void AssertRetentionDidNotBreakState(
            HeavyScenario scenario,
            AiExecutionState state)
        {
            Assert.NotNull(state.Steps);

            Assert.True(
                state.Steps.Count <= scenario.StepCount);

            Assert.All(
                state.Steps.Values,
                step =>
                {
                    Assert.True(
                        step.Status == AiStepExecutionStatus.Completed ||
                        step.Status == AiStepExecutionStatus.Failed);
                });
        }

        private static void AssertLedgerCategoryContainsAny(
            IReadOnlyCollection<AiDecisionLedgerEntry> entries,
            AiDecisionLedgerCategory category,
            string eventPrefix)
        {
            Assert.Contains(
                entries,
                entry =>
                    entry.Category == category &&
                    entry.EventType.StartsWith(
                        eventPrefix,
                        StringComparison.OrdinalIgnoreCase));
        }

        private static CompletedExecutionInfo[] SelectReplaySamples(
            IReadOnlyCollection<CompletedExecutionInfo> executions,
            int sampleCount)
        {
            var byInstance = executions
                .GroupBy(item => item.RuntimeInstanceId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();

            var remaining = executions
                .Where(item => byInstance.All(sample =>
                    !string.Equals(sample.ExecutionId, item.ExecutionId, StringComparison.Ordinal)))
                .Take(Math.Max(0, sampleCount - byInstance.Count));

            return byInstance
                .Concat(remaining)
                .Take(sampleCount)
                .ToArray();
        }

        private void PrintHeavySummary(
            HeavyScenario scenario,
            IReadOnlyCollection<CompletedExecutionInfo> executions,
            IReadOnlyCollection<CompletedExecutionInfo> replaySamples)
        {
            var distribution = executions
                .GroupBy(item => item.RuntimeInstanceId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Count(),
                    StringComparer.Ordinal);

            _output.WriteLine("");
            _output.WriteLine("============================================================");
            _output.WriteLine("MULTI-INSTANCE REAL HEAVY EXECUTION TEST");
            _output.WriteLine("============================================================");
            _output.WriteLine($"Scenario:             {scenario.Name}");
            _output.WriteLine($"ScenarioId:           {scenario.ScenarioId}");
            _output.WriteLine($"Runs:                 {scenario.RunCount}");
            _output.WriteLine($"StepsPerRun:          {scenario.StepCount}");
            _output.WriteLine($"NominalSteps:         {scenario.RunCount * scenario.StepCount}");
            _output.WriteLine($"RuntimeInstances:     {scenario.RuntimeInstanceCount}");
            _output.WriteLine($"WorkersPerInstance:   {scenario.WorkerCount}");
            _output.WriteLine($"ReplaySamples:        {replaySamples.Count}");
            _output.WriteLine("");
            _output.WriteLine("DISTRIBUTION BY RUNTIME INSTANCE");
            _output.WriteLine("------------------------------------------------------------");

            foreach (var item in distribution.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                _output.WriteLine(
                    $"{item.Key,-35} | Runs={item.Value}");
            }

            _output.WriteLine("");
            _output.WriteLine("REPLAY SAMPLES");
            _output.WriteLine("------------------------------------------------------------");

            foreach (var sample in replaySamples.OrderBy(item => item.RuntimeInstanceId, StringComparer.Ordinal))
            {
                _output.WriteLine(
                    $"{sample.RuntimeInstanceId,-35} | SharedRunId={sample.SharedRunId} | ExecutionId={sample.ExecutionId}");
            }
        }

        private RedisAiSharedRunStore CreateRunStore()
        {
            if (_connection is null)
            {
                throw new InvalidOperationException("Redis connection was not initialized.");
            }

            return new RedisAiSharedRunStore(
                _connection,
                Options.Create(new RedisAiSharedRunStoreOptions
                {
                    KeyPrefix = _runKeyPrefix,
                    ListScanLimit = 20_000
                }),
                new StaticAiControlPlaneIdResolver("test-control-plane"));
        }

        private RedisAiSharedQueue CreateQueue()
        {
            if (_connection is null)
            {
                throw new InvalidOperationException("Redis connection was not initialized.");
            }

            return new RedisAiSharedQueue(
                _connection,
                Options.Create(new RedisAiSharedQueueOptions
                {
                    KeyPrefix = _queueKeyPrefix,
                    ListScanLimit = 20_000
                }),
                new StaticAiControlPlaneIdResolver("test-control-plane"));
        }

        private static AiEngineOptions CreateOptions(
            HeavyScenario scenario,
            string runtimeInstanceId)
        {
            var options = new AiEngineOptions
            {
                DefaultPipelineDefinitionSource = "Runtime",
                RuntimeInstanceWorker = new AiRuntimeInstanceWorkerOptions
                {
                    MaxStepsPerCycle = scenario.MaxStepsPerCycle,
                    IdleDelay = scenario.WorkerIdleDelay,
                    MaxCycles = scenario.MaxWorkerCycles,
                    IgnoreConcurrencyConflicts = true
                },
                PipelineBackgroundController = new AiRuntimePipelineBackgroundControllerOptions
                {
                    MaxConcurrentRuns = scenario.MaxConcurrentRunsPerInstance,
                    QueueCapacity = scenario.QueueCapacityPerInstance,
                    RejectEnqueueWhenStopped = false,
                    StopOnFirstFailure = false,
                    MaxLocalWorkersPerExecution = scenario.WorkerCount,
                    Distributed = new AiRuntimeDistributedExecutionOptions
                    {
                        Enabled = true,
                        WorkerCount = scenario.WorkerCount,
                        StopOnFirstTerminal = true,
                        TerminalObservationTimeout = TimeSpan.FromSeconds(60)
                    }
                }
            };

            options.Observability.EnableTracing = true;
            options.Observability.EnableInMemoryRecording = true;
            options.Observability.EnableMetrics = true;

            options.Observability.DecisionLedger.WriteMode = AiDecisionLedgerWriteMode.BestEffort;
            options.Observability.DecisionLedger.StorageMode = AiDecisionLedgerStorageMode.InMemory;

            options.Snapshots.Enabled = true;
            options.Snapshots.Mongo.Enabled = true;
            options.Snapshots.Mongo.ConnectionString = "mongodb://localhost:27017";
            options.Snapshots.Mongo.DatabaseName = "multiplexed_ai_tests";
            options.Snapshots.Mongo.CollectionName =
                $"execution_snapshots_real_heavy_{runtimeInstanceId}_{Guid.NewGuid():N}";

            options.Cleanup.AutoCleanupOnCompleted = false;
            options.Cleanup.AutoCleanupOnFailed = false;
            options.Cleanup.SuppressSnapshotIfExist = true;
            options.Cleanup.SuppressCleanupExceptions = true;

            return options;
        }

        private static AiPipelineDefinition CreatePipelineDefinition(
            HeavyScenario scenario)
        {
            var steps = new List<AiPipelineStepDefinition>
            {
                new()
                {
                    Name = "chaos-step-001",
                    StepKey = "hello-world",
                    Order = 1,
                    Config = CreateStepConfig(
                        scenario,
                        index: 1,
                        isFlaky: false)
                }
            };

            for (var index = 2; index < scenario.StepCount; index++)
            {
                var isFlaky =
                    index % scenario.FlakyStepInterval == 0;

                steps.Add(
                    new AiPipelineStepDefinition
                    {
                        Name = $"chaos-step-{index:000}",
                        StepKey = isFlaky
                            ? "distributed.chaos.flaky-provider"
                            : "hello-world",
                        Order = index,
                        DependsOn = new[] { "chaos-step-001" },
                        Config = CreateStepConfig(
                            scenario,
                            index,
                            isFlaky)
                    });
            }

            steps.Add(
                new AiPipelineStepDefinition
                {
                    Name = "final-join-step",
                    StepKey = "hello-world",
                    Order = scenario.StepCount,
                    DependsOn = Enumerable.Range(2, scenario.StepCount - 2)
                        .Select(index => $"chaos-step-{index:000}")
                        .ToArray(),
                    Config = new Dictionary<string, object?>
                    {
                        ["provider"] = "openai",
                        ["model"] = "gpt-4.1",
                        ["operation"] = "llm.compose",
                        ["delayMs"] = 5
                    }
                });

            return new AiPipelineDefinition
            {
                Name = scenario.PipelineName,
                Version = "1.0.0",
                ExecutionMode = AiExecutionMode.Dag,
                Config = new Dictionary<string, object?>
                {
                    ["concurrency"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["maxDegreeOfParallelism"] = scenario.MaxDegreeOfParallelism,
                        ["maxProviderConcurrency"] = scenario.MaxProviderConcurrency,
                        ["leaseSeconds"] = 60,
                        ["defaultRetryAfterMs"] = 10,
                        ["jitter"] = false
                    },
                    ["retention"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = scenario.EnableRetention,
                        ["policies"] = scenario.RetentionPolicies.ToArray(),
                        ["archiveReason"] = scenario.RetentionArchiveReason,
                        ["trigger"] = new Dictionary<string, object?>
                        {
                            ["enabled"] = scenario.EnableRetention,
                            ["maxStepsInState"] = scenario.MaxCompletedStepsInState,
                            ["maxCompletedStepsInState"] = scenario.MaxCompletedStepsInState,
                            ["maxInlinePayloadBytes"] = scenario.MaxInlinePayloadBytes
                        }
                    }
                },
                Steps = steps
            };
        }

        private static Dictionary<string, object?> CreateStepConfig(
            HeavyScenario scenario,
            int index,
            bool isFlaky)
        {
            var config = new Dictionary<string, object?>
            {
                ["provider"] = "openai",
                ["model"] = "gpt-4.1",
                ["operation"] = "llm.chat",
                ["delayMs"] = isFlaky ? 10 : 1
            };

            if (isFlaky)
            {
                config["attemptKey"] =
                    $"{scenario.PipelineName}:chaos-step-{index:000}";

                config["retry"] = new Dictionary<string, object?>
                {
                    ["maxRetries"] = 2,
                    ["strategy"] = "Fixed",
                    ["baseDelayMs"] = 15,
                    ["maxDelayMs"] = 15,
                    ["jitter"] = false
                };
            }

            return config;
        }

        private static async Task<ReplayFingerprint> CreateReplayFingerprintAsync(
            HeavyScenario scenario,
            string executionId,
            AiExecutionRecord record,
            AiExecutionState state,
            IAiExecutionStepResolver resolver)
        {
            var selectedSteps = scenario.PipelineDefinition.Steps
                .Where(step =>
                    !string.IsNullOrWhiteSpace(step.Name) &&
                    scenario.FingerprintStepNames.Contains(
                        step.Name,
                        StringComparer.Ordinal));

            var stepStatuses = new SortedDictionary<string, string>(
                StringComparer.Ordinal);

            foreach (var step in selectedSteps)
            {
                var status = await resolver.GetStepStatusAsync(
                    executionId,
                    step.Name!,
                    state,
                    CancellationToken.None);

                Assert.NotNull(status);

                stepStatuses[step.Name!] = status!.Status.ToString();
            }

            var retryCounts = new SortedDictionary<string, int>(
                StringComparer.Ordinal);

            foreach (var stepName in scenario.ExpectedRetriedSteps)
            {
                var step = await resolver.GetStepAsync(
                    executionId,
                    stepName,
                    state,
                    CancellationToken.None);

                Assert.NotNull(step);

                retryCounts[stepName] =
                    step!.RetryState?.RetryCount ?? 0;
            }

            var required = new SortedDictionary<string, string>(
                StringComparer.Ordinal);

            foreach (var stepName in scenario.RequiredResolvedSteps)
            {
                var step = await resolver.GetStepAsync(
                    executionId,
                    stepName,
                    state,
                    CancellationToken.None);

                Assert.NotNull(step);

                required[stepName] = step!.Status.ToString();
            }

            return new ReplayFingerprint
            {
                Status = record.Status.ToString(),
                IsTerminal = record.IsTerminal,
                CompletedSteps = record.CompletedSteps
                    .OrderBy(step => step, StringComparer.Ordinal)
                    .ToArray(),
                StepStatuses = stepStatuses,
                RetryCounts = retryCounts,
                RequiredResolvedSteps = required
            };
        }

        private sealed class QueueGloballyAdmissionController : IAiRunAdmissionController
        {
            private readonly int _runtimeInstanceCount;

            public QueueGloballyAdmissionController(
                int runtimeInstanceCount)
            {
                _runtimeInstanceCount = runtimeInstanceCount;
            }

            public Task<AiRunAdmissionDecision> AdmitAsync(
                AiRunAdmissionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.QueueGlobally,
                    Reason = "Heavy multi-instance test queues globally.",
                    VisibleInstanceCount = _runtimeInstanceCount,
                    AvailableInstanceCount = 0,
                    CurrentInstanceCount = _runtimeInstanceCount,
                    MaxInstanceCount = _runtimeInstanceCount,
                    Metadata = request.Metadata
                });
            }
        }

        private sealed class NeverCalledSharedRunDispatcher : IAiSharedRunDispatcher
        {
            public Task<AiSharedRunDispatchResult> DispatchAsync(
                AiSharedRunDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException(
                    "The shared runtime controller should not directly dispatch runs when admission queues globally.");
            }
        }

        private sealed class RuntimeInstanceHarness : IAsyncDisposable
        {
            public RuntimeInstanceHarness(
                string runtimeInstanceId,
                AiDagExecutionEngineTestHost host,
                IAiRuntimePipelineBackgroundController backgroundController,
                IAiRuntimeQueueControlPlane queueControlPlane,
                IAiSharedRunDispatcher sharedRunDispatcher,
                IAiDagExecutionStore dagStore,
                IAiExecutionStepResolver resolver,
                IAiExecutionReplayService replayService,
                IAiExecutionSnapshotStore<ExecutionContextSnapshot> snapshotStore,
                IAiDecisionLedger ledger,
                IAiTraceTimeline timeline,
                IAiRuntimeMetrics metrics,
                IAiExecutionAssistanceStore executionAssistanceStore,
                AiExecutionAssistancePump executionAssistancePump,
                IRuntimeAiPipelineDefinitionProvider pipelineDefinitionProvider)
            {
                RuntimeInstanceId = runtimeInstanceId;
                Host = host;
                BackgroundController = backgroundController;
                QueueControlPlane = queueControlPlane;
                SharedRunDispatcher = sharedRunDispatcher;
                DagStore = dagStore;
                Resolver = resolver;
                ReplayService = replayService;
                SnapshotStore = snapshotStore;
                Ledger = ledger;
                Timeline = timeline;
                Metrics = metrics;
                ExecutionAssistanceStore = executionAssistanceStore;
                ExecutionAssistancePump = executionAssistancePump;
                PipelineDefinitionProvider = pipelineDefinitionProvider;
            }

            public string RuntimeInstanceId { get; }

            public AiDagExecutionEngineTestHost Host { get; }

            public IAiRuntimePipelineBackgroundController BackgroundController { get; }

            public IAiRuntimeQueueControlPlane QueueControlPlane { get; }

            public IAiSharedRunDispatcher SharedRunDispatcher { get; }

            public IAiDagExecutionStore DagStore { get; }

            public IAiExecutionStepResolver Resolver { get; }

            public IAiExecutionReplayService ReplayService { get; }

            public IAiExecutionSnapshotStore<ExecutionContextSnapshot> SnapshotStore { get; }

            public IAiDecisionLedger Ledger { get; }

            public IAiTraceTimeline Timeline { get; }

            public IAiRuntimeMetrics Metrics { get; }

            public IAiExecutionAssistanceStore ExecutionAssistanceStore { get; }

            public AiExecutionAssistancePump ExecutionAssistancePump { get; }

            public IRuntimeAiPipelineDefinitionProvider PipelineDefinitionProvider { get; }

            public async ValueTask DisposeAsync()
            {
                await BackgroundController.StopAsync();
                await Host.DisposeAsync();
            }
        }

        private sealed record CompletedExecutionInfo(
            string SharedRunId,
            string RuntimeInstanceId,
            string LocalRunId,
            string ExecutionId,
            RuntimeInstanceHarness RuntimeInstance,
            AiExecutionRecord Record);

        private sealed class HeavyScenario
        {
            public bool EnableRetention { get; init; }
            public required string Name { get; init; }

            public required string ScenarioId { get; init; }

            public required string PipelineName { get; init; }

            public required string CandidateIdPrefix { get; init; }

            public required string RetentionArchiveReason { get; init; }

            public AiPipelineDefinition PipelineDefinition { get; set; } = null!;

            public int RuntimeInstanceCount { get; init; }

            public int ExpectedMinimumParticipatingInstances { get; init; }

            public int RunCount { get; init; }

            public int StepCount { get; init; }

            public int WorkerCount { get; init; }

            public int MaxConcurrentRunsPerInstance { get; init; }

            public int QueueCapacityPerInstance { get; init; }

            public int MaxDispatchesPerPumpCycle { get; init; }

            public int MaxStepsPerCycle { get; init; }

            public int MaxWorkerCycles { get; init; }

            public int MaxDegreeOfParallelism { get; init; }

            public int MaxProviderConcurrency { get; init; }

            public int MaxCompletedStepsInState { get; init; }

            public int MaxInlinePayloadBytes { get; init; }

            public int FlakyStepInterval { get; init; }

            public int ReplaySampleCount { get; init; }

            public TimeSpan WorkerIdleDelay { get; init; }

            public TimeSpan SharedDispatchTimeout { get; init; }

            public TimeSpan ExecutionTimeout { get; init; }

            public TimeSpan SnapshotTimeout { get; init; }

            public IReadOnlyCollection<string> RequiredResolvedSteps { get; init; } =
                Array.Empty<string>();

            public IReadOnlyCollection<string> ExpectedRetriedSteps { get; init; } =
                Array.Empty<string>();

            public IReadOnlyCollection<string> FingerprintStepNames { get; init; } =
                Array.Empty<string>();

            public IReadOnlyCollection<string> RetentionPolicies { get; init; } =
                Array.Empty<string>();

            

            public static HeavyScenario AllFeaturesHeavy()
            {
                return CreateAllFeaturesScenario(
                    name: "shared-queue-real-heavy-all-features",
                    pipelinePrefix: "shared-queue-real-heavy",
                    candidateIdPrefix: "candidate-shared-queue-real-heavy",
                    retentionArchiveReason: "shared-queue-real-heavy-retention",
                    runCount: 3,
                    stepCount: 20,
                    runtimeInstanceCount: 3,
                    expectedMinimumParticipatingInstances: 2,
                    workerCount: 10,
                    maxConcurrentRunsPerInstance: 3,
                    queueCapacityPerInstance: 64,
                    maxDispatchesPerPumpCycle: 2,
                    maxStepsPerCycle: 1,
                    maxWorkerCycles: 5_000,
                    maxDegreeOfParallelism: 12,
                    maxProviderConcurrency: 3,
                    maxCompletedStepsInState: 15,
                    maxInlinePayloadBytes: 1,
                    flakyStepInterval: 9,
                    replaySampleCount: 3,
                    enableRetention:true,
                    workerIdleDelay: TimeSpan.FromMilliseconds(1),
                    sharedDispatchTimeout: TimeSpan.FromSeconds(60),
                    executionTimeout: TimeSpan.FromSeconds(90),
                    snapshotTimeout: TimeSpan.FromSeconds(60));
            }

            public static HeavyScenario AllFeaturesStress(
                int runCount = 9,
                int stepCount = 100,
                int runtimeInstanceCount = 3,
                int expectedMinimumParticipatingInstances = 2,
                int workerCount = 10,
                int maxConcurrentRunsPerInstance = 3,
                int queueCapacityPerInstance = 64,
                int maxDispatchesPerPumpCycle = 2,
                int maxStepsPerCycle = 1,
                int maxWorkerCycles = 10_000,
                int maxDegreeOfParallelism = 12,
                int maxProviderConcurrency = 3,
                int maxCompletedStepsInState = 15,
                int maxInlinePayloadBytes = 1,
                int flakyStepInterval = 9,
                int replaySampleCount = 3,
                bool enableRetention = true,
                TimeSpan? workerIdleDelay = null,
                TimeSpan? sharedDispatchTimeout = null,
                TimeSpan? executionTimeout = null,
                TimeSpan? snapshotTimeout = null)
            {
                return CreateAllFeaturesScenario(
                    name: "shared-queue-real-stress-all-features",
                    pipelinePrefix: "shared-queue-real-stress",
                    candidateIdPrefix: "candidate-shared-queue-real-stress",
                    retentionArchiveReason: "shared-queue-real-stress-retention",
                    runCount: runCount,
                    stepCount: stepCount,
                    runtimeInstanceCount: runtimeInstanceCount,
                    expectedMinimumParticipatingInstances: expectedMinimumParticipatingInstances,
                    workerCount: workerCount,
                    maxConcurrentRunsPerInstance: maxConcurrentRunsPerInstance,
                    queueCapacityPerInstance: queueCapacityPerInstance,
                    maxDispatchesPerPumpCycle: maxDispatchesPerPumpCycle,
                    maxStepsPerCycle: maxStepsPerCycle,
                    maxWorkerCycles: maxWorkerCycles,
                    maxDegreeOfParallelism: maxDegreeOfParallelism,
                    maxProviderConcurrency: maxProviderConcurrency,
                    maxCompletedStepsInState: maxCompletedStepsInState,
                    maxInlinePayloadBytes: maxInlinePayloadBytes,
                    flakyStepInterval: flakyStepInterval,
                    replaySampleCount: replaySampleCount,
                    enableRetention: enableRetention,
                    workerIdleDelay: workerIdleDelay ?? TimeSpan.FromMilliseconds(1),
                    sharedDispatchTimeout: sharedDispatchTimeout ?? TimeSpan.FromSeconds(90),
                    executionTimeout: executionTimeout ?? TimeSpan.FromMinutes(5),
                    snapshotTimeout: snapshotTimeout ?? TimeSpan.FromMinutes(2));
            }

            private static HeavyScenario CreateAllFeaturesScenario(
                string name,
                string pipelinePrefix,
                string candidateIdPrefix,
                string retentionArchiveReason,
                int runCount,
                int stepCount,
                int runtimeInstanceCount,
                int expectedMinimumParticipatingInstances,
                int workerCount,
                int maxConcurrentRunsPerInstance,
                int queueCapacityPerInstance,
                int maxDispatchesPerPumpCycle,
                int maxStepsPerCycle,
                int maxWorkerCycles,
                int maxDegreeOfParallelism,
                int maxProviderConcurrency,
                int maxCompletedStepsInState,
                int maxInlinePayloadBytes,
                int flakyStepInterval,
                int replaySampleCount,
                bool enableRetention,
                TimeSpan workerIdleDelay,
                TimeSpan sharedDispatchTimeout,
                TimeSpan executionTimeout,
                TimeSpan snapshotTimeout)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runCount);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepCount);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runtimeInstanceCount);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedMinimumParticipatingInstances);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentRunsPerInstance);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacityPerInstance);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDispatchesPerPumpCycle);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxStepsPerCycle);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWorkerCycles);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDegreeOfParallelism);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxProviderConcurrency);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCompletedStepsInState);
                ArgumentOutOfRangeException.ThrowIfNegative(maxInlinePayloadBytes);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(flakyStepInterval);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(replaySampleCount);

                if (stepCount < 3)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(stepCount),
                        "The scenario requires at least 3 steps: first step, at least one middle step, and final join step.");
                }

                if (expectedMinimumParticipatingInstances > runtimeInstanceCount)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(expectedMinimumParticipatingInstances),
                        "Expected participating instances cannot be greater than runtime instance count.");
                }

                var scenarioId = Guid.NewGuid().ToString("N");

                var pipelineName =
                    $"{pipelinePrefix}-{scenarioId}";

                var requiredCandidateSteps = new[]
                {
                    "chaos-step-001",
                    "chaos-step-009",
                    "chaos-step-018",
                    "chaos-step-045",
                    "chaos-step-090",
                    "final-join-step"
                };

                var requiredSteps = requiredCandidateSteps
                    .Where(stepName =>
                        string.Equals(
                            stepName,
                            "final-join-step",
                            StringComparison.Ordinal) ||
                        TryGetChaosStepIndex(stepName, out var index) &&
                        index < stepCount)
                    .ToArray();

                var retriedSteps = Enumerable.Range(2, stepCount - 2)
                    .Where(index => index % flakyStepInterval == 0)
                    .Select(index => $"chaos-step-{index:000}")
                    .ToArray();


                string[] retentionPolicies = Array.Empty<string>();

                if (enableRetention)
                {
                    retentionPolicies =
                    [
                        "retention.compact.terminal",
                        "retention.evict.terminal"
                    ];
                }

                var scenario = new HeavyScenario
                {
                    Name = name,
                    ScenarioId = scenarioId,
                    PipelineName = pipelineName,
                    CandidateIdPrefix = candidateIdPrefix,
                    RetentionArchiveReason = retentionArchiveReason,
                    RuntimeInstanceCount = runtimeInstanceCount,
                    ExpectedMinimumParticipatingInstances = expectedMinimumParticipatingInstances,
                    RunCount = runCount,
                    StepCount = stepCount,
                    WorkerCount = workerCount,
                    MaxConcurrentRunsPerInstance = maxConcurrentRunsPerInstance,
                    QueueCapacityPerInstance = queueCapacityPerInstance,
                    MaxDispatchesPerPumpCycle = maxDispatchesPerPumpCycle,
                    MaxStepsPerCycle = maxStepsPerCycle,
                    MaxWorkerCycles = maxWorkerCycles,
                    MaxDegreeOfParallelism = maxDegreeOfParallelism,
                    MaxProviderConcurrency = maxProviderConcurrency,
                    MaxCompletedStepsInState = maxCompletedStepsInState,
                    MaxInlinePayloadBytes = maxInlinePayloadBytes,
                    FlakyStepInterval = flakyStepInterval,
                    ReplaySampleCount = replaySampleCount,
                    WorkerIdleDelay = workerIdleDelay,
                    SharedDispatchTimeout = sharedDispatchTimeout,
                    ExecutionTimeout = executionTimeout,
                    SnapshotTimeout = snapshotTimeout,
                    RetentionPolicies = retentionPolicies,
                    RequiredResolvedSteps = requiredSteps,
                    ExpectedRetriedSteps = retriedSteps,
                    EnableRetention = enableRetention,
                    FingerprintStepNames = requiredSteps
                        .Concat(retriedSteps)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                };

                scenario.PipelineDefinition = CreatePipelineDefinition(
                    scenario);

                return scenario;
            }

            private static bool TryGetChaosStepIndex(
                string stepName,
                out int index)
            {
                index = 0;

                const string prefix = "chaos-step-";

                if (!stepName.StartsWith(
                        prefix,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                return int.TryParse(
                    stepName[prefix.Length..],
                    out index);
            }
        }

        private sealed class ReplayFingerprint
        {
            public required string Status { get; init; }

            public required bool IsTerminal { get; init; }

            public required IReadOnlyList<string> CompletedSteps { get; init; }

            public required IReadOnlyDictionary<string, string> StepStatuses { get; init; }

            public required IReadOnlyDictionary<string, int> RetryCounts { get; init; }

            public required IReadOnlyDictionary<string, string> RequiredResolvedSteps { get; init; }
        }
    }
}
