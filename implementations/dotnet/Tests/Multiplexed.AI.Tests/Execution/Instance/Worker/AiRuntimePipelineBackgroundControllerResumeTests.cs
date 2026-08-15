using System.Reflection;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.ExecutionAssistance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.Observability.Logging;
using Multiplexed.Rbac.Core.ExecutionContext;
using ExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.AI.Tests.Unit.Execution.Instance.Worker
{
    /// <summary>
    /// Unit tests for runtime pipeline background controller resume dispatch.
    /// </summary>
    public sealed class AiRuntimePipelineBackgroundControllerResumeTests
    {
        /// <summary>
        /// Verifies that controlled recovery resume does not create a new execution and
        /// advances the existing durable execution identifier through the runtime worker.
        /// </summary>
        [Fact]
        public async Task EnqueueResumeAsync_Should_Run_Worker_With_Existing_ExecutionId()
        {
            const string existingExecutionId = "execution-existing-1";

            const string recoveryOwnerId =
                "runtime-recovery:execution-existing-1:shared-run-1:local-run-failed-1";

            var worker = new CapturingRuntimeInstanceWorker();
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();
            var lifecycleHook = new CapturingRunLifecycleHook();
            var executionControlService = new RecoveryExecutionControlService();

            var controller = CreateController(
                worker,
                runExecutionIndex,
                lifecycleHook,
                executionControlService);

            await controller
                .StartAsync()
                .ConfigureAwait(false);

            await controller
                .PauseQueueAsync(
                    reason: "hold resume queued for deterministic index assertion",
                    requestedBy: "unit-test")
                .ConfigureAwait(false);

            var handle = await controller
                .EnqueueResumeAsync(
                    new AiRuntimePipelineRunRequest
                    {
                        PipelineName = "pipeline-1",
                        ExecutionContextSnapshot = CreateExecutionContextSnapshot(),
                        PipelineDefinition = CreatePipelineDefinition(),
                        Metadata = new Dictionary<string, string>
                        {
                            ["recovery.mode"] = "resume-existing-execution",
                            ["recovery.forensicsId"] = recoveryOwnerId,
                            ["recovery.failedExecutionId"] = existingExecutionId,
                            ["recovery.failedRuntimeInstanceId"] = "runtime-instance-failed-1",
                            ["recovery.failedLocalRunId"] = "local-run-failed-1",
                            ["shared.run.id"] = "shared-run-1"
                        }
                    },
                    existingExecutionId)
                .ConfigureAwait(false);

            var queuedIndex =
                await runExecutionIndex
                    .GetAsync(handle.RunId)
                    .ConfigureAwait(false);

            Assert.NotNull(queuedIndex);
            Assert.Equal(existingExecutionId, queuedIndex!.ExecutionId);
            Assert.Equal("queued", queuedIndex.Status);
            Assert.Equal("true", queuedIndex.Metadata["recovery.resume"]);
            Assert.Equal(existingExecutionId, queuedIndex.Metadata["recovery.execution.id"]);
            Assert.Equal(recoveryOwnerId, queuedIndex.Metadata["recovery.forensicsId"]);

            await controller
                .ResumeQueueAsync(requestedBy: "unit-test")
                .ConfigureAwait(false);

            var final = await handle.Completion.WaitAsync(
                TimeSpan.FromSeconds(10));

            await controller
                .StopAsync()
                .ConfigureAwait(false);

            var indexed =
                await runExecutionIndex
                    .GetAsync(handle.RunId)
                    .ConfigureAwait(false);

            Assert.Equal(existingExecutionId, handle.ExecutionId);
            Assert.Equal(existingExecutionId, worker.LastExecutionId);
            Assert.Equal(existingExecutionId, final.ExecutionId);
            Assert.Equal(AiExecutionStatus.Completed, final.Status);
            Assert.NotNull(indexed);
            Assert.Equal(existingExecutionId, indexed!.ExecutionId);
            Assert.Equal("completed", indexed.Status);
            Assert.Equal(1, executionControlService.ResumeFromRecoveryCallCount);
            Assert.Equal(1, executionControlService.MarkRunningCallCount);
            Assert.Equal(recoveryOwnerId, executionControlService.LastRecoveryOwnerId);
            Assert.True(lifecycleHook.FinalizedCalled);
            Assert.Equal(existingExecutionId, lifecycleHook.LastExecutionId);
        }

        /// <summary>
        /// Verifies that an ambiguous retry for the same recovery owner resolves to one canonical local run.
        /// </summary>
        [Fact]
        public async Task EnqueueResumeAsync_Should_Return_Same_RunId_And_Execute_Only_Once_For_Same_RecoveryOwner()
        {
            const string existingExecutionId = "execution-existing-idempotent";
            const string recoveryOwnerId =
                "runtime-recovery:execution-existing-idempotent:shared-run-idempotent:local-run-failed-idempotent";

            var worker = new CapturingRuntimeInstanceWorker();
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();
            var lifecycleHook = new CapturingRunLifecycleHook();
            var executionControlService = new RecoveryExecutionControlService();
            var controller = CreateController(
                worker,
                runExecutionIndex,
                lifecycleHook,
                executionControlService);

            await controller.StartAsync().ConfigureAwait(false);
            await controller
                .PauseQueueAsync(
                    reason: "hold canonical recovery acceptance",
                    requestedBy: "unit-test")
                .ConfigureAwait(false);

            var request = new AiRuntimePipelineRunRequest
            {
                PipelineName = "pipeline-1",
                ExecutionContextSnapshot = CreateExecutionContextSnapshot(),
                PipelineDefinition = CreatePipelineDefinition(),
                Metadata = new Dictionary<string, string>
                {
                    ["recovery.mode"] = "resume-existing-execution",
                    ["recovery.forensicsId"] = recoveryOwnerId,
                    ["recovery.failedExecutionId"] = existingExecutionId,
                    ["recovery.failedRuntimeInstanceId"] = "runtime-instance-failed-idempotent",
                    ["recovery.failedLocalRunId"] = "local-run-failed-idempotent",
                    ["shared.run.id"] = "shared-run-idempotent"
                }
            };

            var first = await controller
                .EnqueueResumeAsync(
                    request,
                    existingExecutionId)
                .ConfigureAwait(false);

            var duplicate = await controller
                .EnqueueResumeAsync(
                    request,
                    existingExecutionId)
                .ConfigureAwait(false);

            Assert.Equal(first.RunId, duplicate.RunId);
            Assert.Equal(existingExecutionId, duplicate.ExecutionId);

            await controller
                .ResumeQueueAsync(requestedBy: "unit-test")
                .ConfigureAwait(false);

            var final = await first.Completion.WaitAsync(
                TimeSpan.FromSeconds(10));

            await controller.StopAsync().ConfigureAwait(false);

            Assert.Equal(AiExecutionStatus.Completed, final.Status);
            Assert.Equal(1, worker.RunExecutionCallCount);
            Assert.Equal(1, executionControlService.ResumeFromRecoveryCallCount);
            Assert.Equal(1, executionControlService.MarkRunningCallCount);
        }

        /// <summary>
        /// Verifies that two runtime-pool children racing the same durable recovery owner
        /// converge on one canonical runtime run and execute the DAG resume only once.
        /// </summary>
        [Fact]
        public async Task EnqueueResumeAsync_Across_RuntimePool_Children_Should_Accept_And_Execute_Only_Once()
        {
            const string existingExecutionId = "execution-existing-pool-race";
            const string recoveryOwnerId =
                "runtime-recovery:execution-existing-pool-race:shared-run-pool-race:local-run-failed-pool-race";

            var firstWorker = new CapturingRuntimeInstanceWorker();
            var secondWorker = new CapturingRuntimeInstanceWorker();
            var sharedRunExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();
            var executionControlService = new RecoveryExecutionControlService();

            var firstController = CreateController(
                firstWorker,
                sharedRunExecutionIndex,
                new CapturingRunLifecycleHook(),
                executionControlService,
                runtimeInstanceId: "runtime-pool-child-1");

            var secondController = CreateController(
                secondWorker,
                sharedRunExecutionIndex,
                new CapturingRunLifecycleHook(),
                executionControlService,
                runtimeInstanceId: "runtime-pool-child-2");

            await firstController.StartAsync().ConfigureAwait(false);
            await secondController.StartAsync().ConfigureAwait(false);

            await firstController
                .PauseQueueAsync(
                    reason: "hold first runtime-pool child",
                    requestedBy: "unit-test")
                .ConfigureAwait(false);

            await secondController
                .PauseQueueAsync(
                    reason: "hold second runtime-pool child",
                    requestedBy: "unit-test")
                .ConfigureAwait(false);

            var request = new AiRuntimePipelineRunRequest
            {
                PipelineName = "pipeline-1",
                ExecutionContextSnapshot = CreateExecutionContextSnapshot(),
                PipelineDefinition = CreatePipelineDefinition(),
                Metadata = new Dictionary<string, string>
                {
                    ["recovery.mode"] = "resume-existing-execution",
                    ["recovery.forensicsId"] = recoveryOwnerId,
                    ["recovery.failedExecutionId"] = existingExecutionId,
                    ["recovery.failedRuntimeInstanceId"] = "runtime-instance-failed-pool-race",
                    ["recovery.failedLocalRunId"] = "local-run-failed-pool-race",
                    ["shared.run.id"] = "shared-run-pool-race"
                }
            };

            var firstEnqueueTask = firstController
                .EnqueueResumeAsync(
                    request,
                    existingExecutionId)
                .AsTask();

            var secondEnqueueTask = secondController
                .EnqueueResumeAsync(
                    request,
                    existingExecutionId)
                .AsTask();

            var enqueueResults = await Task
                .WhenAll(
                    firstEnqueueTask,
                    secondEnqueueTask)
                .ConfigureAwait(false);

            Assert.Equal(enqueueResults[0].RunId, enqueueResults[1].RunId);

            var canonicalEntry = await sharedRunExecutionIndex
                .GetAsync(enqueueResults[0].RunId)
                .ConfigureAwait(false);

            Assert.NotNull(canonicalEntry);
            Assert.True(
                string.Equals(
                    canonicalEntry!.RuntimeInstanceId,
                    "runtime-pool-child-1",
                    StringComparison.Ordinal) ||
                string.Equals(
                    canonicalEntry.RuntimeInstanceId,
                    "runtime-pool-child-2",
                    StringComparison.Ordinal),
                $"Unexpected canonical runtime instance '{canonicalEntry.RuntimeInstanceId ?? string.Empty}'.");

            await firstController
                .ResumeQueueAsync(requestedBy: "unit-test")
                .ConfigureAwait(false);

            await secondController
                .ResumeQueueAsync(requestedBy: "unit-test")
                .ConfigureAwait(false);

            var canonicalHandle = string.Equals(
                    canonicalEntry.RuntimeInstanceId,
                    "runtime-pool-child-1",
                    StringComparison.Ordinal)
                ? enqueueResults[0]
                : enqueueResults[1];

            var final = await canonicalHandle.Completion.WaitAsync(
                TimeSpan.FromSeconds(10));

            await firstController.StopAsync().ConfigureAwait(false);
            await secondController.StopAsync().ConfigureAwait(false);

            var finalIndex = await sharedRunExecutionIndex
                .GetAsync(canonicalHandle.RunId)
                .ConfigureAwait(false);

            Assert.Equal(AiExecutionStatus.Completed, final.Status);
            Assert.Equal(1, firstWorker.RunExecutionCallCount + secondWorker.RunExecutionCallCount);
            Assert.Equal(1, executionControlService.ResumeFromRecoveryCallCount);
            Assert.Equal(1, executionControlService.MarkRunningCallCount);
            Assert.NotNull(finalIndex);
            Assert.Equal("completed", finalIndex!.Status);
            Assert.Equal(canonicalEntry.RuntimeInstanceId, finalIndex.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that a durable execution wait completes the current controller attempt,
        /// releases the single controller slot, and allows a later run to execute.
        /// </summary>
        [Fact]
        public async Task EnqueueResumeAsync_DurableWaiting_Should_Release_Controller_Slot_And_Allow_Next_Run()
        {
            const string waitingExecutionId = "execution-waiting-capacity";
            const string waitingRecoveryOwnerId =
                "runtime-recovery:execution-waiting-capacity:shared-run-waiting:local-run-waiting";

            const string nextExecutionId = "execution-after-waiting";
            const string nextRecoveryOwnerId =
                "runtime-recovery:execution-after-waiting:shared-run-next:local-run-next";

            var worker = new CapturingRuntimeInstanceWorker(
                AiExecutionStatus.Waiting,
                AiExecutionStatus.Completed);

            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();
            var lifecycleHook = new CapturingRunLifecycleHook();
            var executionControlService = new RecoveryExecutionControlService();
            var controller = CreateController(
                worker,
                runExecutionIndex,
                lifecycleHook,
                executionControlService);

            await controller.StartAsync().ConfigureAwait(false);

            var waitingHandle = await controller
                .EnqueueResumeAsync(
                    CreateRecoveryRequest(
                        waitingExecutionId,
                        waitingRecoveryOwnerId,
                        "shared-run-waiting",
                        "local-run-waiting"),
                    waitingExecutionId)
                .ConfigureAwait(false);

            var waiting = await waitingHandle.Completion.WaitAsync(
                TimeSpan.FromSeconds(10));

            var waitingIndex = await runExecutionIndex
                .GetAsync(waitingHandle.RunId)
                .ConfigureAwait(false);

            Assert.Equal(AiExecutionStatus.Waiting, waiting.Status);
            Assert.Equal(AiRuntimeWorkerRunStatus.Paused, waitingHandle.Status);
            Assert.NotNull(waitingIndex);
            Assert.Equal("waiting", waitingIndex!.Status);
            Assert.False(lifecycleHook.FinalizedCalled);

            var nextHandle = await controller
                .EnqueueResumeAsync(
                    CreateRecoveryRequest(
                        nextExecutionId,
                        nextRecoveryOwnerId,
                        "shared-run-next",
                        "local-run-next"),
                    nextExecutionId)
                .ConfigureAwait(false);

            var completed = await nextHandle.Completion.WaitAsync(
                TimeSpan.FromSeconds(10));

            await controller.StopAsync().ConfigureAwait(false);

            Assert.Equal(AiExecutionStatus.Completed, completed.Status);
            Assert.Equal(2, worker.RunExecutionCallCount);
            Assert.True(lifecycleHook.FinalizedCalled);
            Assert.Equal(nextExecutionId, lifecycleHook.LastExecutionId);
        }

        /// <summary>
        /// Creates a deterministic recovery-resume request for controller lifecycle tests.
        /// </summary>
        private static AiRuntimePipelineRunRequest CreateRecoveryRequest(
            string executionId,
            string recoveryOwnerId,
            string sharedRunId,
            string failedLocalRunId)
        {
            return new AiRuntimePipelineRunRequest
            {
                PipelineName = "pipeline-1",
                ExecutionContextSnapshot = CreateExecutionContextSnapshot(),
                PipelineDefinition = CreatePipelineDefinition(),
                Metadata = new Dictionary<string, string>
                {
                    ["recovery.mode"] = "resume-existing-execution",
                    ["recovery.forensicsId"] = recoveryOwnerId,
                    ["recovery.failedExecutionId"] = executionId,
                    ["recovery.failedRuntimeInstanceId"] = "runtime-instance-failed",
                    ["recovery.failedLocalRunId"] = failedLocalRunId,
                    ["shared.run.id"] = sharedRunId
                }
            };
        }

        /// <summary>
        /// Creates a runtime pipeline background controller with test doubles.
        /// </summary>
        private static AiRuntimePipelineBackgroundController CreateController(
            CapturingRuntimeInstanceWorker worker,
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            IAiRuntimePipelineRunLifecycleHook lifecycleHook,
            IAiExecutionControlService executionControlService,
            string runtimeInstanceId = "runtime-instance-1")
        {
            var engine = new AiDagExecutionEngine(
                NullProxy.Create<IAiDagExecutionEngineServices>(),
                NullProxy.Create<IAiDagExecutionEngineRuntimeServices>());

            return new AiRuntimePipelineBackgroundController(
                engine,
                worker,
                new CapturingRuntimeInstanceWorkerGroup(),
                new CapturingRuntimeInstanceWorkerFactory(worker),
                new StaticPipelineRunDefinitionResolver(),
                new NoopPipelineRunDefinitionPublisher(),
                lifecycleHook,
                executionControlService,
                new TestRuntimeInstanceIdentity(runtimeInstanceId),
                NullProxy.Create<IAiRuntimeLogger>(),
                NullProxy.Create<IAiRuntimeObservability>(),
                NullProxy.Create<IAiExecutionAssistanceCandidateStore>(),
                runExecutionIndex,
                new TestExecutionContextAccessor(),
                Options.Create(new AiRuntimePipelineBackgroundControllerOptions
                {
                    QueueCapacity = 16,
                    MaxConcurrentRuns = 1,
                    MaxLocalWorkersPerExecution = 1,
                    RejectEnqueueWhenStopped = true
                }));
        }

        /// <summary>
        /// Creates a minimal DAG pipeline definition.
        /// </summary>
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

        /// <summary>
        /// Creates a tenant execution context snapshot.
        /// </summary>
        private static ExecutionContextSnapshot CreateExecutionContextSnapshot()
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = $"unit-test-context-{Guid.NewGuid():N}",
                TenantId = "unit-test-tenant",
                TenantGroupId = "unit-test-tenant-group",
                Project = "deterministic-ai-runtime-tests",
                UserId = "unit-test-user",
                CurrentNamespace = "default",
                Namespaces = new List<NamespaceEntry>
                {
                    new()
                    {
                        Name = "default",
                        Trns = new HashSet<string>
                        {
                            "trn:deterministic-ai-runtime-tests:runtime:run:read",
                            "trn:deterministic-ai-runtime-tests:runtime:run:write",
                            "trn:deterministic-ai-runtime-tests:runtime:execution:read"
                        }
                    }
                },
                InFlightCount = 0,
                TtlSeconds = 300
            };
        }


        /// <summary>
        /// Provides the recovery-owned execution-control transitions required by resume.
        /// </summary>
        private sealed class RecoveryExecutionControlService : IAiExecutionControlService
        {
            private AiExecutionControlState? state;

            /// <summary>
            /// Gets the number of recovery resume requests.
            /// </summary>
            public int ResumeFromRecoveryCallCount { get; private set; }

            /// <summary>
            /// Gets the number of transitions to effective running state.
            /// </summary>
            public int MarkRunningCallCount { get; private set; }

            /// <summary>
            /// Gets the last deterministic recovery owner identifier.
            /// </summary>
            public string? LastRecoveryOwnerId { get; private set; }

            /// <inheritdoc />
            public Task<AiExecutionControlState> ResumeExecutionFromRecoveryAsync(
                string executionId,
                string recoveryOwnerId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
                ArgumentException.ThrowIfNullOrWhiteSpace(recoveryOwnerId);
                cancellationToken.ThrowIfCancellationRequested();

                this.ResumeFromRecoveryCallCount++;
                this.LastRecoveryOwnerId = recoveryOwnerId;
                this.state = new AiExecutionControlState
                {
                    ExecutionId = executionId,
                    Status = AiExecutionControlStatus.Resuming,
                    PendingAction = AiExecutionControlAction.Resume,
                    RequestedBy = recoveryOwnerId,
                    Reason = "unit-test recovery resume",
                    ResumeRequestedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                };

                return Task.FromResult(this.state);
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> MarkRunningAsync(
                string executionId,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
                cancellationToken.ThrowIfCancellationRequested();

                this.MarkRunningCallCount++;
                this.state = new AiExecutionControlState
                {
                    ExecutionId = executionId,
                    Status = AiExecutionControlStatus.Running,
                    PendingAction = AiExecutionControlAction.None,
                    RequestedBy = requestedBy ?? this.LastRecoveryOwnerId,
                    Reason = "unit-test recovery running",
                    UpdatedAtUtc = DateTime.UtcNow
                };

                return Task.FromResult(this.state);
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState?> GetStateAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(this.state);
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> PauseExecutionAsync(
                string executionId,
                string? reason = null,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> ResumeExecutionAsync(
                string executionId,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> CancelExecutionAsync(
                string executionId,
                string? reason = null,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> MarkCancelledAsync(
                string executionId,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> MarkWaitingForInputAsync(
                string executionId,
                string waitingKey,
                string? waitingStepName = null,
                string? reason = null,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> SubmitHumanInputAsync(
                string executionId,
                string waitingKey,
                IReadOnlyDictionary<string, object?> input,
                string? submittedBy = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiExecutionControlDecision> CheckCanAdvanceAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> MarkPausedAsync(
                string executionId,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiExecutionControlState> PauseExecutionForRecoveryAsync(
                string executionId,
                string recoveryOwnerId,
                string? reason = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }

        /// <summary>
        /// Runtime worker fake that captures the execution id it was asked to run.
        /// </summary>
        private sealed class CapturingRuntimeInstanceWorker : IAiRuntimeInstanceWorker
        {
            private readonly Queue<AiExecutionStatus> statuses;

            public CapturingRuntimeInstanceWorker(
                params AiExecutionStatus[] statuses)
            {
                this.statuses = new Queue<AiExecutionStatus>(
                    statuses.Length == 0
                        ? new[] { AiExecutionStatus.Completed }
                        : statuses);
            }

            public string? LastExecutionId { get; private set; }

            public int RunExecutionCallCount { get; private set; }

            public Task<AiExecutionRecord> RunExecutionAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
                cancellationToken.ThrowIfCancellationRequested();

                LastExecutionId = executionId;
                RunExecutionCallCount++;

                var status = statuses.Count > 0
                    ? statuses.Dequeue()
                    : AiExecutionStatus.Completed;

                return Task.FromResult(new AiExecutionRecord
                {
                    ExecutionId = executionId,
                    PipelineName = "pipeline-1",
                    Status = status,
                    CompletedAtUtc = status == AiExecutionStatus.Completed
                        ? DateTime.UtcNow
                        : default
                });
            }
        }

        /// <summary>
        /// Worker group fake used only if distributed mode is accidentally enabled.
        /// </summary>
        private sealed class CapturingRuntimeInstanceWorkerGroup : IAiRuntimeInstanceWorkerGroup
        {
            public Task<AiExecutionRecord> RunExecutionAsync(
                string executionId,
                IReadOnlyCollection<IAiRuntimeInstanceWorker> workers,
                CancellationToken cancellationToken = default)
            {
                return workers.First().RunExecutionAsync(
                    executionId,
                    cancellationToken);
            }
        }

        /// <summary>
        /// Worker factory fake.
        /// </summary>
        private sealed class CapturingRuntimeInstanceWorkerFactory : IAiRuntimeInstanceWorkerFactory
        {
            private readonly IAiRuntimeInstanceWorker worker;

            public CapturingRuntimeInstanceWorkerFactory(
                IAiRuntimeInstanceWorker worker)
            {
                this.worker = worker;
            }

            public IReadOnlyCollection<IAiRuntimeInstanceWorker> CreateWorkers(
                int workerCount)
            {
                return Enumerable
                    .Range(0, Math.Max(1, workerCount))
                    .Select(_ => worker)
                    .ToArray();
            }
        }

        /// <summary>
        /// Static pipeline definition resolver fake.
        /// </summary>
        private sealed class StaticPipelineRunDefinitionResolver : IAiRuntimePipelineRunDefinitionResolver
        {
            public Task<AiPipelineDefinition> ResolveAsync(
                AiRuntimePipelineRunRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(
                    request.PipelineDefinition ?? CreatePipelineDefinition());
            }
        }

        /// <summary>
        /// Pipeline definition publisher fake.
        /// </summary>
        private sealed class NoopPipelineRunDefinitionPublisher : IAiRuntimePipelineRunDefinitionPublisher
        {
            public Task PublishAsync(
                AiPipelineDefinition definition,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(definition);
                cancellationToken.ThrowIfCancellationRequested();

                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Run lifecycle hook fake.
        /// </summary>
        private sealed class CapturingRunLifecycleHook : IAiRuntimePipelineRunLifecycleHook
        {
            public bool FinalizedCalled { get; private set; }

            public string? LastExecutionId { get; private set; }

            public Task OnFinalizedAsync(
                AiRuntimePipelineRunFinalizedContext context,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(context);
                cancellationToken.ThrowIfCancellationRequested();

                FinalizedCalled = true;
                LastExecutionId = context.ExecutionId;

                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Runtime instance identity fake.
        /// </summary>
        private sealed class TestRuntimeInstanceIdentity : IAiRuntimeInstanceIdentityDescriptor
        {
            public TestRuntimeInstanceIdentity(
                string runtimeInstanceId)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
                RuntimeInstanceId = runtimeInstanceId;
            }

            public string RuntimeInstanceId { get; }

            public string HostName => "unit-test-host";

            public int ProcessId => Environment.ProcessId;

            public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
        }

        /// <summary>
        /// Execution context accessor fake.
        /// </summary>
        private sealed class TestExecutionContextAccessor : IExecutionContextAccessor
        {
            public ExecutionContext? Current { get; private set; }

            public void Set(
                ExecutionContext context)
            {
                Current = context;
            }

            public void Clear()
            {
                Current = null;
            }
        }

        /// <summary>
        /// Dynamic no-op proxy factory for large runtime service interfaces that are not
        /// relevant to this test.
        /// </summary>
        private static class NullProxy
        {
            public static T Create<T>()
                where T : class
            {
                return DispatchProxy.Create<T, NullDispatchProxy>();
            }

            public static object? Create(
                Type type)
            {
                var method = typeof(NullProxy)
                    .GetMethod(
                        nameof(Create),
                        BindingFlags.Public | BindingFlags.Static,
                        Type.EmptyTypes)!
                    .MakeGenericMethod(type);

                return method.Invoke(null, null);
            }
        }

        /// <summary>
        /// Dynamic no-op dispatch proxy.
        /// </summary>
        private class NullDispatchProxy : DispatchProxy
        {
            protected override object? Invoke(
                MethodInfo? targetMethod,
                object?[]? args)
            {
                if (targetMethod is null)
                {
                    return null;
                }

                var returnType = targetMethod.ReturnType;

                if (string.Equals(
                        targetMethod.Name,
                        "TraceExecutionAsync",
                        StringComparison.Ordinal) &&
                    args is not null)
                {
                    var callback = args.OfType<Delegate>().FirstOrDefault();

                    if (callback is not null)
                    {
                        return callback.DynamicInvoke();
                    }
                }

                if (returnType == typeof(void))
                {
                    return null;
                }

                if (returnType == typeof(Task))
                {
                    return Task.CompletedTask;
                }

                if (returnType.IsGenericType &&
                    returnType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    var resultType = returnType.GetGenericArguments()[0];
                    var fromResult = typeof(Task)
                        .GetMethod(nameof(Task.FromResult))!
                        .MakeGenericMethod(resultType);

                    return fromResult.Invoke(
                        null,
                        new[] { GetDefaultValue(resultType) });
                }

                if (returnType == typeof(ValueTask))
                {
                    return default(ValueTask);
                }

                if (returnType.IsGenericType &&
                    returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
                {
                    return Activator.CreateInstance(
                        returnType,
                        GetDefaultValue(returnType.GetGenericArguments()[0]));
                }

                if (returnType.IsInterface)
                {
                    return NullProxy.Create(returnType);
                }

                if (typeof(IDisposable).IsAssignableFrom(returnType))
                {
                    return DisposableScope.Instance;
                }

                if (returnType == typeof(bool))
                {
                    return false;
                }

                if (returnType == typeof(string))
                {
                    return string.Empty;
                }

                return GetDefaultValue(returnType);
            }

            private static object? GetDefaultValue(
                Type type)
            {
                return type.IsValueType
                    ? Activator.CreateInstance(type)
                    : null;
            }
        }

        /// <summary>
        /// Disposable no-op scope.
        /// </summary>
        private sealed class DisposableScope : IDisposable
        {
            public static readonly IDisposable Instance = new DisposableScope();

            public void Dispose()
            {
            }
        }
    }
}