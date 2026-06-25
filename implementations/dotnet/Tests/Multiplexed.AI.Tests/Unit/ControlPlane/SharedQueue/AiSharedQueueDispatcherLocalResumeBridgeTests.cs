using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.Admission.Reservations;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.ControlPlane.SharedController;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue;
using Multiplexed.AI.Tests.Fixtures;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedQueue
{
    /// <summary>
    /// Tests the local recovery-resume bridge from the shared queue dispatcher to the runtime queue.
    /// </summary>
    public sealed class AiSharedQueueDispatcherLocalResumeBridgeTests
    {
        [Fact]
        public async Task DispatchNextAsync_Should_Trigger_RuntimeQueue_Resume_When_Recovery_Metadata_Is_Present()
        {
            var queue = new InMemoryAiSharedQueue();
            var store = new InMemoryAiSharedRunStore();
            var controller = new CapturingRuntimePipelineBackgroundController();
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();

            var runtimeQueueControlPlane = new AiRuntimeQueueControlPlane(
                controller,
                runExecutionIndex,
                Options.Create(new AiRuntimeQueueControlPlaneOptions()),
                new NoopAiControlPlaneObserver());

            var runtimeInstance = new LocalAiSharedRuntimeInstance(
                "runtime-1",
                runtimeQueueControlPlane);

            var runDispatcher = new LocalBridgeSharedRunDispatcher(
                runtimeInstance);

            await store.CreateAsync(
                CreateSharedRun(
                    "shared-run-1",
                    AiSharedRunStatus.QueuedGlobally,
                    metadata: new Dictionary<string, string>
                    {
                        ["recovery.mode"] = "resume-existing-execution",
                        ["recovery.failedExecutionId"] = "execution-existing-1",
                        ["recovery.failedRuntimeInstanceId"] = "runtime-failed-1",
                        ["recovery.failedLocalRunId"] = "run-failed-1",
                        ["recovery.reason"] = "unit-test-recovery"
                    }));

            await queue.EnqueueAsync(
                CreateQueueItem("shared-run-1"));

            var dispatcher = new AiSharedQueueDispatcher(
                queue,
                store,
                runDispatcher,
                new FakeRunAdmissionController(),
                new InMemoryAiRuntimeAdmissionReservationStore(),
                await CreateReadyRuntimeRegistryAsync(),
                new FakeRuntimeScaleOutRequestPublisher(),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                new FakeExecutionContextAccessor(),
                NullLogger<AiSharedQueueDispatcher>.Instance);

            var result = await dispatcher.DispatchNextAsync(new AiSharedQueueDispatchRequest
            {
                RuntimeInstanceId = "runtime-1",
                WorkerId = "worker-1",
                CorrelationId = "correlation-1",
                RequestedBy = "tester",
                Source = "unit-test",
                Reason = "dispatch recovery resume"
            });

            var indexed = await runExecutionIndex.GetAsync(
                result.DispatchResult!.LocalRunId!);

            Assert.True(result.Success);
            Assert.NotNull(result.DispatchResult);
            Assert.True(controller.EnqueueResumeCalled);
            Assert.False(controller.EnqueueCalled);
            Assert.Equal("execution-existing-1", controller.LastExecutionId);
            Assert.Equal("pipeline-1", controller.LastRunRequest?.PipelineName);
            Assert.Equal("execution-existing-1", result.DispatchResult!.ExecutionId);
            Assert.Equal("local-run-1", result.DispatchResult.LocalRunId);
            Assert.NotNull(indexed);
            Assert.Equal("execution-existing-1", indexed!.ExecutionId);
            Assert.Equal("True", indexed.Metadata["recovery.resume"]);
            Assert.Equal("execution-existing-1", indexed.Metadata["recovery.execution.id"]);
            Assert.Equal("resume-existing-execution", indexed.Metadata["recovery.mode"]);
            Assert.Equal("runtime-failed-1", indexed.Metadata["recovery.failedRuntimeInstanceId"]);
            Assert.Equal("run-failed-1", indexed.Metadata["recovery.failedLocalRunId"]);

            var sharedRun = await store.GetAsync("shared-run-1");
            Assert.NotNull(sharedRun);
            Assert.Equal(AiSharedRunStatus.Dispatched, sharedRun!.Status);
            Assert.Equal("local-run-1", sharedRun.LocalRunId);
            Assert.Equal("execution-existing-1", sharedRun.ExecutionId);
            Assert.Equal("runtime-1", sharedRun.AssignedRuntimeInstanceId);
        }

        private static async Task<InMemoryAiRuntimeInstanceRegistry> CreateReadyRuntimeRegistryAsync()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(
                    new AiRuntimeInstanceRegistration
                    {
                        RuntimeInstanceId = "runtime-1",
                        Role = AiRuntimeInstanceRole.Runtime,
                        HostName = "unit-test-host",
                        ProcessId = Environment.ProcessId,
                        WorkerCount = 1,
                        QueueCapacity = 100,
                        MaxConcurrentRuns = 1,
                        RuntimeVersion = "unit-test",
                        Metadata = new Dictionary<string, string>
                        {
                            ["test"] = "true"
                        }
                    })
                .ConfigureAwait(false);

            await registry.HeartbeatAsync(
                    "runtime-1",
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

            return registry;
        }

        private static AiSharedRunRecord CreateSharedRun(
            string sharedRunId,
            AiSharedRunStatus status,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            var now = DateTimeOffset.UtcNow;

            return new AiSharedRunRecord
            {
                SharedRunId = sharedRunId,
                Status = status,
                RunRequest = new AiRuntimePipelineRunRequest
                {
                    PipelineName = "pipeline-1",
                    PipelineDefinition = CreatePipelineDefinition()
                },
                ExecutionContextSnapshot = CreateExecutionContextSnapshot(),
                PipelineKey = "pipeline-1",
                CorrelationId = sharedRunId,
                SubmittedAtUtc = now,
                UpdatedAtUtc = now,
                Metadata = metadata ?? new Dictionary<string, string>()
            };
        }

        private static AiSharedQueueItem CreateQueueItem(
            string sharedRunId)
        {
            var now = DateTimeOffset.UtcNow;

            return new AiSharedQueueItem
            {
                SharedRunId = sharedRunId,
                Status = AiSharedQueueItemStatus.Pending,
                ExecutionContextSnapshot = CreateExecutionContextSnapshot(),
                PipelineKey = "pipeline-1",
                EnqueuedAtUtc = now,
                UpdatedAtUtc = now
            };
        }

        private static AiPipelineDefinition CreatePipelineDefinition()
        {
            return new AiPipelineDefinition
            {
                Name = "pipeline-1",
                ExecutionMode = AiExecutionMode.Dag,
                Version = "unit-test",
                Steps = new[]
                {
                    new AiPipelineStepDefinition
                    {
                        Name = "step-1",
                        StepKey = "noop",
                        Order = 0
                    }
                }
            };
        }

        private static ExecutionContextSnapshot CreateExecutionContextSnapshot()
        {
            return AiExecutionContextSnapshotTestFactory.Create(
                tenantId: "tenant-1",
                tenantGroupId: "tenant-group-1");
        }

        private sealed class LocalBridgeSharedRunDispatcher : IAiSharedRunDispatcher
        {
            private readonly IAiSharedRuntimeInstance runtimeInstance;

            public LocalBridgeSharedRunDispatcher(
                IAiSharedRuntimeInstance runtimeInstance)
            {
                this.runtimeInstance = runtimeInstance
                    ?? throw new ArgumentNullException(nameof(runtimeInstance));
            }

            public Task<AiSharedRunDispatchResult> DispatchAsync(
                AiSharedRunDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);

                return DispatchCoreAsync(
                    request,
                    cancellationToken);
            }

            private async Task<AiSharedRunDispatchResult> DispatchCoreAsync(
                AiSharedRunDispatchRequest request,
                CancellationToken cancellationToken)
            {
                var startedAtUtc = DateTimeOffset.UtcNow;

                var result = await runtimeInstance
                    .DispatchAsync(
                        new AiSharedRuntimeInstanceDispatchRequest
                        {
                            RuntimeInstanceId = request.RuntimeInstanceId,
                            SharedRun = request.SharedRun,
                            RunRequest = request.SharedRun.RunRequest!,
                            ClaimToken = request.ClaimToken,
                            CorrelationId = request.CorrelationId,
                            RequestedBy = request.RequestedBy,
                            Source = request.Source,
                            Reason = request.Reason,
                            Metadata = request.Metadata
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                var completedAtUtc = DateTimeOffset.UtcNow;

                return new AiSharedRunDispatchResult
                {
                    Success = result.Success,
                    SharedRunId = request.SharedRun.SharedRunId,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    LocalRunId = result.LocalRunId,
                    ExecutionId = result.ExecutionId,
                    ClaimToken = request.ClaimToken,
                    Message = result.Message,
                    FailureReason = result.FailureReason,
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = completedAtUtc,
                    DurationMs = (long)(completedAtUtc - startedAtUtc).TotalMilliseconds,
                };
            }
        }

        private sealed class CapturingRuntimePipelineBackgroundController : IAiRuntimePipelineBackgroundController
        {
            public bool EnqueueCalled { get; private set; }

            public bool EnqueueResumeCalled { get; private set; }

            public string? LastExecutionId { get; private set; }

            public string? LastRunId { get; private set; }

            public AiRuntimePipelineRunRequest? LastRunRequest { get; private set; }

            public Task StartAsync(
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task StopAsync(
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public ValueTask<AiRuntimeWorkerRunHandle> EnqueueAsync(
                AiRuntimePipelineRunRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);
                cancellationToken.ThrowIfCancellationRequested();

                EnqueueCalled = true;
                LastRunRequest = request;

                return ValueTask.FromResult(
                    CreateHandle("execution-new-1"));
            }

            public ValueTask<AiRuntimeWorkerRunHandle> EnqueueResumeAsync(
                AiRuntimePipelineRunRequest request,
                string executionId,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);
                ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
                cancellationToken.ThrowIfCancellationRequested();

                EnqueueResumeCalled = true;
                LastRunRequest = request;
                LastExecutionId = executionId;

                return ValueTask.FromResult(
                    CreateHandle(executionId));
            }

            public Task PauseQueueAsync(
                string? reason = null,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task ResumeQueueAsync(
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<bool> CancelQueuedRunAsync(
                string runId,
                string? reason = null,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(true);
            }

            public Task<bool> CancelRunAsync(
                string runId,
                string? reason = null,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(true);
            }

            public Task<AiRuntimePipelineRunState?> GetRunStateAsync(
                string runId,
                CancellationToken cancellationToken = default)
            {
                LastRunId = runId;

                return Task.FromResult<AiRuntimePipelineRunState?>(
                    new AiRuntimePipelineRunState
                    {
                        RunId = runId,
                        ExecutionId = LastExecutionId ?? "execution-new-1",
                        PipelineKey = "pipeline-1",
                        PipelineName = "pipeline-1",
                        RuntimeInstanceId = "runtime-1",
                        Status = "running",
                        IsQueued = false,
                        IsRunning = true
                    });
            }

            public Task<AiRuntimePipelineQueueState> GetQueueStateAsync(
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new AiRuntimePipelineQueueState
                {
                    RuntimeInstanceId = "runtime-1",
                    IsPaused = false,
                    QueuedRunCount = 0,
                    RunningRunCount = 1,
                    ActiveRunCount = 1,
                    QueueCapacity = 16,
                    MaxConcurrentRuns = 1,
                    AvailableRunSlots = 0,
                    CanAcceptRun = false,
                    SnapshotAtUtc = DateTimeOffset.UtcNow
                });
            }

            private static AiRuntimeWorkerRunHandle CreateHandle(
                string executionId)
            {
                var completionSource = new TaskCompletionSource<AiExecutionRecord>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                completionSource.SetResult(new AiExecutionRecord
                {
                    ExecutionId = executionId,
                    PipelineName = "pipeline-1",
                    Status = AiExecutionStatus.Completed,
                    CompletedAtUtc = DateTime.UtcNow
                });

                var handle = new AiRuntimeWorkerRunHandle(
                    "local-run-1",
                    completionSource.Task,
                    executionId);

                handle.MarkRunning(executionId);

                return handle;
            }
        }
    }
}
