using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.SharedController;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using Multiplexed.AI.Tests.Fixtures;
using StackExchange.Redis;

namespace Multiplexed.AI.Tests.Integration.Runtime.ControlPlane.SharedController
{
    public sealed class RedisAiSharedRuntimeControllerTests : IAsyncLifetime
    {
        private readonly string _keyPrefix =
            $"test:ai:shared-controller:{Guid.NewGuid():N}";

        private IConnectionMultiplexer? _connection;

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

            var keys = server.Keys(
                    database: database.Database,
                    pattern: $"{_keyPrefix}*")
                .ToArray();

            if (keys.Length > 0)
            {
                await database.KeyDeleteAsync(keys);
            }

            await _connection.CloseAsync();
            await _connection.DisposeAsync();
        }

        [Fact]
        public async Task SubmitRunAsync_Should_Persist_Shared_Run_In_Redis()
        {
            var controller = CreateController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.AssignToInstance,
                    AssignedRuntimeInstanceId = "runtime-1",
                    AssignedInstance = CreateRuntimeInstance("runtime-1"),
                    Reason = "Runtime instance selected.",
                    VisibleInstanceCount = 1,
                    AvailableInstanceCount = 1,
                    CurrentInstanceCount = 1
                });

            var submit = await controller.SubmitRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "shared-run-1",
                RunRequest = CreateRunRequest(),
                CorrelationId = "correlation-1",
                RequestedBy = "tester",
                Source = "unit-test"
            });

            Assert.True(submit.Success);
            Assert.NotNull(submit.Run);
            Assert.Equal("shared-run-1", submit.SharedRunId);
            Assert.Equal(AiSharedRunStatus.Dispatched, submit.Run.Status);
            Assert.Equal("runtime-1", submit.AssignedRuntimeInstanceId);
            Assert.Equal("local-run-1", submit.LocalRunId);
            Assert.Equal("execution-1", submit.ExecutionId);

            var get = await controller.GetRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.GetRun,
                SharedRunId = "shared-run-1"
            });

            Assert.True(get.Success);
            Assert.NotNull(get.Run);
            Assert.Equal("shared-run-1", get.Run.SharedRunId);
            Assert.Equal(AiSharedRunStatus.Dispatched, get.Run.Status);
            Assert.Equal("runtime-1", get.Run.AssignedRuntimeInstanceId);
            Assert.Equal("local-run-1", get.Run.LocalRunId);
            Assert.Equal("execution-1", get.Run.ExecutionId);
            Assert.Equal("pipeline-1", get.Run.RunRequest.PipelineName);
        }

        [Fact]
        public async Task CancelRunAsync_Should_Cancel_Shared_Run_In_Redis()
        {
            var controller = CreateController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.QueueGlobally,
                    Reason = "No local capacity.",
                    VisibleInstanceCount = 1,
                    AvailableInstanceCount = 0,
                    CurrentInstanceCount = 1
                });

            await controller.SubmitRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "shared-run-1",
                RunRequest = CreateRunRequest()
            });

            var cancel = await controller.CancelRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.CancelRun,
                SharedRunId = "shared-run-1",
                Reason = "operator cancel",
                RequestedBy = "tester",
                Source = "unit-test"
            });

            Assert.True(cancel.Success);
            Assert.NotNull(cancel.Run);
            Assert.Equal(AiSharedRunStatus.Cancelled, cancel.Run.Status);
            Assert.Equal("operator cancel", cancel.Run.FailureReason);
            Assert.Equal("tester", cancel.Run.RequestedBy);
            Assert.Equal("unit-test", cancel.Run.Source);

            var get = await controller.GetRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.GetRun,
                SharedRunId = "shared-run-1"
            });

            Assert.True(get.Success);
            Assert.NotNull(get.Run);
            Assert.Equal(AiSharedRunStatus.Cancelled, get.Run.Status);
        }

        [Fact]
        public async Task ListRunsAsync_Should_Read_Shared_Runs_From_Redis()
        {
            var controller = CreateController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.QueueGlobally
                });

            await controller.SubmitRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "shared-run-1",
                RunRequest = CreateRunRequest()
            });

            await controller.SubmitRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "shared-run-2",
                RunRequest = CreateRunRequest()
            });

            var list = await controller.ListRunsAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.ListRuns
            });

            Assert.True(list.Success);
            Assert.Equal(2, list.Runs.Count);
            Assert.Contains(list.Runs, run => run.SharedRunId == "shared-run-1");
            Assert.Contains(list.Runs, run => run.SharedRunId == "shared-run-2");
        }

        [Fact]
        public async Task SubmitRunAsync_Should_Enqueue_SharedQueue_Item_When_Admission_Queues_Globally()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.QueueGlobally,
                    Reason = "No instance capacity."
                });

            var sharedQueue = new InMemoryAiSharedQueue();

            var controller = new AiSharedRuntimeController(
                admission,
                new InMemoryAiSharedRunStore(),
                sharedQueue,
                new FakeSharedRunDispatcher(),
                new NoopAiRuntimeScaleOutRequestPublisher(),
                new StaticAiControlPlaneIdResolver("test-control-plane"),
                Options.Create(new AiSharedRuntimeControllerOptions()),
                new NoopAiControlPlaneObserver());

            var result = await controller.SubmitRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "shared-run-1",
                RunRequest = CreateRunRequest(),
                TenantId = "tenant-1",
                PipelineKey = "pipeline-1"
            });

            Assert.True(result.Success);
            Assert.NotNull(result.Run);
            Assert.Equal(AiSharedRunStatus.QueuedGlobally, result.Run.Status);

            var queueItem = await sharedQueue.GetAsync("shared-run-1");

            Assert.NotNull(queueItem);
            Assert.Equal("shared-run-1", queueItem!.SharedRunId);
            Assert.Equal(AiSharedQueueItemStatus.Pending, queueItem.Status);
            Assert.Equal("tenant-1", queueItem.TenantId);
            Assert.Equal("pipeline-1", queueItem.PipelineKey);
            Assert.Equal("No instance capacity.", queueItem.Reason);
        }

        [Fact]
        public async Task SubmitRunAsync_Should_Persist_And_Publish_ScaleOut_Request_In_Redis()
        {
            var publisher = new CapturingScaleOutPublisher();

            var controller = CreateController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.RequestScaleOut,
                    Reason = "Scale-out required.",
                    VisibleInstanceCount = 1,
                    AvailableInstanceCount = 0,
                    CurrentInstanceCount = 1,
                    MaxInstanceCount = 3
                },
                scaleOutPublisher: publisher);

            var submit = await controller.SubmitRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "shared-run-scale-1",
                RunRequest = CreateRunRequest(),
                TenantId = "tenant-1",
                PipelineKey = "pipeline-1",
                CorrelationId = "correlation-scale-1",
                RequestedBy = "tester",
                Source = "redis-integration-test"
            });

            Assert.True(submit.Success);
            Assert.NotNull(submit.Run);
            Assert.Equal("shared-run-scale-1", submit.SharedRunId);
            Assert.Equal(AiSharedRunStatus.ScaleOutRequested, submit.Run.Status);

            var get = await controller.GetRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.GetRun,
                SharedRunId = "shared-run-scale-1"
            });

            Assert.True(get.Success);
            Assert.NotNull(get.Run);
            Assert.Equal("shared-run-scale-1", get.Run.SharedRunId);
            Assert.Equal(AiSharedRunStatus.ScaleOutRequested, get.Run.Status);
            Assert.Equal("pipeline-1", get.Run.RunRequest.PipelineName);

            Assert.NotNull(publisher.LastRequest);
            Assert.Equal("shared-run-scale-1", publisher.LastRequest!.SharedRunId);
            Assert.Equal("tenant-1", publisher.LastRequest.TenantId);
            Assert.Equal("pipeline-1", publisher.LastRequest.PipelineKey);
            Assert.Equal(1, publisher.LastRequest.VisibleInstanceCount);
            Assert.Equal(0, publisher.LastRequest.AvailableInstanceCount);
            Assert.Equal(1, publisher.LastRequest.CurrentInstanceCount);
            Assert.Equal(3, publisher.LastRequest.MaxInstanceCount);
            Assert.Equal("correlation-scale-1", publisher.LastRequest.CorrelationId);
            Assert.Equal("tester", publisher.LastRequest.RequestedBy);
            Assert.Equal("redis-integration-test", publisher.LastRequest.Source);
            Assert.Equal("Scale-out required.", publisher.LastRequest.Reason);
        }

        private AiSharedRuntimeController CreateController(
            AiRunAdmissionDecision admissionDecision,
            IAiSharedRunDispatcher? dispatcher = null,
            IAiRuntimeScaleOutRequestPublisher? scaleOutPublisher = null)
        {
            if (_connection is null)
            {
                throw new InvalidOperationException("Redis connection was not initialized.");
            }

            var store = new RedisAiSharedRunStore(
                _connection,
                Options.Create(new RedisAiSharedRunStoreOptions
                {
                    KeyPrefix = _keyPrefix,
                    ListScanLimit = 100
                }), 
                new StaticAiControlPlaneIdResolver("test-control-plane"));

            return new AiSharedRuntimeController(
                new FakeRunAdmissionController(admissionDecision),
                store,
                new InMemoryAiSharedQueue(),
                dispatcher ?? new FakeSharedRunDispatcher(),
                scaleOutPublisher ?? new NoopAiRuntimeScaleOutRequestPublisher(),
                new StaticAiControlPlaneIdResolver("test-control-plane"),
                Options.Create(new AiSharedRuntimeControllerOptions()),
                new NoopAiControlPlaneObserver());
        }

        private static AiRuntimePipelineRunRequest CreateRunRequest()
        {
            return new AiRuntimePipelineRunRequest
            {
                PipelineName = "pipeline-1"
            };
        }

        private static AiRuntimeInstanceSnapshot CreateRuntimeInstance(
            string runtimeInstanceId)
        {
            var now = DateTimeOffset.UtcNow;

            return new AiRuntimeInstanceSnapshot
            {
                RuntimeInstanceId = runtimeInstanceId,
                Status = AiRuntimeInstanceStatus.Ready,
                WorkerCount = 4,
                QueuedRunCount = 0,
                RunningRunCount = 0,
                ActiveRunCount = 0,
                QueueCapacity = 8,
                MaxConcurrentRuns = 2,
                AvailableRunSlots = 2,
                IsQueuePaused = false,
                CanAcceptRun = true,
                RegisteredAtUtc = now,
                LastHeartbeatAtUtc = now,
                SnapshotAtUtc = now
            };
        }

        private sealed class FakeRunAdmissionController : IAiRunAdmissionController
        {
            private readonly AiRunAdmissionDecision _decision;

            public FakeRunAdmissionController(
                AiRunAdmissionDecision decision)
            {
                _decision = decision;
            }

            public Task<AiRunAdmissionDecision> AdmitAsync(
                AiRunAdmissionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_decision);
            }
        }

        private sealed class FakeSharedRunDispatcher : IAiSharedRunDispatcher
        {
            private readonly AiSharedRunDispatchResult _result;

            public FakeSharedRunDispatcher(
                AiSharedRunDispatchResult? result = null)
            {
                var now = DateTimeOffset.UtcNow;

                _result = result ?? new AiSharedRunDispatchResult
                {
                    Success = true,
                    SharedRunId = "shared-run-1",
                    RuntimeInstanceId = "runtime-1",
                    LocalRunId = "local-run-1",
                    ExecutionId = "execution-1",
                    Message = "Dispatched.",
                    StartedAtUtc = now,
                    CompletedAtUtc = now
                };
            }

            public AiSharedRunDispatchRequest? LastRequest { get; private set; }

            public Task<AiSharedRunDispatchResult> DispatchAsync(
                AiSharedRunDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;

                var now = DateTimeOffset.UtcNow;

                return Task.FromResult(new AiSharedRunDispatchResult
                {
                    Success = _result.Success,
                    SharedRunId = request.SharedRun.SharedRunId,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    LocalRunId = _result.LocalRunId,
                    ExecutionId = _result.ExecutionId,
                    ClaimToken = request.ClaimToken,
                    Message = _result.Message,
                    FailureReason = _result.FailureReason,
                    StartedAtUtc = _result.StartedAtUtc == default ? now : _result.StartedAtUtc,
                    CompletedAtUtc = _result.CompletedAtUtc == default ? now : _result.CompletedAtUtc,
                    DurationMs = _result.DurationMs,
                    Diagnostics = _result.Diagnostics
                });
            }
        }

        private sealed class CapturingScaleOutPublisher : IAiRuntimeScaleOutRequestPublisher
        {
            public AiRuntimeScaleOutRequest? LastRequest { get; private set; }

            public Task<AiRuntimeScaleOutRequestResult> PublishAsync(
                AiRuntimeScaleOutRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;

                return Task.FromResult(new AiRuntimeScaleOutRequestResult
                {
                    Success = true,
                    SharedRunId = request.SharedRunId,
                    ScaleOutRequestId = $"redis-test-scale-out-{request.SharedRunId}",
                    RequestedTargetInstanceCount = request.CurrentInstanceCount + 1,
                    Message = "Scale-out request captured.",
                    PublishedAtUtc = DateTimeOffset.UtcNow
                });
            }
        }
    }
}