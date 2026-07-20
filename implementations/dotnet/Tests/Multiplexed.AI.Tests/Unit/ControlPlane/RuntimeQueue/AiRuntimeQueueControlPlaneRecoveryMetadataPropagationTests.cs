using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Tests.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeQueue
{
    /// <summary>
    /// Tests recovery metadata propagation from the runtime queue control-plane to the local pipeline controller.
    /// </summary>
    public sealed class AiRuntimeQueueControlPlaneRecoveryMetadataPropagationTests
    {
        /// <summary>
        /// Verifies that recovery metadata is copied into the runtime pipeline run request before EnqueueResumeAsync is invoked.
        /// </summary>
        [Fact]
        public async Task EnqueueRunAsync_Should_Propagate_Recovery_Metadata_To_EnqueueResume_RunRequest()
        {
            var runExecutionIndex = new FakeRuntimeRunExecutionIndex();
            var controller = new FakeRuntimePipelineBackgroundController(runExecutionIndex);
            var observer = new FakeControlPlaneObserver();

            var controlPlane = new AiRuntimeQueueControlPlane(
                controller,
                runExecutionIndex,
                Options.Create(new AiRuntimeQueueControlPlaneOptions()),
                observer);

            var request = new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.EnqueueRun,
                RuntimeInstanceId = "runtime-replacement-1",
                RequestedBy = "test",
                Source = "unit-test",
                Reason = "recovery metadata propagation proof",
                CorrelationId = "correlation-1",
                IncludeRunState = true,
                IncludeQueueState = true,
                RunRequest = new AiRuntimePipelineRunRequest
                {
                    PipelineName = "pipeline-1",
                    ExecutionContextSnapshot = new ExecutionContextSnapshot
                    {
                        ContextKey = "ctx-tenant-1",
                        Project = "project-1",
                        UserId = "user-1",
                        TenantId = "tenant-1",
                        TenantGroupId = "tenant-group-1",
                        CurrentNamespace = "tenant-1",
                        Namespaces = [],
                        InFlightCount = 0,
                        TtlSeconds = 30,
                        CreatedAtUtc = DateTime.UtcNow
                    },
                    Input = new Dictionary<string, object?>
                    {
                        ["value"] = 42
                    },
                    Metadata = new Dictionary<string, string>
                    {
                        ["request.original"] = "kept"
                    }
                },
                Metadata = new Dictionary<string, string>
                {
                    ["recovery.mode"] = "resume-existing-execution",
                    ["recovery.forensicsId"] = "runtime-recovery:execution-1:shared-run-1:local-run-failed-1",
                    ["recovery.failedExecutionId"] = "execution-1",
                    ["recovery.failedRuntimeInstanceId"] = "runtime-failed-1",
                    ["recovery.failedLocalRunId"] = "local-run-failed-1",
                    ["shared.run.id"] = "shared-run-1"
                }
            };

            var result = await controlPlane
                .EnqueueRunAsync(request)
                .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.Equal("local-run-replacement-1", result.RunId);
            Assert.Equal("execution-1", result.ExecutionId);

            Assert.Equal(0, controller.EnqueueCalls);
            Assert.Equal(1, controller.EnqueueResumeCalls);
            Assert.NotNull(controller.LastResumeRequest);
            Assert.Equal("execution-1", controller.LastResumeExecutionId);

            Assert.Equal("kept", controller.LastResumeRequest!.Metadata["request.original"]);
            Assert.Equal("resume-existing-execution", controller.LastResumeRequest.Metadata["recovery.mode"]);
            Assert.Equal("runtime-recovery:execution-1:shared-run-1:local-run-failed-1", controller.LastResumeRequest.Metadata["recovery.forensicsId"]);
            Assert.Equal("execution-1", controller.LastResumeRequest.Metadata["recovery.failedExecutionId"]);
            Assert.Equal("runtime-failed-1", controller.LastResumeRequest.Metadata["recovery.failedRuntimeInstanceId"]);
            Assert.Equal("local-run-failed-1", controller.LastResumeRequest.Metadata["recovery.failedLocalRunId"]);
            Assert.Equal("shared-run-1", controller.LastResumeRequest.Metadata["shared.run.id"]);

            Assert.Single(runExecutionIndex.RegisteredEntries);
            Assert.Equal("execution-1", runExecutionIndex.RegisteredEntries[0].ExecutionId);
            Assert.Equal("runtime-recovery:execution-1:shared-run-1:local-run-failed-1", runExecutionIndex.RegisteredEntries[0].Metadata["recovery.forensicsId"]);
            Assert.Equal("runtime-failed-1", runExecutionIndex.RegisteredEntries[0].Metadata["recovery.failedRuntimeInstanceId"]);
            Assert.Equal("local-run-failed-1", runExecutionIndex.RegisteredEntries[0].Metadata["recovery.failedLocalRunId"]);
            Assert.Equal("true", runExecutionIndex.RegisteredEntries[0].Metadata["recovery.resume"].ToLower());
        }

        /// <summary>
        /// Fake runtime pipeline background controller.
        /// </summary>
        private sealed class FakeRuntimePipelineBackgroundController : IAiRuntimePipelineBackgroundController
        {
            private readonly IAiRuntimeRunExecutionIndex runExecutionIndex;

            /// <summary>
            /// Initializes a new instance of the <see cref="FakeRuntimePipelineBackgroundController"/> class.
            /// </summary>
            /// <param name="runExecutionIndex">
            /// The local run execution index owned by the background controller for resume registration.
            /// </param>
            public FakeRuntimePipelineBackgroundController(
                IAiRuntimeRunExecutionIndex runExecutionIndex)
            {
                this.runExecutionIndex =
                    runExecutionIndex ??
                    throw new ArgumentNullException(nameof(runExecutionIndex));
            }

            /// <summary>
            /// Gets the number of normal enqueue calls.
            /// </summary>
            public int EnqueueCalls { get; private set; }

            /// <summary>
            /// Gets the number of resume enqueue calls.
            /// </summary>
            public int EnqueueResumeCalls { get; private set; }

            /// <summary>
            /// Gets the last resume request.
            /// </summary>
            public AiRuntimePipelineRunRequest? LastResumeRequest { get; private set; }

            /// <summary>
            /// Gets the last resume execution identifier.
            /// </summary>
            public string? LastResumeExecutionId { get; private set; }

            /// <inheritdoc />
            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            /// <inheritdoc />
            public ValueTask<AiRuntimeWorkerRunHandle> EnqueueAsync(AiRuntimePipelineRunRequest request, CancellationToken cancellationToken = default)
            {
                EnqueueCalls++;
                return new ValueTask<AiRuntimeWorkerRunHandle>(
                    new AiRuntimeWorkerRunHandle(
                        "local-run-normal-1",
                        Task.FromResult(new Multiplexed.Abstractions.AI.Execution.AiExecutionRecord
                        {
                            ExecutionId = "execution-normal-1"
                        }),
                        "execution-normal-1"));
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

                EnqueueResumeCalls++;
                LastResumeRequest = request;
                LastResumeExecutionId = executionId;

                var runId = "local-run-replacement-1";

                await this.runExecutionIndex
                    .RegisterQueuedAsync(
                        new AiRuntimeRunExecutionIndexEntry
                        {
                            RunId = runId,
                            ExecutionId = executionId,
                            RuntimeInstanceId = "runtime-replacement-1",
                            Status = "queued",
                            CreatedAtUtc = DateTimeOffset.UtcNow,
                            ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                            Metadata = CreateResumeIndexMetadata(
                                request,
                                executionId)
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                return new AiRuntimeWorkerRunHandle(
                    runId,
                    Task.FromResult(
                        new Multiplexed.Abstractions.AI.Execution.AiExecutionRecord
                        {
                            ExecutionId = executionId
                        }),
                    executionId);
            }


            /// <summary>
            /// Creates the resume index metadata currently owned by the runtime pipeline background controller.
            /// </summary>
            /// <param name="request">The enriched resume run request.</param>
            /// <param name="executionId">The durable execution identifier being resumed.</param>
            /// <returns>The metadata persisted with the replacement local run index entry.</returns>
            private static IReadOnlyDictionary<string, string> CreateResumeIndexMetadata(
                AiRuntimePipelineRunRequest request,
                string executionId)
            {
                var metadata =
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["pipeline.name"] = request.PipelineName,
                        ["runtime.instance.id"] = "runtime-replacement-1",
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

            /// <inheritdoc />
            public Task PauseQueueAsync(string? reason = null, string? requestedBy = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task ResumeQueueAsync(string? requestedBy = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task<bool> CancelQueuedRunAsync(string runId, string? reason = null, string? requestedBy = null, CancellationToken cancellationToken = default) => Task.FromResult(false);

            /// <inheritdoc />
            public Task<bool> CancelRunAsync(string runId, string? reason = null, string? requestedBy = null, CancellationToken cancellationToken = default) => Task.FromResult(false);

            /// <inheritdoc />
            public Task<AiRuntimePipelineRunState?> GetRunStateAsync(string runId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiRuntimePipelineRunState?>(
                    new AiRuntimePipelineRunState
                    {
                        RunId = runId,
                        ExecutionId = "execution-1",
                        PipelineKey = "pipeline-1",
                        PipelineName = "pipeline-1",
                        RuntimeInstanceId = "runtime-replacement-1",
                        Status = "queued",
                        IsQueued = true,
                        IsRunning = false
                    });
            }

            /// <inheritdoc />
            public Task<AiRuntimePipelineQueueState> GetQueueStateAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new AiRuntimePipelineQueueState
                    {
                        RuntimeInstanceId = "runtime-replacement-1",
                        IsPaused = false,
                        QueuedRunCount = 1,
                        RunningRunCount = 0,
                        ActiveRunCount = 0,
                        QueueCapacity = 10,
                        MaxConcurrentRuns = 3,
                        AvailableRunSlots = 3,
                        WorkerCount = 1,
                        ActiveWorkerCount = 0,
                        AvailableWorkerCount = 1,
                        MaxLocalWorkersPerExecution = 1,
                        CanAcceptRun = true,
                        SnapshotAtUtc = DateTimeOffset.UtcNow
                    });
            }
        }

        /// <summary>
        /// Fake control-plane observer.
        /// </summary>
        private sealed class FakeControlPlaneObserver : IAiControlPlaneObserver
        {
            /// <inheritdoc />
            public Task RecordAsync(AiControlPlaneEvent controlPlaneEvent, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }
}