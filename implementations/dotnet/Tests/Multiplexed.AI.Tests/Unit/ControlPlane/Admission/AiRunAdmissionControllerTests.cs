using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.Admission;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Admission
{
    public sealed class AiRunAdmissionControllerTests
    {
        [Fact]
        public async Task AdmitAsync_Should_Assign_To_Available_Instance()
        {
            var registry = new FakeRuntimeInstanceRegistry(
                CreateInstance(
                    "runtime-1",
                    AiRuntimeInstanceStatus.Ready,
                    canAcceptRun: true,
                    queuedRunCount: 1,
                    runningRunCount: 0));

            var controller = CreateController(registry);

            var decision = await controller.AdmitAsync(CreateRequest());

            Assert.Equal(AiRunAdmissionDecisionType.AssignToInstance, decision.DecisionType);
            Assert.True(decision.Accepted);
            Assert.Equal("runtime-1", decision.AssignedRuntimeInstanceId);
            Assert.NotNull(decision.AssignedInstance);
            Assert.Equal(1, decision.VisibleInstanceCount);
            Assert.Equal(1, decision.AvailableInstanceCount);
        }

        [Fact]
        public async Task AdmitAsync_Should_Select_Least_Loaded_Instance()
        {
            var registry = new FakeRuntimeInstanceRegistry(
                CreateInstance(
                    "runtime-b",
                    AiRuntimeInstanceStatus.Ready,
                    canAcceptRun: true,
                    queuedRunCount: 4,
                    runningRunCount: 1),
                CreateInstance(
                    "runtime-a",
                    AiRuntimeInstanceStatus.Ready,
                    canAcceptRun: true,
                    queuedRunCount: 1,
                    runningRunCount: 0));

            var controller = CreateController(registry);

            var decision = await controller.AdmitAsync(CreateRequest());

            Assert.Equal(AiRunAdmissionDecisionType.AssignToInstance, decision.DecisionType);
            Assert.Equal("runtime-a", decision.AssignedRuntimeInstanceId);
        }

        [Fact]
        public async Task AdmitAsync_Should_Select_Preferred_Instance_When_Available()
        {
            var registry = new FakeRuntimeInstanceRegistry(
                CreateInstance(
                    "runtime-a",
                    AiRuntimeInstanceStatus.Ready,
                    canAcceptRun: true,
                    queuedRunCount: 0,
                    runningRunCount: 0),
                CreateInstance(
                    "runtime-b",
                    AiRuntimeInstanceStatus.Ready,
                    canAcceptRun: true,
                    queuedRunCount: 3,
                    runningRunCount: 2));

            var controller = CreateController(registry);

            var decision = await controller.AdmitAsync(CreateRequest(
                preferredRuntimeInstanceId: "runtime-b"));

            Assert.Equal(AiRunAdmissionDecisionType.AssignToInstance, decision.DecisionType);
            Assert.Equal("runtime-b", decision.AssignedRuntimeInstanceId);
            Assert.Contains("Preferred", decision.Reason);
        }

        [Fact]
        public async Task AdmitAsync_Should_Ignore_Preferred_Instance_When_Not_Available()
        {
            var registry = new FakeRuntimeInstanceRegistry(
                CreateInstance(
                    "runtime-a",
                    AiRuntimeInstanceStatus.Ready,
                    canAcceptRun: true,
                    queuedRunCount: 0,
                    runningRunCount: 0),
                CreateInstance(
                    "runtime-b",
                    AiRuntimeInstanceStatus.Ready,
                    canAcceptRun: false,
                    queuedRunCount: 8,
                    runningRunCount: 2));

            var controller = CreateController(registry);

            var decision = await controller.AdmitAsync(CreateRequest(
                preferredRuntimeInstanceId: "runtime-b"));

            Assert.Equal(AiRunAdmissionDecisionType.AssignToInstance, decision.DecisionType);
            Assert.Equal("runtime-a", decision.AssignedRuntimeInstanceId);
        }

        [Fact]
        public async Task AdmitAsync_Should_Request_ScaleOut_When_No_Instance_Available_And_Max_Not_Reached()
        {
            var registry = new FakeRuntimeInstanceRegistry(
                CreateInstance(
                    "runtime-1",
                    AiRuntimeInstanceStatus.Busy,
                    canAcceptRun: false,
                    queuedRunCount: 8,
                    runningRunCount: 2));

            var controller = CreateController(
                registry,
                new AiRunAdmissionOptions
                {
                    MaxInstanceCount = 3,
                    EnableScaleOutRequest = true
                });

            var decision = await controller.AdmitAsync(CreateRequest());

            Assert.Equal(AiRunAdmissionDecisionType.RequestScaleOut, decision.DecisionType);
            Assert.True(decision.ShouldRequestScaleOut);
            Assert.True(decision.Accepted);
            Assert.Equal(1, decision.CurrentInstanceCount);
            Assert.Equal(3, decision.MaxInstanceCount);
        }

        [Fact]
        public async Task AdmitAsync_Should_QueueGlobally_When_No_Capacity_And_ScaleOut_Not_Allowed()
        {
            var registry = new FakeRuntimeInstanceRegistry(
                CreateInstance(
                    "runtime-1",
                    AiRuntimeInstanceStatus.Busy,
                    canAcceptRun: false,
                    queuedRunCount: 8,
                    runningRunCount: 2));

            var controller = CreateController(
                registry,
                new AiRunAdmissionOptions
                {
                    EnableScaleOutRequest = false,
                    EnableGlobalQueueFallback = true
                });

            var decision = await controller.AdmitAsync(CreateRequest());

            Assert.Equal(AiRunAdmissionDecisionType.QueueGlobally, decision.DecisionType);
            Assert.True(decision.ShouldQueueGlobally);
            Assert.True(decision.Accepted);
        }

        [Fact]
        public async Task AdmitAsync_Should_Reject_When_No_Capacity_And_No_Fallback()
        {
            var registry = new FakeRuntimeInstanceRegistry(
                CreateInstance(
                    "runtime-1",
                    AiRuntimeInstanceStatus.Busy,
                    canAcceptRun: false,
                    queuedRunCount: 8,
                    runningRunCount: 2));

            var controller = CreateController(
                registry,
                new AiRunAdmissionOptions
                {
                    EnableScaleOutRequest = false,
                    EnableGlobalQueueFallback = false,
                    RejectWhenNoCapacity = true
                });

            var decision = await controller.AdmitAsync(CreateRequest());

            Assert.Equal(AiRunAdmissionDecisionType.Reject, decision.DecisionType);
            Assert.True(decision.Rejected);
            Assert.False(decision.Accepted);
        }

        [Fact]
        public async Task AdmitAsync_Should_Reject_When_Disabled()
        {
            var registry = new FakeRuntimeInstanceRegistry(
                CreateInstance(
                    "runtime-1",
                    AiRuntimeInstanceStatus.Ready,
                    canAcceptRun: true,
                    queuedRunCount: 0,
                    runningRunCount: 0));

            var controller = CreateController(
                registry,
                new AiRunAdmissionOptions
                {
                    Enabled = false
                });

            var decision = await controller.AdmitAsync(CreateRequest());

            Assert.Equal(AiRunAdmissionDecisionType.Reject, decision.DecisionType);
            Assert.Contains("disabled", decision.Reason);
        }

        [Fact]
        public async Task AdmitAsync_Should_Ignore_Paused_Instance_By_Default()
        {
            var registry = new FakeRuntimeInstanceRegistry(
                CreateInstance(
                    "runtime-1",
                    AiRuntimeInstanceStatus.Paused,
                    canAcceptRun: true,
                    queuedRunCount: 0,
                    runningRunCount: 0));

            var controller = CreateController(
                registry,
                new AiRunAdmissionOptions
                {
                    EnableScaleOutRequest = false,
                    EnableGlobalQueueFallback = false
                });

            var decision = await controller.AdmitAsync(CreateRequest());

            Assert.Equal(AiRunAdmissionDecisionType.Reject, decision.DecisionType);
            Assert.Equal(0, decision.AvailableInstanceCount);
        }

        [Fact]
        public async Task AdmitAsync_Should_Allow_Paused_Instance_When_Configured()
        {
            var registry = new FakeRuntimeInstanceRegistry(
                CreateInstance(
                    "runtime-1",
                    AiRuntimeInstanceStatus.Paused,
                    canAcceptRun: true,
                    queuedRunCount: 0,
                    runningRunCount: 0));

            var controller = CreateController(
                registry,
                new AiRunAdmissionOptions
                {
                    AllowPausedInstances = true
                });

            var decision = await controller.AdmitAsync(CreateRequest());

            Assert.Equal(AiRunAdmissionDecisionType.AssignToInstance, decision.DecisionType);
            Assert.Equal("runtime-1", decision.AssignedRuntimeInstanceId);
        }

        [Fact]
        public async Task AdmitAsync_Should_Request_ScaleOut_When_No_Instances_Are_Registered()
        {
            var registry = new FakeRuntimeInstanceRegistry();

            var controller = CreateController(
                registry,
                new AiRunAdmissionOptions
                {
                    MaxInstanceCount = 2,
                    EnableScaleOutRequest = true
                });

            var decision = await controller.AdmitAsync(CreateRequest());

            Assert.Equal(AiRunAdmissionDecisionType.RequestScaleOut, decision.DecisionType);
            Assert.Equal(0, decision.VisibleInstanceCount);
            Assert.Equal(0, decision.AvailableInstanceCount);
        }

        [Fact]
        public async Task AdmitAsync_Should_Throw_When_Request_Is_Null()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var controller = CreateController(registry);

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                controller.AdmitAsync(null!));
        }

        private static AiRunAdmissionController CreateController(
            IAiRuntimeInstanceRegistry registry,
            AiRunAdmissionOptions? options = null)
        {
            return new AiRunAdmissionController(
                registry,
                Options.Create(options ?? new AiRunAdmissionOptions()));
        }

        private static AiRunAdmissionRequest CreateRequest(
            string? preferredRuntimeInstanceId = null)
        {
            return new AiRunAdmissionRequest
            {
                PreferredRuntimeInstanceId = preferredRuntimeInstanceId,
                RunRequest = new AiRuntimePipelineRunRequest
                {
                    PipelineName = "pipeline-1"
                }
            };
        }

        private static AiRuntimeInstanceSnapshot CreateInstance(
            string runtimeInstanceId,
            AiRuntimeInstanceStatus status,
            bool canAcceptRun,
            int queuedRunCount,
            int runningRunCount)
        {
            var now = DateTimeOffset.UtcNow;

            return new AiRuntimeInstanceSnapshot
            {
                RuntimeInstanceId = runtimeInstanceId,
                Status = status,
                WorkerCount = 4,
                QueuedRunCount = queuedRunCount,
                RunningRunCount = runningRunCount,
                ActiveRunCount = queuedRunCount + runningRunCount,
                QueueCapacity = 8,
                MaxConcurrentRuns = 2,
                AvailableRunSlots = canAcceptRun ? 1 : 0,
                IsQueuePaused = status == AiRuntimeInstanceStatus.Paused,
                CanAcceptRun = canAcceptRun,
                RegisteredAtUtc = now,
                LastHeartbeatAtUtc = now,
                SnapshotAtUtc = now
            };
        }

        private sealed class FakeRuntimeInstanceRegistry : IAiRuntimeInstanceRegistry
        {
            private readonly IReadOnlyList<AiRuntimeInstanceSnapshot> _instances;

            public FakeRuntimeInstanceRegistry(
                params AiRuntimeInstanceSnapshot[] instances)
            {
                _instances = instances;
            }

            public Task<AiRuntimeInstanceSnapshot> RegisterAsync(
                AiRuntimeInstanceRegistration registration,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
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
                throw new NotSupportedException();
            }

            public Task<AiRuntimeInstanceSnapshot?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                    _instances.FirstOrDefault(instance =>
                        string.Equals(
                            instance.RuntimeInstanceId,
                            runtimeInstanceId,
                            StringComparison.Ordinal)));
            }

            public Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListAsync(
                bool includeStopped = false,
                CancellationToken cancellationToken = default)
            {
                IReadOnlyList<AiRuntimeInstanceSnapshot> result = includeStopped
                    ? _instances
                    : _instances
                        .Where(instance => instance.Status != AiRuntimeInstanceStatus.Stopped)
                        .ToArray();

                return Task.FromResult(result);
            }

            public Task<AiRuntimeInstanceSnapshot?> MarkDrainingAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<AiRuntimeInstanceSnapshot?> UnregisterAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }
    }
}