using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.SharedInstance;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.SharedInstance
{
    /// <summary>
    /// Tests local shared runtime instance dispatch into the runtime queue control plane.
    /// </summary>
    public sealed class LocalAiSharedRuntimeInstanceResumeDispatchTests
    {
        [Fact]
        public async Task DispatchAsync_Should_Forward_Recovery_Resume_Metadata_To_RuntimeQueueControlPlane()
        {
            var queueControlPlane = new CapturingRuntimeQueueControlPlane();
            var runtimeInstance = new LocalAiSharedRuntimeInstance(
                "runtime-1",
                queueControlPlane);

            var sharedRun = CreateSharedRun(
                metadata: new Dictionary<string, string>
                {
                    ["recovery.mode"] = "resume-existing-execution",
                    ["recovery.failedExecutionId"] = "execution-existing-1",
                    ["recovery.failedRuntimeInstanceId"] = "runtime-failed-1",
                    ["recovery.failedLocalRunId"] = "run-failed-1",
                    ["recovery.reason"] = "unit-test-recovery"
                });

            var result = await runtimeInstance.DispatchAsync(
                new AiSharedRuntimeInstanceDispatchRequest
                {
                    RuntimeInstanceId = "runtime-1",
                    SharedRun = sharedRun,
                    RunRequest = sharedRun.RunRequest!,
                    ClaimToken = "claim-token-1",
                    CorrelationId = "correlation-1",
                    RequestedBy = "tester",
                    Source = "unit-test",
                    Reason = "dispatch recovery resume",
                    Metadata = new Dictionary<string, string>
                    {
                        ["request.marker"] = "dispatch-request"
                    }
                });

            Assert.True(result.Success);
            Assert.Equal("local-run-1", result.LocalRunId);
            Assert.Equal("execution-existing-1", result.ExecutionId);
            Assert.NotNull(queueControlPlane.LastEnqueueRequest);
            Assert.Equal(AiRuntimeQueueControlPlaneOperation.EnqueueRun, queueControlPlane.LastEnqueueRequest!.Operation);
            Assert.Equal("correlation-1", queueControlPlane.LastEnqueueRequest.CorrelationId);
            Assert.Equal("tester", queueControlPlane.LastEnqueueRequest.RequestedBy);
            Assert.Equal("unit-test", queueControlPlane.LastEnqueueRequest.Source);
            Assert.Equal("dispatch recovery resume", queueControlPlane.LastEnqueueRequest.Reason);
            Assert.NotNull(queueControlPlane.LastEnqueueRequest.RunRequest);
            Assert.NotNull(queueControlPlane.LastEnqueueRequest.RunRequest!.ExecutionContextSnapshot);
            Assert.Equal("tenant-1", queueControlPlane.LastEnqueueRequest.RunRequest.ExecutionContextSnapshot!.TenantId);
            Assert.Equal("resume-existing-execution", queueControlPlane.LastEnqueueRequest.Metadata["recovery.mode"]);
            Assert.Equal("execution-existing-1", queueControlPlane.LastEnqueueRequest.Metadata["recovery.failedExecutionId"]);
            Assert.Equal("runtime-failed-1", queueControlPlane.LastEnqueueRequest.Metadata["recovery.failedRuntimeInstanceId"]);
            Assert.Equal("run-failed-1", queueControlPlane.LastEnqueueRequest.Metadata["recovery.failedLocalRunId"]);
            Assert.Equal("unit-test-recovery", queueControlPlane.LastEnqueueRequest.Metadata["recovery.reason"]);
            Assert.Equal("dispatch-request", queueControlPlane.LastEnqueueRequest.Metadata["request.marker"]);
            Assert.Equal("shared-run-1", queueControlPlane.LastEnqueueRequest.Metadata["shared.run.id"]);
            Assert.Equal("runtime-1", queueControlPlane.LastEnqueueRequest.Metadata["runtime.instance.id"]);
            Assert.Equal("claim-token-1", queueControlPlane.LastEnqueueRequest.Metadata["claim.token"]);
            Assert.True(
                queueControlPlane.LastEnqueueRequest.Metadata.ContainsKey("tenantId") ||
                queueControlPlane.LastEnqueueRequest.Metadata.ContainsKey("tenant.id") ||
                queueControlPlane.LastEnqueueRequest.Metadata.ContainsKey("TenantId"));
            Assert.True(queueControlPlane.GetRunStatusCalled);
        }

        private static AiSharedRunRecord CreateSharedRun(
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            var now = DateTimeOffset.UtcNow;

            return new AiSharedRunRecord
            {
                SharedRunId = "shared-run-1",
                Status = AiSharedRunStatus.QueuedGlobally,
                RunRequest = new AiRuntimePipelineRunRequest
                {
                    PipelineName = "pipeline-1",
                    PipelineDefinition = new AiPipelineDefinition
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
                    }
                },
                ExecutionContextSnapshot = CreateExecutionContextSnapshot(),
                PipelineKey = "pipeline-1",
                CorrelationId = "correlation-1",
                SubmittedAtUtc = now,
                UpdatedAtUtc = now,
                Metadata = metadata ?? new Dictionary<string, string>()
            };
        }

        private static ExecutionContextSnapshot CreateExecutionContextSnapshot()
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = $"unit-test-context-{Guid.NewGuid():N}",
                TenantId = "tenant-1",
                TenantGroupId = "tenant-group-1",
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

        private sealed class CapturingRuntimeQueueControlPlane : IAiRuntimeQueueControlPlane
        {
            public AiRuntimeQueueControlPlaneRequest? LastEnqueueRequest { get; private set; }

            public bool GetRunStatusCalled { get; private set; }

            public Task<AiRuntimeQueueControlPlaneResult> ExecuteAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                return request.Operation switch
                {
                    AiRuntimeQueueControlPlaneOperation.EnqueueRun => EnqueueRunAsync(request, cancellationToken),
                    AiRuntimeQueueControlPlaneOperation.GetRunStatus => GetRunStatusAsync(request, cancellationToken),
                    AiRuntimeQueueControlPlaneOperation.GetQueueStatus => GetQueueStatusAsync(request, cancellationToken),
                    AiRuntimeQueueControlPlaneOperation.CancelRun => CancelRunAsync(request, cancellationToken),
                    AiRuntimeQueueControlPlaneOperation.CancelQueuedRun => CancelQueuedRunAsync(request, cancellationToken),
                    AiRuntimeQueueControlPlaneOperation.PauseQueue => PauseQueueAsync(request, cancellationToken),
                    AiRuntimeQueueControlPlaneOperation.ResumeQueue => ResumeQueueAsync(request, cancellationToken),
                    _ => throw new NotSupportedException($"Operation '{request.Operation}' is not supported by this test fake.")
                };
            }

            public Task<AiRuntimeQueueControlPlaneResult> EnqueueRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);
                cancellationToken.ThrowIfCancellationRequested();

                LastEnqueueRequest = request;

                var completionSource = new TaskCompletionSource<AiExecutionRecord>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                completionSource.SetResult(new AiExecutionRecord
                {
                    ExecutionId = "execution-existing-1",
                    PipelineName = "pipeline-1",
                    Status = AiExecutionStatus.Completed,
                    CompletedAtUtc = DateTime.UtcNow
                });

                var handle = new AiRuntimeWorkerRunHandle(
                    "local-run-1",
                    completionSource.Task,
                    "execution-existing-1");

                handle.MarkRunning("execution-existing-1");

                return Task.FromResult(new AiRuntimeQueueControlPlaneResult
                {
                    Operation = AiRuntimeQueueControlPlaneOperation.EnqueueRun,
                    Success = true,
                    Message = "Enqueued.",
                    RunId = "local-run-1",
                    ExecutionId = "execution-existing-1",
                    RunHandle = handle,
                    RunState = new AiRuntimePipelineRunState
                    {
                        RunId = "local-run-1",
                        ExecutionId = "execution-existing-1",
                        PipelineKey = "pipeline-1",
                        PipelineName = "pipeline-1",
                        RuntimeInstanceId = "runtime-1",
                        Status = "running",
                        IsQueued = false,
                        IsRunning = true
                    },
                    RuntimeInstanceId = "runtime-1",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                });
            }

            public Task<AiRuntimeQueueControlPlaneResult> GetRunStatusAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);
                cancellationToken.ThrowIfCancellationRequested();

                GetRunStatusCalled = true;

                return Task.FromResult(new AiRuntimeQueueControlPlaneResult
                {
                    Operation = AiRuntimeQueueControlPlaneOperation.GetRunStatus,
                    Success = true,
                    RunId = request.RunId,
                    ExecutionId = "execution-existing-1",
                    RunState = new AiRuntimePipelineRunState
                    {
                        RunId = request.RunId,
                        ExecutionId = "execution-existing-1",
                        PipelineKey = "pipeline-1",
                        PipelineName = "pipeline-1",
                        RuntimeInstanceId = "runtime-1",
                        Status = "running",
                        IsQueued = false,
                        IsRunning = true
                    },
                    RuntimeInstanceId = "runtime-1",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                });
            }

            public Task<AiRuntimeQueueControlPlaneResult> GetQueueStatusAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(new AiRuntimeQueueControlPlaneResult
                {
                    Operation = AiRuntimeQueueControlPlaneOperation.GetQueueStatus,
                    Success = true,
                    QueueState = new AiRuntimePipelineQueueState
                    {
                        RuntimeInstanceId = "runtime-1",
                        CanAcceptRun = true,
                        SnapshotAtUtc = DateTimeOffset.UtcNow
                    },
                    RuntimeInstanceId = "runtime-1",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                });
            }

            public Task<AiRuntimeQueueControlPlaneResult> CancelRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(CreateSuccessfulResult(AiRuntimeQueueControlPlaneOperation.CancelRun));
            }

            public Task<AiRuntimeQueueControlPlaneResult> CancelQueuedRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(CreateSuccessfulResult(AiRuntimeQueueControlPlaneOperation.CancelQueuedRun));
            }

            public Task<AiRuntimeQueueControlPlaneResult> PauseQueueAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(CreateSuccessfulResult(AiRuntimeQueueControlPlaneOperation.PauseQueue));
            }

            public Task<AiRuntimeQueueControlPlaneResult> ResumeQueueAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(CreateSuccessfulResult(AiRuntimeQueueControlPlaneOperation.ResumeQueue));
            }

            private static AiRuntimeQueueControlPlaneResult CreateSuccessfulResult(
                AiRuntimeQueueControlPlaneOperation operation)
            {
                return new AiRuntimeQueueControlPlaneResult
                {
                    Operation = operation,
                    Success = true,
                    RuntimeInstanceId = "runtime-1",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                };
            }
        }
    }
}
