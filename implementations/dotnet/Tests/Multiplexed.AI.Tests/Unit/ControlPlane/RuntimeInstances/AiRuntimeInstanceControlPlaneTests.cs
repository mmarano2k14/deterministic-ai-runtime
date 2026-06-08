using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Control;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances
{
    public sealed class AiRuntimeInstanceControlPlaneTests
    {
        [Fact]
        public async Task RegisterAsync_Should_Call_Registry_And_Return_Instance()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var controlPlane = CreateControlPlane(registry);

            var result = await controlPlane.RegisterAsync(new AiRuntimeInstanceControlPlaneRequest
            {
                Operation = AiRuntimeInstanceControlPlaneOperation.Register,
                Registration = new AiRuntimeInstanceRegistration
                {
                    RuntimeInstanceId = "runtime-1",
                    HostName = "host-1",
                    WorkerCount = 4,
                    QueueCapacity = 8,
                    MaxConcurrentRuns = 2
                }
            });

            Assert.True(result.Success);
            Assert.NotNull(result.Instance);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);
            Assert.Equal("runtime-1", registry.LastRuntimeInstanceId);
            Assert.True(registry.RegisterCalled);
        }

        [Fact]
        public async Task HeartbeatAsync_Should_Call_Registry_And_Return_Instance()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var controlPlane = CreateControlPlane(registry);

            var result = await controlPlane.HeartbeatAsync(new AiRuntimeInstanceControlPlaneRequest
            {
                Operation = AiRuntimeInstanceControlPlaneOperation.Heartbeat,
                RuntimeInstanceId = "runtime-1",
                QueuedRunCount = 2,
                RunningRunCount = 1,
                ActiveRunCount = 3,
                AvailableRunSlots = 0,
                IsQueuePaused = false,
                CanAcceptRun = false,
                Status = AiRuntimeInstanceStatus.Busy
            });

            Assert.True(result.Success);
            Assert.NotNull(result.Instance);
            Assert.Equal(AiRuntimeInstanceStatus.Busy, result.Instance.Status);
            Assert.True(registry.HeartbeatCalled);
            Assert.Equal("runtime-1", registry.LastRuntimeInstanceId);
            Assert.Equal(2, registry.LastQueuedRunCount);
            Assert.Equal(1, registry.LastRunningRunCount);
            Assert.Equal(3, registry.LastActiveRunCount);
            Assert.Equal(0, registry.LastAvailableRunSlots);
            Assert.False(registry.LastIsQueuePaused);
            Assert.False(registry.LastCanAcceptRun);
            Assert.Equal(AiRuntimeInstanceStatus.Busy, registry.LastStatus);
        }

        [Fact]
        public async Task GetInstanceAsync_Should_Call_Registry()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var controlPlane = CreateControlPlane(registry);

            var result = await controlPlane.GetInstanceAsync(new AiRuntimeInstanceControlPlaneRequest
            {
                Operation = AiRuntimeInstanceControlPlaneOperation.GetInstance,
                RuntimeInstanceId = "runtime-1"
            });

            Assert.True(result.Success);
            Assert.NotNull(result.Instance);
            Assert.True(registry.GetCalled);
            Assert.Equal("runtime-1", registry.LastRuntimeInstanceId);
        }

        [Fact]
        public async Task ListInstancesAsync_Should_Call_Registry()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var controlPlane = CreateControlPlane(registry);

            var result = await controlPlane.ListInstancesAsync(new AiRuntimeInstanceControlPlaneRequest
            {
                Operation = AiRuntimeInstanceControlPlaneOperation.ListInstances,
                IncludeStopped = true
            });

            Assert.True(result.Success);
            Assert.NotEmpty(result.Instances);
            Assert.True(registry.ListCalled);
            Assert.True(registry.LastIncludeStopped);
        }

        [Fact]
        public async Task MarkDrainingAsync_Should_Call_Registry()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var controlPlane = CreateControlPlane(registry);

            var result = await controlPlane.MarkDrainingAsync(new AiRuntimeInstanceControlPlaneRequest
            {
                Operation = AiRuntimeInstanceControlPlaneOperation.MarkDraining,
                RuntimeInstanceId = "runtime-1"
            });

            Assert.True(result.Success);
            Assert.NotNull(result.Instance);
            Assert.Equal(AiRuntimeInstanceStatus.Draining, result.Instance.Status);
            Assert.True(registry.MarkDrainingCalled);
            Assert.Equal("runtime-1", registry.LastRuntimeInstanceId);
        }

        [Fact]
        public async Task UnregisterAsync_Should_Call_Registry()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var controlPlane = CreateControlPlane(registry);

            var result = await controlPlane.UnregisterAsync(new AiRuntimeInstanceControlPlaneRequest
            {
                Operation = AiRuntimeInstanceControlPlaneOperation.Unregister,
                RuntimeInstanceId = "runtime-1"
            });

            Assert.True(result.Success);
            Assert.NotNull(result.Instance);
            Assert.Equal(AiRuntimeInstanceStatus.Stopped, result.Instance.Status);
            Assert.True(registry.UnregisterCalled);
            Assert.Equal("runtime-1", registry.LastRuntimeInstanceId);
        }

        [Fact]
        public async Task ExecuteAsync_Should_Dispatch_By_Operation()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var controlPlane = CreateControlPlane(registry);

            var result = await controlPlane.ExecuteAsync(new AiRuntimeInstanceControlPlaneRequest
            {
                Operation = AiRuntimeInstanceControlPlaneOperation.MarkDraining,
                RuntimeInstanceId = "runtime-1"
            });

            Assert.True(result.Success);
            Assert.Equal(AiRuntimeInstanceControlPlaneOperation.MarkDraining, result.Operation);
            Assert.True(registry.MarkDrainingCalled);
        }

        [Fact]
        public async Task RegisterAsync_Should_Return_Failure_When_Registration_Is_Missing()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var controlPlane = CreateControlPlane(registry);

            var result = await controlPlane.RegisterAsync(new AiRuntimeInstanceControlPlaneRequest
            {
                Operation = AiRuntimeInstanceControlPlaneOperation.Register
            });

            Assert.False(result.Success);
            Assert.Contains("Registration is required", result.FailureReason);
        }

        [Fact]
        public async Task GetInstanceAsync_Should_Return_Failure_When_RuntimeInstanceId_Is_Missing()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var controlPlane = CreateControlPlane(registry);

            var result = await controlPlane.GetInstanceAsync(new AiRuntimeInstanceControlPlaneRequest
            {
                Operation = AiRuntimeInstanceControlPlaneOperation.GetInstance
            });

            Assert.False(result.Success);
            Assert.Contains("RuntimeInstanceId is required", result.FailureReason);
        }

        [Fact]
        public async Task RegisterAsync_Should_Return_Failure_When_Disabled()
        {
            var registry = new FakeRuntimeInstanceRegistry();

            var controlPlane = CreateControlPlane(
                registry,
                new AiRuntimeInstanceControlPlaneOptions
                {
                    EnableRegister = false
                });

            var result = await controlPlane.RegisterAsync(new AiRuntimeInstanceControlPlaneRequest
            {
                Operation = AiRuntimeInstanceControlPlaneOperation.Register,
                Registration = new AiRuntimeInstanceRegistration
                {
                    RuntimeInstanceId = "runtime-1",
                    WorkerCount = 1
                }
            });

            Assert.False(result.Success);
            Assert.Contains("disabled", result.FailureReason);
        }

        [Fact]
        public async Task RegisterAsync_Should_Record_Started_And_Completed_Events()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var observer = new CapturingControlPlaneObserver();

            var controlPlane = new AiRuntimeInstanceControlPlane(
                registry,
                Options.Create(new AiRuntimeInstanceControlPlaneOptions()),
                observer);

            var result = await controlPlane.RegisterAsync(new AiRuntimeInstanceControlPlaneRequest
            {
                Operation = AiRuntimeInstanceControlPlaneOperation.Register,
                CorrelationId = "correlation-1",
                Source = "unit-test",
                RequestedBy = "tester",
                Registration = new AiRuntimeInstanceRegistration
                {
                    RuntimeInstanceId = "runtime-1",
                    WorkerCount = 4
                }
            });

            Assert.True(result.Success);
            Assert.Equal(2, observer.Events.Count);

            Assert.Equal(AiControlPlaneEventType.OperationStarted, observer.Events[0].EventType);
            Assert.Equal(AiControlPlaneEventType.OperationCompleted, observer.Events[1].EventType);

            Assert.All(observer.Events, controlPlaneEvent =>
            {
                Assert.Equal(AiControlPlaneArea.InstanceRegistry, controlPlaneEvent.Area);
                Assert.Equal("Register", controlPlaneEvent.Operation);
                Assert.Equal("correlation-1", controlPlaneEvent.Correlation.CorrelationId);
                Assert.Equal("runtime-1", controlPlaneEvent.Correlation.RuntimeInstanceId);
            });
        }

        private static AiRuntimeInstanceControlPlane CreateControlPlane(
            IAiRuntimeInstanceRegistry registry,
            AiRuntimeInstanceControlPlaneOptions? options = null)
        {
            return new AiRuntimeInstanceControlPlane(
                registry,
                Options.Create(options ?? new AiRuntimeInstanceControlPlaneOptions()),
                new NoopAiControlPlaneObserver());
        }

        private sealed class CapturingControlPlaneObserver : IAiControlPlaneObserver
        {
            public List<AiControlPlaneEvent> Events { get; } = new();

            public Task RecordAsync(
                AiControlPlaneEvent controlPlaneEvent,
                CancellationToken cancellationToken = default)
            {
                Events.Add(controlPlaneEvent);

                return Task.CompletedTask;
            }
        }

        private sealed class FakeRuntimeInstanceRegistry : IAiRuntimeInstanceRegistry
        {
            public bool RegisterCalled { get; private set; }

            public bool HeartbeatCalled { get; private set; }

            public bool GetCalled { get; private set; }

            public bool ListCalled { get; private set; }

            public bool MarkDrainingCalled { get; private set; }

            public bool UnregisterCalled { get; private set; }

            public string? LastRuntimeInstanceId { get; private set; }

            public int LastQueuedRunCount { get; private set; }

            public int LastRunningRunCount { get; private set; }

            public int LastActiveRunCount { get; private set; }

            public int? LastAvailableRunSlots { get; private set; }

            public int? LastActiveWorkerCount { get; private set; }

            public int? LastAvailableWorkerCount { get; private set; }

            public int? LastMaxLocalWorkersPerExecution { get; private set; }

            public bool LastIsQueuePaused { get; private set; }

            public bool LastCanAcceptRun { get; private set; }

            public AiRuntimeInstanceStatus LastStatus { get; private set; }

            public bool LastIncludeStopped { get; private set; }

            public Task<AiRuntimeInstanceSnapshot> RegisterAsync(
                AiRuntimeInstanceRegistration registration,
                CancellationToken cancellationToken = default)
            {
                RegisterCalled = true;
                LastRuntimeInstanceId = registration.RuntimeInstanceId;

                return Task.FromResult(CreateSnapshot(
                    registration.RuntimeInstanceId,
                    AiRuntimeInstanceStatus.Ready,
                    registration.WorkerCount,
                    activeWorkerCount: 0,
                    availableWorkerCount: registration.WorkerCount,
                    maxLocalWorkersPerExecution: null,
                    queuedRunCount: 0,
                    runningRunCount: 0,
                    activeRunCount: 0,
                    queueCapacity: registration.QueueCapacity,
                    maxConcurrentRuns: registration.MaxConcurrentRuns,
                    availableRunSlots: registration.MaxConcurrentRuns,
                    isQueuePaused: false,
                    canAcceptRun: true));
            }

            public Task<AiRuntimeInstanceSnapshot?> HeartbeatAsync(
                string runtimeInstanceId,
                int queuedRunCount,
                int runningRunCount,
                int activeRunCount,
                int? availableRunSlots,
                int? activeWorkerCount,
                int? availableWorkerCount,
                int? maxLocalWorkersPerExecution,
                bool isQueuePaused,
                bool canAcceptRun,
                AiRuntimeInstanceStatus status,
                CancellationToken cancellationToken = default)
            {
                HeartbeatCalled = true;
                LastRuntimeInstanceId = runtimeInstanceId;
                LastQueuedRunCount = queuedRunCount;
                LastRunningRunCount = runningRunCount;
                LastActiveRunCount = activeRunCount;
                LastAvailableRunSlots = availableRunSlots;
                LastActiveWorkerCount = activeWorkerCount;
                LastAvailableWorkerCount = availableWorkerCount;
                LastMaxLocalWorkersPerExecution = maxLocalWorkersPerExecution;
                LastIsQueuePaused = isQueuePaused;
                LastCanAcceptRun = canAcceptRun;
                LastStatus = status;

                return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                    CreateSnapshot(
                        runtimeInstanceId,
                        status,
                        workerCount: 4,
                        activeWorkerCount: activeWorkerCount,
                        availableWorkerCount: availableWorkerCount,
                        maxLocalWorkersPerExecution: maxLocalWorkersPerExecution,
                        queuedRunCount,
                        runningRunCount,
                        activeRunCount,
                        queueCapacity: 8,
                        maxConcurrentRuns: 2,
                        availableRunSlots,
                        isQueuePaused,
                        canAcceptRun));
            }

            public Task<AiRuntimeInstanceSnapshot?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                GetCalled = true;
                LastRuntimeInstanceId = runtimeInstanceId;

                return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                    CreateSnapshot(
                        runtimeInstanceId,
                        AiRuntimeInstanceStatus.Ready,
                        workerCount: 4,
                        activeWorkerCount: 0,
                        availableWorkerCount: 4,
                        maxLocalWorkersPerExecution: 2,
                        queuedRunCount: 0,
                        runningRunCount: 0,
                        activeRunCount: 0,
                        queueCapacity: 8,
                        maxConcurrentRuns: 2,
                        availableRunSlots: 2,
                        isQueuePaused: false,
                        canAcceptRun: true));
            }

            public Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListAsync(
                bool includeStopped = false,
                CancellationToken cancellationToken = default)
            {
                ListCalled = true;
                LastIncludeStopped = includeStopped;

                IReadOnlyList<AiRuntimeInstanceSnapshot> snapshots =
                    new[]
                    {
                CreateSnapshot(
                    "runtime-1",
                    AiRuntimeInstanceStatus.Ready,
                    workerCount: 4,
                    activeWorkerCount: 0,
                    availableWorkerCount: 4,
                    maxLocalWorkersPerExecution: 2,
                    queuedRunCount: 0,
                    runningRunCount: 0,
                    activeRunCount: 0,
                    queueCapacity: 8,
                    maxConcurrentRuns: 2,
                    availableRunSlots: 2,
                    isQueuePaused: false,
                    canAcceptRun: true)
                    };

                return Task.FromResult(snapshots);
            }

            public Task<AiRuntimeInstanceSnapshot?> MarkDrainingAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                MarkDrainingCalled = true;
                LastRuntimeInstanceId = runtimeInstanceId;

                return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                    CreateSnapshot(
                        runtimeInstanceId,
                        AiRuntimeInstanceStatus.Draining,
                        workerCount: 4,
                        activeWorkerCount: 0,
                        availableWorkerCount: 0,
                        maxLocalWorkersPerExecution: 2,
                        queuedRunCount: 0,
                        runningRunCount: 0,
                        activeRunCount: 0,
                        queueCapacity: 8,
                        maxConcurrentRuns: 2,
                        availableRunSlots: 0,
                        isQueuePaused: false,
                        canAcceptRun: false));
            }

            public Task<AiRuntimeInstanceSnapshot?> UnregisterAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                UnregisterCalled = true;
                LastRuntimeInstanceId = runtimeInstanceId;

                return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                    CreateSnapshot(
                        runtimeInstanceId,
                        AiRuntimeInstanceStatus.Stopped,
                        workerCount: 4,
                        activeWorkerCount: 0,
                        availableWorkerCount: 0,
                        maxLocalWorkersPerExecution: 2,
                        queuedRunCount: 0,
                        runningRunCount: 0,
                        activeRunCount: 0,
                        queueCapacity: 8,
                        maxConcurrentRuns: 2,
                        availableRunSlots: 0,
                        isQueuePaused: false,
                        canAcceptRun: false));
            }

            private static AiRuntimeInstanceSnapshot CreateSnapshot(
                string runtimeInstanceId,
                AiRuntimeInstanceStatus status,
                int workerCount,
                int? activeWorkerCount,
                int? availableWorkerCount,
                int? maxLocalWorkersPerExecution,
                int queuedRunCount,
                int runningRunCount,
                int activeRunCount,
                int? queueCapacity,
                int? maxConcurrentRuns,
                int? availableRunSlots,
                bool isQueuePaused,
                bool canAcceptRun)
            {
                var now = DateTimeOffset.UtcNow;

                return new AiRuntimeInstanceSnapshot
                {
                    RuntimeInstanceId = runtimeInstanceId,
                    Status = status,
                    WorkerCount = workerCount,
                    ActiveWorkerCount = activeWorkerCount,
                    AvailableWorkerCount = availableWorkerCount,
                    MaxLocalWorkersPerExecution = maxLocalWorkersPerExecution,
                    QueuedRunCount = queuedRunCount,
                    RunningRunCount = runningRunCount,
                    ActiveRunCount = activeRunCount,
                    QueueCapacity = queueCapacity,
                    MaxConcurrentRuns = maxConcurrentRuns,
                    AvailableRunSlots = availableRunSlots,
                    IsQueuePaused = isQueuePaused,
                    CanAcceptRun = canAcceptRun,
                    RegisteredAtUtc = now,
                    LastHeartbeatAtUtc = now,
                    SnapshotAtUtc = now
                };
            }
        }
    }
}