using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Redis;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.Admission.Reservations;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.SharedController;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue.Redis;
using Multiplexed.AI.Tests.Fixtures;
using StackExchange.Redis;
using System.Collections.Concurrent;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.Tests.Integration.Runtime.ControlPlane.SharedController
{
    /// <summary>
    /// Multi-instance shared queue integration tests.
    /// </summary>
    /// <remarks>
    /// These tests simulate several runtime instances consuming the same Redis-backed
    /// shared queue concurrently.
    ///
    /// This is the layer before real Kubernetes:
    ///
    /// SharedController
    /// -> Redis shared run store
    /// -> Redis shared queue
    /// -> multiple runtime instances
    /// -> concurrent shared queue pumps
    /// -> atomic Redis claim
    /// -> no double dispatch.
    ///
    /// These tests intentionally do not execute real DAG pipelines yet.
    /// The next layer will replace the simulated dispatcher with real local runtime
    /// dispatchers per instance.
    /// </remarks>
    [Collection("redis")]
    public sealed class AiSharedQueueMultiInstanceExecutionIntegrationTests : IAsyncLifetime
    {
        private readonly ITestOutputHelper _output;

        private readonly string _runKeyPrefix =
            $"test:ai:shared-runs:multi-instance:{Guid.NewGuid():N}";

        private readonly string _queueKeyPrefix =
            $"test:ai:shared-queue:multi-instance:{Guid.NewGuid():N}";

        private IConnectionMultiplexer? _connection;

        public AiSharedQueueMultiInstanceExecutionIntegrationTests(
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

        /// <summary>
        /// Validates that several simulated runtime instances can consume the same
        /// Redis shared queue concurrently without double-dispatching any shared run.
        /// </summary>
        [Fact]
        public async Task SharedQueue_Should_Dispatch_Queued_Runs_Across_Multiple_Runtime_Instances_Without_Double_Dispatch()
        {
            await RunMultiInstanceSharedQueueScenarioAsync(
                itemCount: 90,
                runtimeInstanceCount: 3,
                maxDispatchesPerPumpCycle: 1,
                expectedMinimumParticipatingInstances: 2);
        }

        /// <summary>
        /// Stress validation for 500 queued shared runs consumed by several simulated runtime instances.
        /// </summary>
        [Fact]
        public async Task SharedQueue_Should_Dispatch_500_Queued_Runs_Across_Multiple_Runtime_Instances_Without_Double_Dispatch()
        {
            await RunMultiInstanceSharedQueueScenarioAsync(
                itemCount: 500,
                runtimeInstanceCount: 3,
                maxDispatchesPerPumpCycle: 5,
                expectedMinimumParticipatingInstances: 2);
        }

        /// <summary>
        /// Repeated stress validation.
        /// </summary>
        [Fact]
        public async Task SharedQueue_Should_Dispatch_500_Queued_Runs_Repeatedly()
        {
            const int iterations = 10;

            for (var iteration = 1; iteration <= iterations; iteration++)
            {
                _output.WriteLine(
                    $"Starting multi-instance shared queue stress iteration {iteration}/{iterations}.");

                await RunMultiInstanceSharedQueueScenarioAsync(
                    itemCount: 500,
                    runtimeInstanceCount: 3,
                    maxDispatchesPerPumpCycle: 5,
                    expectedMinimumParticipatingInstances: 2,
                    scenarioSuffix: $"iteration-{iteration}");

                _output.WriteLine(
                    $"Completed multi-instance shared queue stress iteration {iteration}/{iterations}.");
            }
        }

        private async Task RunMultiInstanceSharedQueueScenarioAsync(
            int itemCount,
            int runtimeInstanceCount,
            int maxDispatchesPerPumpCycle,
            int expectedMinimumParticipatingInstances,
            string? scenarioSuffix = null)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runtimeInstanceCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDispatchesPerPumpCycle);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedMinimumParticipatingInstances);

            var store = CreateRunStore();
            var queue = CreateQueue();

            var controller = new AiSharedRuntimeController(
                new QueueGloballyAdmissionController(),
                store,
                queue,
                new NeverCalledSharedRunDispatcher(),
                new NoopAiRuntimeScaleOutRequestPublisher(),
                Options.Create(new AiSharedRuntimeControllerOptions()),
                new NoopAiControlPlaneObserver());

            var scenarioId = string.IsNullOrWhiteSpace(scenarioSuffix)
                ? Guid.NewGuid().ToString("N")
                : scenarioSuffix;

            var sharedRunIdPrefix = $"shared-run-{scenarioId}-";

            for (var index = 0; index < itemCount; index++)
            {
                var sharedRunId = $"{sharedRunIdPrefix}{index:D5}";

                var submit = await controller.SubmitRunAsync(
                    new AiSharedRuntimeControllerRequest
                    {
                        Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                        RequestedSharedRunId = sharedRunId,
                        RunRequest = new AiRuntimePipelineRunRequest
                        {
                            PipelineName = "multi-instance-shared-queue-test"
                        },
                        TenantId = "tenant-multi-instance",
                        PipelineKey = "multi-instance-shared-queue-test",
                        CorrelationId = $"correlation-{sharedRunId}",
                        RequestedBy = "integration-test",
                        Source = "multi-instance-shared-queue-test",
                        Reason = "Queue globally for multi-instance shared queue dispatch.",
                        Metadata = new Dictionary<string, string>
                        {
                            ["scenario"] = "multi-instance-shared-queue",
                            ["scenario.id"] = scenarioId,
                            ["item.index"] = index.ToString(),
                            ["item.count"] = itemCount.ToString()
                        }
                    });

                Assert.True(
                    submit.Success,
                    submit.FailureReason ?? $"Submit failed for shared run '{sharedRunId}'.");

                Assert.NotNull(submit.Run);
                Assert.Equal(AiSharedRunStatus.QueuedGlobally, submit.Run.Status);
            }

            var queuedRuns = (await store.ListAsync(
                    includeCancelled: true,
                    includeCompleted: true,
                    includeFailed: true))
                .Where(run => run.SharedRunId.StartsWith(
                    sharedRunIdPrefix,
                    StringComparison.Ordinal))
                .ToArray();

            Assert.Equal(itemCount, queuedRuns.Length);

            Assert.All(
                queuedRuns,
                run => Assert.Equal(AiSharedRunStatus.QueuedGlobally, run.Status));

            var queuedItems = (await queue.ListAsync(
                    includeTerminal: true))
                .Where(item => item.SharedRunId.StartsWith(
                    sharedRunIdPrefix,
                    StringComparison.Ordinal))
                .ToArray();

            Assert.Equal(itemCount, queuedItems.Length);

            Assert.All(
                queuedItems,
                item => Assert.Equal(AiSharedQueueItemStatus.Pending, item.Status));

            var recorder = new MultiInstanceDispatchRecorder(
                dispatchDelay: TimeSpan.FromMilliseconds(1));

            var pumpTasks = Enumerable
                .Range(1, runtimeInstanceCount)
                .Select(instanceNumber =>
                    RunRuntimeInstancePumpUntilEmptyAsync(
                        runtimeInstanceId: $"runtime-instance-{instanceNumber}",
                        workerId: $"runtime-instance-{instanceNumber}-shared-queue-worker",
                        queue,
                        store,
                        recorder,
                        maxDispatchesPerPumpCycle))
                .ToArray();

            var pumpResults = await Task.WhenAll(
                pumpTasks);

            var totalSuccessfulDispatches = pumpResults.Sum(
                result => result.SuccessfulDispatchCount);

            var totalFailedDispatches = pumpResults.Sum(
                result => result.FailedDispatchCount);

            Assert.Equal(0, totalFailedDispatches);
            Assert.Equal(itemCount, totalSuccessfulDispatches);

            var scenarioDispatches = recorder.Dispatches
                .Where(dispatch => dispatch.SharedRunId.StartsWith(
                    sharedRunIdPrefix,
                    StringComparison.Ordinal))
                .ToArray();

            Assert.Equal(itemCount, scenarioDispatches.Length);

            var duplicateDispatches = scenarioDispatches
                .GroupBy(dispatch => dispatch.SharedRunId, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .ToArray();

            Assert.Empty(duplicateDispatches);

            var finalRuns = (await store.ListAsync(
                    includeCancelled: true,
                    includeCompleted: true,
                    includeFailed: true))
                .Where(run => run.SharedRunId.StartsWith(
                    sharedRunIdPrefix,
                    StringComparison.Ordinal))
                .ToArray();

            Assert.Equal(itemCount, finalRuns.Length);

            Assert.All(
                finalRuns,
                run =>
                {
                    Assert.Equal(AiSharedRunStatus.Dispatched, run.Status);
                    Assert.False(string.IsNullOrWhiteSpace(run.AssignedRuntimeInstanceId));
                    Assert.False(string.IsNullOrWhiteSpace(run.LocalRunId));
                    Assert.False(string.IsNullOrWhiteSpace(run.ExecutionId));
                });

            var finalQueueItems = (await queue.ListAsync(
                    includeTerminal: true))
                .Where(item => item.SharedRunId.StartsWith(
                    sharedRunIdPrefix,
                    StringComparison.Ordinal))
                .ToArray();

            Assert.Equal(itemCount, finalQueueItems.Length);

            Assert.All(
                finalQueueItems,
                item => Assert.Equal(AiSharedQueueItemStatus.Dispatched, item.Status));

            var dispatchDistribution = scenarioDispatches
                .GroupBy(dispatch => dispatch.RuntimeInstanceId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Count(),
                    StringComparer.Ordinal);

            Assert.True(
                dispatchDistribution.Count >= expectedMinimumParticipatingInstances,
                $"Expected at least '{expectedMinimumParticipatingInstances}' runtime instances to participate, " +
                $"but only '{dispatchDistribution.Count}' participated. Distribution: {FormatDistribution(dispatchDistribution)}");

            _output.WriteLine("");
            _output.WriteLine("============================================================");
            _output.WriteLine("MULTI-INSTANCE SHARED QUEUE DISPATCH TEST");
            _output.WriteLine("============================================================");
            _output.WriteLine($"ScenarioId:                 {scenarioId}");
            _output.WriteLine($"QueuedRuns:                 {itemCount}");
            _output.WriteLine($"RuntimeInstances:           {runtimeInstanceCount}");
            _output.WriteLine($"MaxDispatchesPerPumpCycle:  {maxDispatchesPerPumpCycle}");
            _output.WriteLine($"SuccessfulDispatches:       {totalSuccessfulDispatches}");
            _output.WriteLine($"FailedDispatches:           {totalFailedDispatches}");
            _output.WriteLine($"ParticipatingInstances:     {dispatchDistribution.Count}");
            _output.WriteLine("");
            _output.WriteLine("DISPATCH DISTRIBUTION");
            _output.WriteLine("------------------------------------------------------------");

            foreach (var item in dispatchDistribution
                .OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                _output.WriteLine(
                    $"{item.Key,-35} | Dispatches={item.Value}");
            }
        }

        private async Task<AiSharedQueuePumpResult> RunRuntimeInstancePumpUntilEmptyAsync(
    string runtimeInstanceId,
    string workerId,
    IAiSharedQueue queue,
    IAiSharedRunStore store,
    MultiInstanceDispatchRecorder recorder,
    int maxDispatchesPerPumpCycle)
        {
            var dispatcher = new AiSharedQueueDispatcher(
                queue,
                store,
                recorder,
                new FakeRunAdmissionController(
                    assignedRuntimeInstanceId: runtimeInstanceId),
                new InMemoryAiRuntimeAdmissionReservationStore(), 
                NullLogger<AiSharedQueueDispatcher>.Instance);

            var pump = new AiSharedQueuePump(
                dispatcher,
                Options.Create(new AiSharedQueuePumpOptions
                {
                    Enabled = true,
                    MaxDispatchesPerCycle = maxDispatchesPerPumpCycle,
                    DefaultClaimTtl = TimeSpan.FromSeconds(30),
                    StopCycleWhenNoItemAvailable = true,
                    StopCycleOnDispatchFailure = true
                }),
                NullLogger<AiSharedQueuePump>.Instance);

            var total = new AiSharedQueuePumpResult
            {
                Success = true,
                RuntimeInstanceId = runtimeInstanceId,
                StartedAtUtc = DateTimeOffset.UtcNow
            };

            while (true)
            {
                var result = await pump.PumpOnceAsync(
                        new AiSharedQueuePumpRequest
                        {
                            PumpRuntimeInstanceId = runtimeInstanceId,
                            PumpWorkerId = workerId,
                            MaxDispatches = maxDispatchesPerPumpCycle,
                            ClaimTtl = TimeSpan.FromSeconds(30),
                            RequestedBy = "integration-test",
                            Source = "multi-instance-shared-queue-test",
                            Reason = "Drain shared queue from simulated runtime instance."
                        })
                    .ConfigureAwait(false);

                total = new AiSharedQueuePumpResult
                {
                    Success = total.Success && result.Success,
                    RuntimeInstanceId = runtimeInstanceId,
                    AttemptedDispatchCount = total.AttemptedDispatchCount + result.AttemptedDispatchCount,
                    SuccessfulDispatchCount = total.SuccessfulDispatchCount + result.SuccessfulDispatchCount,
                    FailedDispatchCount = total.FailedDispatchCount + result.FailedDispatchCount,
                    StoppedBecauseNoItemAvailable = result.StoppedBecauseNoItemAvailable,
                    StartedAtUtc = total.StartedAtUtc,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    DurationMs = result.DurationMs,
                    FailureReason = result.FailureReason
                };

                if (result.StoppedBecauseNoItemAvailable ||
                    result.SuccessfulDispatchCount == 0 ||
                    result.FailedDispatchCount > 0)
                {
                    return total;
                }
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
                }));
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
                }));
        }

        private static string FormatDistribution(
            IReadOnlyDictionary<string, int> distribution)
        {
            return string.Join(
                ", ",
                distribution
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => $"{item.Key}={item.Value}"));
        }

        private sealed class QueueGloballyAdmissionController : IAiRunAdmissionController
        {
            public Task<AiRunAdmissionDecision> AdmitAsync(
                AiRunAdmissionRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);

                return Task.FromResult(new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.QueueGlobally,
                    Reason = "Multi-instance test queues globally.",
                    VisibleInstanceCount = 3,
                    AvailableInstanceCount = 0,
                    CurrentInstanceCount = 3,
                    MaxInstanceCount = 3,
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

        private sealed class MultiInstanceDispatchRecorder : IAiSharedRunDispatcher
        {
            private readonly TimeSpan _dispatchDelay;
            private int _counter;

            public MultiInstanceDispatchRecorder(
                TimeSpan dispatchDelay)
            {
                _dispatchDelay = dispatchDelay;
            }

            public ConcurrentBag<DispatchRecord> Dispatches { get; } = new();

            public int TotalDispatchCount => _counter;

            public async Task<AiSharedRunDispatchResult> DispatchAsync(
                AiSharedRunDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);

                if (_dispatchDelay > TimeSpan.Zero)
                {
                    await Task.Delay(
                        _dispatchDelay,
                        cancellationToken);
                }

                var sequence = Interlocked.Increment(ref _counter);
                var now = DateTimeOffset.UtcNow;

                Dispatches.Add(
                    new DispatchRecord(
                        request.SharedRun.SharedRunId,
                        request.RuntimeInstanceId,
                        request.ClaimToken,
                        sequence));

                return new AiSharedRunDispatchResult
                {
                    Success = true,
                    SharedRunId = request.SharedRun.SharedRunId,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    LocalRunId = $"local-run-{sequence:D6}",
                    ExecutionId = $"execution-{sequence:D6}",
                    ClaimToken = request.ClaimToken,
                    Message = "Dispatched by simulated runtime instance.",
                    StartedAtUtc = now,
                    CompletedAtUtc = now,
                    DurationMs = 0
                };
            }
        }

        private sealed record DispatchRecord(
            string SharedRunId,
            string RuntimeInstanceId,
            string? ClaimToken,
            int Sequence);
    }
}