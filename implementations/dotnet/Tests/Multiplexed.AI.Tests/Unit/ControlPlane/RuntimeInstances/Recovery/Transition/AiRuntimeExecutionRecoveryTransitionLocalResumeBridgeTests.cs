using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
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
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.ControlPlane.SharedController;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue;
using Multiplexed.AI.Tests.Fixtures;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Recovery.Transition
{
    /// <summary>
    /// Tests the recovery transition to local runtime resume bridge.
    /// </summary>
    public sealed class AiRuntimeExecutionRecoveryTransitionLocalResumeBridgeTests
    {
        [Fact]
        public async Task ApplyAsync_Should_Requeue_With_Resume_Metadata_And_Dispatch_To_Local_Runtime_Resume()
        {
            var sharedQueue = new InMemoryAiSharedQueue();
            var sharedRunStore = new InMemoryAiSharedRunStore();
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();

            await sharedRunStore.CreateAsync(
                CreateSharedRun(
                    "shared-run-1",
                    AiSharedRunStatus.QueuedGlobally));

            await sharedQueue.EnqueueAsync(
                CreateQueueItem(
                    "shared-run-1"));

            await runExecutionIndex.RegisterQueuedAsync(new AiRuntimeRunExecutionIndexEntry
            {
                RunId = "run-failed-1",
                ExecutionId = "execution-existing-1",
                RuntimeInstanceId = "runtime-failed-1",
                Status = "queued",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExecutionContextSnapshot = CreateExecutionContextSnapshot(),
                Metadata = new Dictionary<string, string>
                {
                    ["test"] = "true"
                }
            });

            await runExecutionIndex.MarkStartedAsync(
                "run-failed-1",
                "execution-existing-1");

            var claimed = await sharedQueue.ClaimNextAsync(new AiSharedQueueClaimRequest
            {
                RuntimeInstanceId = "runtime-failed-1",
                WorkerId = "worker-failed-1",
                PipelineKey = "pipeline-1",
                ClaimTtl = TimeSpan.FromMinutes(5),
                Reason = "test-dispatch-before-recovery"
            });

            Assert.NotNull(claimed);
            Assert.False(string.IsNullOrWhiteSpace(claimed!.ClaimToken));

            await sharedQueue.MarkDispatchedAsync(
                "shared-run-1",
                claimed.ClaimToken!,
                reason: "test-dispatched-before-recovery");

            var recoveryTransition = new AiRuntimeExecutionRecoveryTransitionService(
                sharedQueue,
                runExecutionIndex,
                Options.Create(new AiRuntimeExecutionRecoveryReconciliationOptions
                {
                    EnableDagExecutionResume = true
                }));

            var transitionResult = await recoveryTransition.ApplyAsync(new AiRuntimeExecutionRecoveryTransitionRequest
            {
                Ownership = CreateOwnership(
                    claimToken: claimed.ClaimToken),
                Reason = "test-recovery-requeue",
                DryRun = false
            });

            var requeuedItem = await sharedQueue.GetAsync("shared-run-1");

            Assert.True(transitionResult.Accepted);
            Assert.True(transitionResult.Changed);
            Assert.NotNull(requeuedItem);
            Assert.Equal(AiSharedQueueItemStatus.Pending, requeuedItem!.Status);
            Assert.Equal("resume-existing-execution", requeuedItem.Metadata["recovery.mode"]);
            Assert.Equal("execution-existing-1", requeuedItem.Metadata["recovery.failedExecutionId"]);
            Assert.Equal("runtime-failed-1", requeuedItem.Metadata["recovery.failedRuntimeInstanceId"]);
            Assert.Equal("run-failed-1", requeuedItem.Metadata["recovery.failedLocalRunId"]);
            Assert.Equal("test-recovery-requeue", requeuedItem.Metadata["recovery.reason"]);

            var controller = new CapturingRuntimePipelineBackgroundController();

            var runtimeQueueControlPlane = new AiRuntimeQueueControlPlane(
                controller,
                runExecutionIndex,
                Options.Create(new AiRuntimeQueueControlPlaneOptions()),
                new NoopAiControlPlaneObserver());

            var runtimeInstance = new LocalAiSharedRuntimeInstance(
                "runtime-1",
                runtimeQueueControlPlane);

            var dispatcher = new AiSharedQueueDispatcher(
                sharedQueue,
                sharedRunStore,
                new LocalBridgeSharedRunDispatcher(runtimeInstance),
                new FakeRunAdmissionController(),
                new InMemoryAiRuntimeAdmissionReservationStore(),
                await CreateReadyRuntimeRegistryAsync(),
                new FakeRuntimeScaleOutRequestPublisher(),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                new FakeExecutionContextAccessor(),
                NullLogger<AiSharedQueueDispatcher>.Instance);

            var dispatchResult = await dispatcher.DispatchNextAsync(new AiSharedQueueDispatchRequest
            {
                RuntimeInstanceId = "runtime-1",
                WorkerId = "worker-2",
                CorrelationId = "correlation-2",
                RequestedBy = "tester",
                Source = "unit-test",
                Reason = "dispatch recovered shared run"
            });

            var newIndexEntry = await runExecutionIndex.GetAsync("local-run-1");
            var failedIndexEntry = await runExecutionIndex.GetAsync("run-failed-1");
            var sharedRun = await sharedRunStore.GetAsync("shared-run-1");

            Assert.True(dispatchResult.Success);
            Assert.NotNull(dispatchResult.DispatchResult);
            Assert.True(controller.EnqueueResumeCalled);
            Assert.False(controller.EnqueueCalled);
            Assert.Equal("execution-existing-1", controller.LastExecutionId);
            Assert.Equal("pipeline-1", controller.LastRunRequest?.PipelineName);
            Assert.Equal("execution-existing-1", dispatchResult.DispatchResult!.ExecutionId);
            Assert.Equal("local-run-1", dispatchResult.DispatchResult.LocalRunId);

            Assert.NotNull(newIndexEntry);
            Assert.Equal("local-run-1", newIndexEntry!.RunId);
            Assert.Equal("execution-existing-1", newIndexEntry.ExecutionId);
            Assert.Equal("runtime-1", newIndexEntry.RuntimeInstanceId);
            Assert.Equal("True", newIndexEntry.Metadata["recovery.resume"]);
            Assert.Equal("execution-existing-1", newIndexEntry.Metadata["recovery.execution.id"]);
            Assert.Equal("resume-existing-execution", newIndexEntry.Metadata["recovery.mode"]);
            Assert.Equal("runtime-failed-1", newIndexEntry.Metadata["recovery.failedRuntimeInstanceId"]);
            Assert.Equal("run-failed-1", newIndexEntry.Metadata["recovery.failedLocalRunId"]);

            Assert.NotNull(failedIndexEntry);
            Assert.Equal("requeued-for-recovery", failedIndexEntry!.Status);

            Assert.NotNull(sharedRun);
            Assert.Equal(AiSharedRunStatus.Dispatched, sharedRun!.Status);
            Assert.Equal("runtime-1", sharedRun.AssignedRuntimeInstanceId);
            Assert.Equal("local-run-1", sharedRun.LocalRunId);
            Assert.Equal("execution-existing-1", sharedRun.ExecutionId);
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
            AiSharedRunStatus status)
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = CreateExecutionContextSnapshot();

            return new AiSharedRunRecord
            {
                SharedRunId = sharedRunId,
                Status = status,
                RunRequest = new AiRuntimePipelineRunRequest
                {
                    PipelineName = "pipeline-1",
                    PipelineDefinition = CreatePipelineDefinition(),
                    ExecutionContextSnapshot = snapshot
                },
                ExecutionContextSnapshot = snapshot,
                PipelineKey = "pipeline-1",
                CorrelationId = sharedRunId,
                SubmittedAtUtc = now,
                UpdatedAtUtc = now,
                Metadata = new Dictionary<string, string>
                {
                    ["original"] = "true"
                }
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
                Priority = 0,
                EnqueuedAtUtc = now,
                UpdatedAtUtc = now,
                Metadata = new Dictionary<string, string>
                {
                    ["queue"] = "true"
                }
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

        private static AiSharedRunOwnershipResolutionResult CreateOwnership(
            string? claimToken)
        {
            return new AiSharedRunOwnershipResolutionResult
            {
                Resolved = true,
                SharedRunId = "shared-run-1",
                RuntimeInstanceId = "runtime-failed-1",
                LocalRunId = "run-failed-1",
                ExecutionId = "execution-existing-1",
                TenantId = "tenant-1",
                TenantGroupId = "tenant-group-1",
                QueueStatus = AiSharedQueueItemStatus.Dispatched,
                SharedRunStatus = AiSharedRunStatus.Dispatched,
                ClaimToken = claimToken,
                CanRecover = true,
                Reason = "shared-run-ownership-resolved"
            };
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
