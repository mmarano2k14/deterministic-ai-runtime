using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.SharedController;
using Multiplexed.AI.Tests.Fixtures;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedController
{
    public sealed class LocalAiSharedRunDispatcherTests
    {
        [Fact]
        public async Task DispatchAsync_Should_Enqueue_Run_To_Runtime_Queue()
        {
            var runtimeQueue = new FakeRuntimeQueueControlPlane(
                CreateQueueResult(
                    success: true,
                    runHandle: new AiRuntimeWorkerRunHandle(
                        "local-run-1",
                        Task.FromResult(new AiExecutionRecord
                        {
                            ExecutionId = "execution-1"
                        }))));

            var dispatcher = new LocalAiSharedRunDispatcher(runtimeQueue);

            var result = await dispatcher.DispatchAsync(new AiSharedRunDispatchRequest
            {
                SharedRun = CreateSharedRun("shared-run-1"),
                RuntimeInstanceId = "runtime-1",
                ClaimToken = "claim-token-1",
                CorrelationId = "correlation-1",
                RequestedBy = "tester",
                Source = "unit-test",
                Reason = "dispatch test"
            });

            Assert.True(result.Success);
            Assert.Equal("shared-run-1", result.SharedRunId);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);
            Assert.Equal("local-run-1", result.LocalRunId);
            Assert.Equal("claim-token-1", result.ClaimToken);

            Assert.NotNull(runtimeQueue.LastRequest);
            Assert.Equal(AiRuntimeQueueControlPlaneOperation.EnqueueRun, runtimeQueue.LastRequest!.Operation);
            Assert.Equal("pipeline-1", runtimeQueue.LastRequest.RunRequest?.PipelineName);
            Assert.Equal("correlation-1", runtimeQueue.LastRequest.CorrelationId);
            Assert.Equal("tester", runtimeQueue.LastRequest.RequestedBy);
            Assert.Equal("unit-test", runtimeQueue.LastRequest.Source);
            Assert.Equal("dispatch test", runtimeQueue.LastRequest.Reason);
        }

        [Fact]
        public async Task DispatchAsync_Should_Return_Failure_When_Runtime_Queue_Fails()
        {
            var runtimeQueue = new FakeRuntimeQueueControlPlane(
                CreateQueueResult(
                    success: false,
                    message: "Queue rejected.",
                    failureReason: "Queue full."));

            var dispatcher = new LocalAiSharedRunDispatcher(runtimeQueue);

            var result = await dispatcher.DispatchAsync(new AiSharedRunDispatchRequest
            {
                SharedRun = CreateSharedRun("shared-run-1"),
                RuntimeInstanceId = "runtime-1"
            });

            Assert.False(result.Success);
            Assert.Equal("shared-run-1", result.SharedRunId);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);
            Assert.Equal("Queue full.", result.FailureReason);
        }

        [Fact]
        public async Task DispatchAsync_Should_Return_Failure_When_Runtime_Queue_Throws()
        {
            var runtimeQueue = new ThrowingRuntimeQueueControlPlane(
                new InvalidOperationException("Runtime queue unavailable."));

            var dispatcher = new LocalAiSharedRunDispatcher(runtimeQueue);

            var result = await dispatcher.DispatchAsync(new AiSharedRunDispatchRequest
            {
                SharedRun = CreateSharedRun("shared-run-1"),
                RuntimeInstanceId = "runtime-1"
            });

            Assert.False(result.Success);
            Assert.Equal("shared-run-1", result.SharedRunId);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);
            Assert.Equal("Runtime queue unavailable.", result.FailureReason);
            Assert.Contains("Runtime queue unavailable.", result.Diagnostics);
        }

        [Fact]
        public async Task DispatchAsync_Should_Merge_Metadata()
        {
            var runtimeQueue = new FakeRuntimeQueueControlPlane(
                CreateQueueResult(
                    success: true,
                    runHandle: new AiRuntimeWorkerRunHandle(
                        "local-run-1",
                        Task.FromResult(new AiExecutionRecord
                        {
                            ExecutionId = "execution-1"
                        }))));

            var dispatcher = new LocalAiSharedRunDispatcher(runtimeQueue);

            await dispatcher.DispatchAsync(new AiSharedRunDispatchRequest
            {
                SharedRun = CreateSharedRun(
                    "shared-run-1",
                    metadata: new Dictionary<string, string>
                    {
                        ["tenant"] = "tenant-1",
                        ["priority"] = "normal"
                    }),
                RuntimeInstanceId = "runtime-1",
                Metadata = new Dictionary<string, string>
                {
                    ["priority"] = "high",
                    ["source"] = "dispatcher-test"
                }
            });

            Assert.NotNull(runtimeQueue.LastRequest);
            Assert.Equal("tenant-1", runtimeQueue.LastRequest!.Metadata["tenant"]);
            Assert.Equal("high", runtimeQueue.LastRequest.Metadata["priority"]);
            Assert.Equal("dispatcher-test", runtimeQueue.LastRequest.Metadata["source"]);
        }

        [Fact]
        public async Task DispatchAsync_Should_Use_SharedRun_Correlation_When_Request_Correlation_Is_Missing()
        {
            var runtimeQueue = new FakeRuntimeQueueControlPlane(
                CreateQueueResult(
                    success: true,
                    runHandle: new AiRuntimeWorkerRunHandle(
                        "local-run-1",
                        Task.FromResult(new AiExecutionRecord
                        {
                            ExecutionId = "execution-1"
                        }))));

            var dispatcher = new LocalAiSharedRunDispatcher(runtimeQueue);

            await dispatcher.DispatchAsync(new AiSharedRunDispatchRequest
            {
                SharedRun = CreateSharedRun(
                    "shared-run-1",
                    correlationId: "shared-correlation-1"),
                RuntimeInstanceId = "runtime-1"
            });

            Assert.NotNull(runtimeQueue.LastRequest);
            Assert.Equal("shared-correlation-1", runtimeQueue.LastRequest!.CorrelationId);
        }

        [Fact]
        public async Task DispatchAsync_Should_Throw_When_Request_Is_Null()
        {
            var dispatcher = new LocalAiSharedRunDispatcher(
                new FakeRuntimeQueueControlPlane(
                    CreateQueueResult(success: true)));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                dispatcher.DispatchAsync(null!));
        }

        [Fact]
        public async Task DispatchAsync_Should_Throw_When_SharedRunId_Is_Missing()
        {
            var dispatcher = new LocalAiSharedRunDispatcher(
                new FakeRuntimeQueueControlPlane(
                    CreateQueueResult(success: true)));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                dispatcher.DispatchAsync(new AiSharedRunDispatchRequest
                {
                    SharedRun = CreateSharedRun(" "),
                    RuntimeInstanceId = "runtime-1"
                }));
        }

        [Fact]
        public async Task DispatchAsync_Should_Throw_When_RuntimeInstanceId_Is_Missing()
        {
            var dispatcher = new LocalAiSharedRunDispatcher(
                new FakeRuntimeQueueControlPlane(
                    CreateQueueResult(success: true)));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                dispatcher.DispatchAsync(new AiSharedRunDispatchRequest
                {
                    SharedRun = CreateSharedRun("shared-run-1"),
                    RuntimeInstanceId = " "
                }));
        }

        private static AiRuntimeQueueControlPlaneResult CreateQueueResult(
            bool success,
            AiRuntimeWorkerRunHandle? runHandle = null,
            string? message = null,
            string? failureReason = null)
        {
            return new AiRuntimeQueueControlPlaneResult
            {
                Operation = AiRuntimeQueueControlPlaneOperation.EnqueueRun,
                Success = success,
                RunHandle = runHandle,
                Message = message,
                FailureReason = failureReason
            };
        }

        private static AiSharedRunRecord CreateSharedRun(
            string sharedRunId,
            string? correlationId = null,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            var now = DateTimeOffset.UtcNow;

            return new AiSharedRunRecord
            {
                SharedRunId = sharedRunId,
                Status = AiSharedRunStatus.AssignedToInstance,
                RunRequest = new AiRuntimePipelineRunRequest
                {
                    PipelineName = "pipeline-1"
                },
                ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create(tenantId: "tenant-1"),
                CorrelationId = correlationId,
                SubmittedAtUtc = now,
                UpdatedAtUtc = now,
                Metadata = metadata ?? new Dictionary<string, string>()
            };
        }

        private sealed class FakeRuntimeQueueControlPlane : IAiRuntimeQueueControlPlane
        {
            private readonly AiRuntimeQueueControlPlaneResult _result;

            public FakeRuntimeQueueControlPlane(
                AiRuntimeQueueControlPlaneResult result)
            {
                _result = result;
            }

            public AiRuntimeQueueControlPlaneRequest? LastRequest { get; private set; }

            public Task<AiRuntimeQueueControlPlaneResult> ExecuteAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;

                return Task.FromResult(_result);
            }

            public Task<AiRuntimeQueueControlPlaneResult> EnqueueRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;

                return Task.FromResult(_result);
            }

            public Task<AiRuntimeQueueControlPlaneResult> CancelRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;

                return Task.FromResult(_result);
            }

            public Task<AiRuntimeQueueControlPlaneResult> CancelQueuedRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;

                return Task.FromResult(_result);
            }

            public Task<AiRuntimeQueueControlPlaneResult> PauseQueueAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;

                return Task.FromResult(_result);
            }

            public Task<AiRuntimeQueueControlPlaneResult> ResumeQueueAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;

                return Task.FromResult(_result);
            }

            public Task<AiRuntimeQueueControlPlaneResult> GetRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;

                return Task.FromResult(_result);
            }

            public Task<AiRuntimeQueueControlPlaneResult> GetQueueAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;

                return Task.FromResult(_result);
            }

            public Task<AiRuntimeQueueControlPlaneResult> GetRunStatusAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;

                return Task.FromResult(_result);
            }

            public Task<AiRuntimeQueueControlPlaneResult> GetQueueStatusAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;

                return Task.FromResult(_result);
            }
        }

        private sealed class ThrowingRuntimeQueueControlPlane : IAiRuntimeQueueControlPlane
        {
            private readonly Exception _exception;

            public ThrowingRuntimeQueueControlPlane(
                Exception exception)
            {
                _exception = exception;
            }

            public Task<AiRuntimeQueueControlPlaneResult> ExecuteAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                throw _exception;
            }

            public Task<AiRuntimeQueueControlPlaneResult> EnqueueRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                throw _exception;
            }

            public Task<AiRuntimeQueueControlPlaneResult> CancelRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                throw _exception;
            }

            public Task<AiRuntimeQueueControlPlaneResult> CancelQueuedRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                throw _exception;
            }

            public Task<AiRuntimeQueueControlPlaneResult> PauseQueueAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                throw _exception;
            }

            public Task<AiRuntimeQueueControlPlaneResult> ResumeQueueAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                throw _exception;
            }

            public Task<AiRuntimeQueueControlPlaneResult> GetRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                throw _exception;
            }

            public Task<AiRuntimeQueueControlPlaneResult> GetQueueAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                throw _exception;
            }

            public Task<AiRuntimeQueueControlPlaneResult> GetRunStatusAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                throw _exception;
            }

            public Task<AiRuntimeQueueControlPlaneResult> GetQueueStatusAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                throw _exception;
            }
        }
    }
}