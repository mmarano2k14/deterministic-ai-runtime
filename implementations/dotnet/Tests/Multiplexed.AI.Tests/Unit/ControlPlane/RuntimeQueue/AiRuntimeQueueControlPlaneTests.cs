using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Tests.Fixtures;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeQueue
{
    public sealed class AiRuntimeQueueControlPlaneTests
    {
        [Fact]
        public async Task EnqueueRunAsync_Should_Call_Controller_And_Return_Handle_States()
        {
            var controller = new FakeRuntimePipelineBackgroundController();
            var controlPlane = CreateControlPlane(controller);

            var result = await controlPlane.EnqueueRunAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.EnqueueRun,
                RunRequest = new AiRuntimePipelineRunRequest
                {
                    PipelineName = "pipeline-1"
                }
            });

            Assert.True(result.Success);
            Assert.NotNull(result.RunHandle);
            Assert.NotNull(result.RunState);
            Assert.NotNull(result.QueueState);
            Assert.True(controller.EnqueueCalled);
            Assert.Equal("pipeline-1", controller.LastRunRequest?.PipelineName);
        }

        [Fact]
        public async Task CancelRunAsync_Should_Call_Controller_With_RunId_Reason_And_RequestedBy()
        {
            var controller = new FakeRuntimePipelineBackgroundController();
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();

            await RegisterIndexedRunAsync(runExecutionIndex);

            var controlPlane = CreateControlPlane(
                controller,
                runExecutionIndex: runExecutionIndex);

            var result = await controlPlane.CancelRunAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.CancelRun,
                RunId = "run-1",
                Reason = "operator cancel",
                RequestedBy = "tester"
            });

            Assert.True(result.Success);
            Assert.True(controller.CancelRunCalled);
            Assert.Equal("run-1", controller.LastRunId);
            Assert.Equal("operator cancel", controller.LastReason);
            Assert.Equal("tester", controller.LastRequestedBy);
        }

        [Fact]
        public async Task CancelQueuedRunAsync_Should_Call_Controller_With_RunId_Reason_And_RequestedBy()
        {
            var controller = new FakeRuntimePipelineBackgroundController();
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();

            await RegisterIndexedRunAsync(runExecutionIndex);

            var controlPlane = CreateControlPlane(
                controller,
                runExecutionIndex: runExecutionIndex);

            var result = await controlPlane.CancelQueuedRunAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.CancelQueuedRun,
                RunId = "run-1",
                Reason = "queued cancel",
                RequestedBy = "tester"
            });

            Assert.True(result.Success);
            Assert.True(controller.CancelQueuedRunCalled);
            Assert.Equal("run-1", controller.LastRunId);
            Assert.Equal("queued cancel", controller.LastReason);
            Assert.Equal("tester", controller.LastRequestedBy);
        }

        [Fact]
        public async Task PauseQueueAsync_Should_Call_Controller_With_Reason_And_RequestedBy()
        {
            var controller = new FakeRuntimePipelineBackgroundController();
            var controlPlane = CreateControlPlane(controller);

            var result = await controlPlane.PauseQueueAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.PauseQueue,
                Reason = "maintenance",
                RequestedBy = "tester"
            });

            Assert.True(result.Success);
            Assert.True(controller.PauseQueueCalled);
            Assert.Equal("maintenance", controller.LastReason);
            Assert.Equal("tester", controller.LastRequestedBy);
            Assert.NotNull(result.QueueState);
            Assert.True(result.QueueState.IsPaused);
        }

        [Fact]
        public async Task ResumeQueueAsync_Should_Call_Controller_With_RequestedBy()
        {
            var controller = new FakeRuntimePipelineBackgroundController();
            var controlPlane = CreateControlPlane(controller);

            var result = await controlPlane.ResumeQueueAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.ResumeQueue,
                RequestedBy = "tester"
            });

            Assert.True(result.Success);
            Assert.True(controller.ResumeQueueCalled);
            Assert.Equal("tester", controller.LastRequestedBy);
            Assert.NotNull(result.QueueState);
            Assert.False(result.QueueState.IsPaused);
        }

        [Fact]
        public async Task GetRunStatusAsync_Should_Call_GetRunStateAsync()
        {
            var controller = new FakeRuntimePipelineBackgroundController();
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();

            await RegisterIndexedRunAsync(
                runExecutionIndex,
                status: "running");

            var controlPlane = CreateControlPlane(
                controller,
                runExecutionIndex: runExecutionIndex);

            var result = await controlPlane.GetRunStatusAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.GetRunStatus,
                RunId = "run-1",
                IncludeRunState = true
            });

            Assert.True(result.Success);
            Assert.True(controller.GetRunStateCalled);
            Assert.NotNull(result.RunState);
            Assert.Equal("run-1", result.RunState.RunId);
        }

        [Fact]
        public async Task GetQueueStatusAsync_Should_Call_GetQueueStateAsync()
        {
            var controller = new FakeRuntimePipelineBackgroundController();
            var controlPlane = CreateControlPlane(controller);

            var result = await controlPlane.GetQueueStatusAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.GetQueueStatus
            });

            Assert.True(result.Success);
            Assert.True(controller.GetQueueStateCalled);
            Assert.NotNull(result.QueueState);
        }

        [Fact]
        public async Task ExecuteAsync_Should_Dispatch_By_Operation()
        {
            var controller = new FakeRuntimePipelineBackgroundController();
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();

            await RegisterIndexedRunAsync(runExecutionIndex);

            var controlPlane = CreateControlPlane(
                controller,
                runExecutionIndex: runExecutionIndex);

            var result = await controlPlane.ExecuteAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.CancelRun,
                RunId = "run-1",
                Reason = "dispatch cancel",
                RequestedBy = "tester"
            });

            Assert.True(result.Success);
            Assert.True(controller.CancelRunCalled);
            Assert.Equal(AiRuntimeQueueControlPlaneOperation.CancelRun, result.Operation);
        }

        [Fact]
        public async Task CancelRunAsync_Should_Return_Failure_When_RunId_Is_Missing()
        {
            var controller = new FakeRuntimePipelineBackgroundController();
            var controlPlane = CreateControlPlane(controller);

            var result = await controlPlane.CancelRunAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.CancelRun
            });

            Assert.False(result.Success);
            Assert.Contains("RunId is required", result.FailureReason);
        }

        [Fact]
        public async Task EnqueueRunAsync_Should_Return_Failure_When_RunRequest_Is_Missing()
        {
            var controller = new FakeRuntimePipelineBackgroundController();
            var controlPlane = CreateControlPlane(controller);

            var result = await controlPlane.EnqueueRunAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.EnqueueRun
            });

            Assert.False(result.Success);
            Assert.Contains("RunRequest is required", result.FailureReason);
        }

        [Fact]
        public async Task PauseQueueAsync_Should_Return_Failure_When_Disabled()
        {
            var controller = new FakeRuntimePipelineBackgroundController();

            var controlPlane = CreateControlPlane(
                controller,
                new AiRuntimeQueueControlPlaneOptions
                {
                    EnablePauseQueue = false
                });

            var result = await controlPlane.PauseQueueAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.PauseQueue
            });

            Assert.False(result.Success);
            Assert.Contains("disabled", result.FailureReason);
        }

        [Fact]
        public async Task PauseQueueAsync_Should_Record_Started_And_Completed_Events()
        {
            var controller = new FakeRuntimePipelineBackgroundController();
            var observer = new CapturingControlPlaneObserver();

            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();

            var controlPlane = new AiRuntimeQueueControlPlane(
                controller,
                runExecutionIndex,
                Options.Create(new AiRuntimeQueueControlPlaneOptions()),
                observer);

            var result = await controlPlane.PauseQueueAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.PauseQueue,
                CorrelationId = "correlation-1",
                RuntimeInstanceId = "runtime-instance-1",
                Source = "unit-test",
                RequestedBy = "tester",
                Reason = "maintenance"
            });

            Assert.True(result.Success);
            Assert.Equal(2, observer.Events.Count);

            Assert.Equal(AiControlPlaneEventType.OperationStarted, observer.Events[0].EventType);
            Assert.Equal(AiControlPlaneEventType.OperationCompleted, observer.Events[1].EventType);

            Assert.All(observer.Events, controlPlaneEvent =>
            {
                Assert.Equal(AiControlPlaneArea.RunControl, controlPlaneEvent.Area);
                Assert.Equal("PauseQueue", controlPlaneEvent.Operation);
                Assert.Equal("correlation-1", controlPlaneEvent.Correlation.CorrelationId);
                Assert.Equal("runtime-instance-1", controlPlaneEvent.Correlation.RuntimeInstanceId);
            });
        }

        [Fact]
        public async Task EnqueueRunAsync_Should_Call_Controller_Resume_When_Recovery_Metadata_Is_Present()
        {
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();
            var controller =
                new RecoveryIndexingRuntimePipelineBackgroundController(
                    runExecutionIndex,
                    "runtime-instance-1");

            var controlPlane = CreateControlPlane(
                controller,
                runExecutionIndex: runExecutionIndex);

            var result = await controlPlane.EnqueueRunAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.EnqueueRun,
                RunRequest = new AiRuntimePipelineRunRequest
                {
                    PipelineName = "pipeline-1",
                    ExecutionContextSnapshot = CreateExecutionContextSnapshot()
                },
                Metadata = new Dictionary<string, string>
                {
                    ["recovery.mode"] = "resume-existing-execution",
                    ["recovery.forensicsId"] =
                        "runtime-recovery:execution-existing-1:shared-run-1:run-failed-1",
                    ["recovery.failedExecutionId"] = "execution-existing-1",
                    ["recovery.failedRuntimeInstanceId"] = "runtime-instance-failed-1",
                    ["recovery.failedLocalRunId"] = "run-failed-1",
                    ["recovery.reason"] = "unit-test-recovery"
                },
                Source = "unit-test",
                RequestedBy = "tester",
                Reason = "resume existing execution",
                CorrelationId = "correlation-1",
                RuntimeInstanceId = "runtime-instance-1"
            });

            Assert.True(result.Success, result.FailureReason);
            Assert.False(string.IsNullOrWhiteSpace(result.RunId));
            Assert.NotNull(result.RunHandle);

            var indexed = await runExecutionIndex
                .GetAsync(result.RunId!)
                .ConfigureAwait(false);

            Assert.Equal("execution-existing-1", result.ExecutionId);
            Assert.True(controller.EnqueueResumeCalled);
            Assert.False(controller.EnqueueCalled);
            Assert.Equal("execution-existing-1", controller.LastExecutionId);
            Assert.Equal("pipeline-1", controller.LastRunRequest?.PipelineName);
            Assert.NotNull(indexed);
            Assert.Equal(result.RunId, indexed!.RunId);
            Assert.Equal("execution-existing-1", indexed.ExecutionId);
            Assert.Equal("true", indexed.Metadata["recovery.resume"]);
            Assert.Equal("execution-existing-1", indexed.Metadata["recovery.execution.id"]);
            Assert.Equal("resume-existing-execution", indexed.Metadata["recovery.mode"]);
            Assert.Equal(
                "runtime-recovery:execution-existing-1:shared-run-1:run-failed-1",
                indexed.Metadata["recovery.forensicsId"]);
            Assert.Equal("runtime-instance-failed-1", indexed.Metadata["recovery.failedRuntimeInstanceId"]);
            Assert.Equal("run-failed-1", indexed.Metadata["recovery.failedLocalRunId"]);
        }

        private static AiRuntimeQueueControlPlane CreateControlPlane(
            IAiRuntimePipelineBackgroundController controller,
            AiRuntimeQueueControlPlaneOptions? options = null,
            IAiRuntimeRunExecutionIndex? runExecutionIndex = null)
        {
            return new AiRuntimeQueueControlPlane(
                controller,
                runExecutionIndex ?? new InMemoryAiRuntimeRunExecutionIndex(),
                Options.Create(options ?? new AiRuntimeQueueControlPlaneOptions()),
                new NoopAiControlPlaneObserver());
        }

        private static async Task RegisterIndexedRunAsync(
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            string runId = "run-1",
            string executionId = "execution-1",
            string runtimeInstanceId = "runtime-instance-1",
            string status = "running")
        {
            await runExecutionIndex
                .RegisterQueuedAsync(
                    new AiRuntimeRunExecutionIndexEntry
                    {
                        RunId = runId,
                        ExecutionId = executionId,
                        RuntimeInstanceId = runtimeInstanceId,
                        Status = status,
                        CreatedAtUtc = DateTimeOffset.UtcNow,
                        ExecutionContextSnapshot = CreateExecutionContextSnapshot(),
                        Metadata = new Dictionary<string, string>
                        {
                            ["source"] = "unit-test",
                            ["requestedBy"] = "tester",
                            ["tenantId"] = "unit-test-tenant"
                        }
                    })
                .ConfigureAwait(false);
        }

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
        /// Wraps the shared runtime pipeline fake while reproducing the production ownership
        /// of resume run execution-index registration.
        /// </summary>
        private sealed class RecoveryIndexingRuntimePipelineBackgroundController
            : IAiRuntimePipelineBackgroundController
        {
            private readonly FakeRuntimePipelineBackgroundController inner = new();
            private readonly IAiRuntimeRunExecutionIndex runExecutionIndex;
            private readonly string runtimeInstanceId;

            /// <summary>
            /// Initializes a new instance of the
            /// <see cref="RecoveryIndexingRuntimePipelineBackgroundController"/> class.
            /// </summary>
            /// <param name="runExecutionIndex">The local runtime run execution index.</param>
            /// <param name="runtimeInstanceId">The replacement runtime instance identifier.</param>
            public RecoveryIndexingRuntimePipelineBackgroundController(
                IAiRuntimeRunExecutionIndex runExecutionIndex,
                string runtimeInstanceId)
            {
                this.runExecutionIndex =
                    runExecutionIndex ??
                    throw new ArgumentNullException(nameof(runExecutionIndex));

                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
                this.runtimeInstanceId = runtimeInstanceId;
            }

            /// <summary>
            /// Gets a value indicating whether normal enqueue was called.
            /// </summary>
            public bool EnqueueCalled => this.inner.EnqueueCalled;

            /// <summary>
            /// Gets a value indicating whether resume enqueue was called.
            /// </summary>
            public bool EnqueueResumeCalled => this.inner.EnqueueResumeCalled;

            /// <summary>
            /// Gets the last durable execution identifier passed to resume.
            /// </summary>
            public string? LastExecutionId => this.inner.LastExecutionId;

            /// <summary>
            /// Gets the last runtime pipeline request.
            /// </summary>
            public AiRuntimePipelineRunRequest? LastRunRequest => this.inner.LastRunRequest;

            /// <inheritdoc />
            public Task StartAsync(
                CancellationToken cancellationToken = default)
            {
                return this.inner.StartAsync(cancellationToken);
            }

            /// <inheritdoc />
            public Task StopAsync(
                CancellationToken cancellationToken = default)
            {
                return this.inner.StopAsync(cancellationToken);
            }

            /// <inheritdoc />
            public ValueTask<AiRuntimeWorkerRunHandle> EnqueueAsync(
                AiRuntimePipelineRunRequest request,
                CancellationToken cancellationToken = default)
            {
                return this.inner.EnqueueAsync(
                    request,
                    cancellationToken);
            }

            /// <inheritdoc />
            public async ValueTask<AiRuntimeWorkerRunHandle> EnqueueResumeAsync(
                AiRuntimePipelineRunRequest request,
                string executionId,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);
                ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
                cancellationToken.ThrowIfCancellationRequested();

                var handle =
                    await this.inner
                        .EnqueueResumeAsync(
                            request,
                            executionId,
                            cancellationToken)
                        .ConfigureAwait(false);

                await this.runExecutionIndex
                    .RegisterQueuedAsync(
                        new AiRuntimeRunExecutionIndexEntry
                        {
                            RunId = handle.RunId,
                            ExecutionId = executionId,
                            RuntimeInstanceId = this.runtimeInstanceId,
                            Status = "queued",
                            CreatedAtUtc = DateTimeOffset.UtcNow,
                            ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                            Metadata = CreateResumeIndexMetadata(
                                request,
                                executionId,
                                this.runtimeInstanceId)
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                return handle;
            }

            /// <inheritdoc />
            public Task PauseQueueAsync(
                string? reason = null,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                return this.inner.PauseQueueAsync(
                    reason,
                    requestedBy,
                    cancellationToken);
            }

            /// <inheritdoc />
            public Task ResumeQueueAsync(
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                return this.inner.ResumeQueueAsync(
                    requestedBy,
                    cancellationToken);
            }

            /// <inheritdoc />
            public Task<bool> CancelQueuedRunAsync(
                string runId,
                string? reason = null,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                return this.inner.CancelQueuedRunAsync(
                    runId,
                    reason,
                    requestedBy,
                    cancellationToken);
            }

            /// <inheritdoc />
            public Task<bool> CancelRunAsync(
                string runId,
                string? reason = null,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                return this.inner.CancelRunAsync(
                    runId,
                    reason,
                    requestedBy,
                    cancellationToken);
            }

            /// <inheritdoc />
            public Task<AiRuntimePipelineRunState?> GetRunStateAsync(
                string runId,
                CancellationToken cancellationToken = default)
            {
                return this.inner.GetRunStateAsync(
                    runId,
                    cancellationToken);
            }

            /// <inheritdoc />
            public Task<AiRuntimePipelineQueueState> GetQueueStateAsync(
                CancellationToken cancellationToken = default)
            {
                return this.inner.GetQueueStateAsync(cancellationToken);
            }

            /// <summary>
            /// Creates the exact resume metadata currently persisted by the production
            /// runtime pipeline background controller.
            /// </summary>
            private static IReadOnlyDictionary<string, string> CreateResumeIndexMetadata(
                AiRuntimePipelineRunRequest request,
                string executionId,
                string runtimeInstanceId)
            {
                var metadata =
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["pipeline.name"] = request.PipelineName,
                        ["runtime.instance.id"] = runtimeInstanceId,
                        ["recovery.resume"] = "true",
                        ["recovery.execution.id"] = executionId,
                        ["context.key"] =
                            request.ExecutionContextSnapshot?.ContextKey ??
                            string.Empty,
                        ["tenant.id"] =
                            request.ExecutionContextSnapshot?.TenantId ??
                            string.Empty,
                        ["tenant.group.id"] =
                            request.ExecutionContextSnapshot?.TenantGroupId ??
                            string.Empty
                    };

                if (request.Metadata is not null)
                {
                    foreach (var item in request.Metadata)
                    {
                        metadata[item.Key] = item.Value;
                    }
                }

                return metadata;
            }
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
    }
}