using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.SharedController;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedController
{
    /// <summary>
    /// Tests recovery forensics emitted by the local shared run dispatcher.
    /// </summary>
    public sealed class LocalAiSharedRunDispatcherForensicsTests
    {
        /// <summary>
        /// Verifies that local recovery redispatch records replacement local run and resume context forensics.
        /// </summary>
        [Fact]
        public async Task DispatchAsync_Should_Record_Local_Run_Registered_And_Resume_Context_Seeded_Forensics_When_Recovery_Redispatch_Succeeds()
        {
            var metadata = CreateRecoveryMetadata();
            var snapshot = CreateSnapshot();

            var sharedRun = CreateSharedRun(
                snapshot,
                metadata);

            var runtimeQueue = new FakeRuntimeQueueControlPlane
            {
                Result = new AiRuntimeQueueControlPlaneResult
                {
                    Operation = AiRuntimeQueueControlPlaneOperation.EnqueueRun,
                    Success = true,
                    Message = "enqueued",
                    RunId = "local-run-replacement-1",
                    ExecutionId = "execution-1",
                    RunHandle = new AiRuntimeWorkerRunHandle(
                        "local-run-replacement-1",
                        Task.FromResult(
                            new AiExecutionRecord
                            {
                                ExecutionId = "execution-1"
                            }),
                        "execution-1"),
                    Diagnostics = Array.Empty<string>(),
                    RuntimeInstanceId = "runtime-replacement-1",
                    StartedAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(-10),
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    DurationMs = 10
                }
            };

            var forensicsRecorder = new FakeRuntimeRecoveryForensicsRecorder();

            var dispatcher = new LocalAiSharedRunDispatcher(
                runtimeQueue,
                forensicsRecorder);

            var result = await dispatcher
                .DispatchAsync(
                    new AiSharedRunDispatchRequest
                    {
                        SharedRun = sharedRun,
                        RuntimeInstanceId = "runtime-replacement-1",
                        ClaimToken = "claim-token-1",
                        CorrelationId = "correlation-1",
                        RequestedBy = "test",
                        Source = "unit-test",
                        Reason = "recovery redispatch",
                        Metadata = metadata
                    })
                .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.Equal("shared-run-1", result.SharedRunId);
            Assert.Equal("runtime-replacement-1", result.RuntimeInstanceId);
            Assert.Equal("local-run-replacement-1", result.LocalRunId);
            Assert.Equal("execution-1", result.ExecutionId);
            Assert.Equal(1, runtimeQueue.EnqueueRunCalls);

            Assert.Equal(2, forensicsRecorder.Events.Count);

            var localRunEvent = forensicsRecorder.Events[0];
            var resumeContextEvent = forensicsRecorder.Events[1];

            Assert.Equal(AiRuntimeRecoveryForensicsEventType.ReplacementLocalRunRegistered, localRunEvent.EventType);
            Assert.Equal(AiRuntimeRecoveryForensicsEventType.ResumeContextSeeded, resumeContextEvent.EventType);

            Assert.Equal("runtime-recovery:execution-1:shared-run-1:local-run-failed-1", localRunEvent.ForensicsId);
            Assert.Equal(localRunEvent.ForensicsId, resumeContextEvent.ForensicsId);

            Assert.Equal("registered", localRunEvent.Outcome);
            Assert.Equal("seeded", resumeContextEvent.Outcome);

            Assert.Equal("execution-1", localRunEvent.ExecutionId);
            Assert.Equal("execution-1", resumeContextEvent.ExecutionId);

            Assert.Equal("shared-run-1", localRunEvent.SharedRunId);
            Assert.Equal("shared-run-1", resumeContextEvent.SharedRunId);

            Assert.Equal("local-run-replacement-1", localRunEvent.LocalRunId);
            Assert.Equal("local-run-replacement-1", resumeContextEvent.LocalRunId);

            Assert.Equal("runtime-replacement-1", localRunEvent.RuntimeInstanceId);
            Assert.Equal("runtime-replacement-1", resumeContextEvent.RuntimeInstanceId);

            Assert.Equal("runtime-failed-1", localRunEvent.Metadata["failed.runtimeInstanceId"]);
            Assert.Equal("local-run-failed-1", localRunEvent.Metadata["failed.localRunId"]);
            Assert.Equal("ctx-tenant-1", localRunEvent.Metadata["resume.contextKey"]);
            Assert.Equal("shared-run.execution-context-snapshot", localRunEvent.Metadata["resume.source"]);

            Assert.Equal("runtime-failed-1", resumeContextEvent.Metadata["failed.runtimeInstanceId"]);
            Assert.Equal("local-run-failed-1", resumeContextEvent.Metadata["failed.localRunId"]);
            Assert.Equal("ctx-tenant-1", resumeContextEvent.Metadata["resume.contextKey"]);
            Assert.Equal("shared-run.execution-context-snapshot", resumeContextEvent.Metadata["resume.source"]);
        }

        /// <summary>
        /// Creates recovery metadata used by recovery redispatch tests.
        /// </summary>
        /// <returns>The recovery metadata.</returns>
        private static IReadOnlyDictionary<string, string> CreateRecoveryMetadata()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["controlPlaneId"] = "control-plane-1",
                ["recovery.forensicsId"] = "runtime-recovery:execution-1:shared-run-1:local-run-failed-1",
                ["recovery.failedExecutionId"] = "execution-1",
                ["recovery.failedRuntimeInstanceId"] = "runtime-failed-1",
                ["recovery.failedLocalRunId"] = "local-run-failed-1"
            };
        }

        /// <summary>
        /// Creates a shared run record.
        /// </summary>
        /// <param name="snapshot">The execution context snapshot.</param>
        /// <param name="metadata">The shared run metadata.</param>
        /// <returns>The shared run record.</returns>
        private static AiSharedRunRecord CreateSharedRun(
            ExecutionContextSnapshot snapshot,
            IReadOnlyDictionary<string, string> metadata)
        {
            return new AiSharedRunRecord
            {
                SharedRunId = "shared-run-1",
                Status = AiSharedRunStatus.QueuedGlobally,
                RunRequest = new AiRuntimePipelineRunRequest
                {
                    PipelineName = "pipeline-1",
                    ExecutionContextSnapshot = snapshot,
                    Input = new Dictionary<string, object?>
                    {
                        ["value"] = 42
                    }
                },
                ExecutionContextSnapshot = snapshot,
                LocalRunId = "local-run-failed-1",
                ExecutionId = "execution-1",
                AssignedRuntimeInstanceId = "runtime-failed-1",
                AdmissionDecision = null,
                PipelineKey = "pipeline-1",
                CorrelationId = "correlation-1",
                RequestedBy = "test",
                Source = "unit-test",
                Reason = "recovery redispatch",
                FailureReason = null,
                SubmittedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
                UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                Metadata = metadata,
                ControlPlaneId = "control-plane-1"
            };
        }

        /// <summary>
        /// Creates an execution context snapshot.
        /// </summary>
        /// <returns>The execution context snapshot.</returns>
        private static ExecutionContextSnapshot CreateSnapshot()
        {
            return new ExecutionContextSnapshot
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
            };
        }

        /// <summary>
        /// Fake runtime queue control-plane.
        /// </summary>
        private sealed class FakeRuntimeQueueControlPlane : IAiRuntimeQueueControlPlane
        {
            /// <summary>
            /// Gets or sets the result returned by the fake queue.
            /// </summary>
            public required AiRuntimeQueueControlPlaneResult Result { get; set; }

            /// <summary>
            /// Gets the number of enqueue calls.
            /// </summary>
            public int EnqueueRunCalls { get; private set; }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> ExecuteAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Result);
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> EnqueueRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                EnqueueRunCalls++;

                return Task.FromResult(Result);
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> CancelRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Result);
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> CancelQueuedRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Result);
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> PauseQueueAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Result);
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> ResumeQueueAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Result);
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> GetRunStatusAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Result);
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> GetQueueStatusAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Result);
            }
        }

        /// <summary>
        /// Fake runtime recovery forensics recorder.
        /// </summary>
        private sealed class FakeRuntimeRecoveryForensicsRecorder : IAiRuntimeRecoveryForensicsRecorder
        {
            /// <summary>
            /// Gets recorded recovery forensics events.
            /// </summary>
            public List<AiRuntimeRecoveryForensicsEvent> Events { get; } = [];

            /// <inheritdoc />
            public Task RecordAsync(
                AiRuntimeRecoveryForensicsRecord record,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task RecordEventAsync(
                AiRuntimeRecoveryForensicsEvent recoveryEvent,
                CancellationToken cancellationToken = default)
            {
                Events.Add(recoveryEvent);

                return Task.CompletedTask;
            }
        }
    }
}