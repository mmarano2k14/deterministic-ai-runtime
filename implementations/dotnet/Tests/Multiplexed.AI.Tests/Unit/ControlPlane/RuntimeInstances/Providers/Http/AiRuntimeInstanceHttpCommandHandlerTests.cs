using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Http;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Providers.Http
{
    /// <summary>
    /// Unit tests for <see cref="AiRuntimeInstanceHttpCommandHandler"/>.
    /// </summary>
    public sealed class AiRuntimeInstanceHttpCommandHandlerTests
    {
        /// <summary>
        /// Verifies that dispatch commands are routed to the local shared runtime instance.
        /// </summary>
        [Fact]
        public async Task HandleAsync_WithDispatchRun_ShouldRouteToSharedRuntimeInstance()
        {
            var runtimeInstanceId = "runtime-http-1";
            var sharedRuntimeInstance = new TestSharedRuntimeInstance(runtimeInstanceId);
            var queueControlPlane = new TestRuntimeQueueControlPlane();

            var handler =
                new AiRuntimeInstanceHttpCommandHandler(
                    sharedRuntimeInstance,
                    queueControlPlane);

            var result =
                await handler.HandleAsync(
                    CreateDispatchCommandRequest(runtimeInstanceId),
                    CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(AiRuntimeInstanceCommandOperation.DispatchRun, result.Operation);
            Assert.Equal(runtimeInstanceId, result.RuntimeInstanceId);
            Assert.NotNull(result.DispatchResult);
            Assert.Null(result.QueueResult);
            Assert.Equal(1, sharedRuntimeInstance.DispatchCallCount);
        }

        /// <summary>
        /// Verifies that get run status commands are routed to the local queue control-plane.
        /// </summary>
        [Fact]
        public async Task HandleAsync_WithGetRunStatus_ShouldRouteToQueueControlPlane()
        {
            var result =
                await ExecuteQueueCommandTestAsync(
                    AiRuntimeInstanceCommandOperation.GetRunStatus,
                    AiRuntimeQueueControlPlaneOperation.GetRunStatus);

            Assert.Equal(AiRuntimeQueueControlPlaneOperation.GetRunStatus, result.QueueResult!.Operation);
        }

        /// <summary>
        /// Verifies that get queue status commands are routed to the local queue control-plane.
        /// </summary>
        [Fact]
        public async Task HandleAsync_WithGetQueueStatus_ShouldRouteToQueueControlPlane()
        {
            var result =
                await ExecuteQueueCommandTestAsync(
                    AiRuntimeInstanceCommandOperation.GetQueueStatus,
                    AiRuntimeQueueControlPlaneOperation.GetQueueStatus);

            Assert.Equal(AiRuntimeQueueControlPlaneOperation.GetQueueStatus, result.QueueResult!.Operation);
        }

        /// <summary>
        /// Verifies that pause queue commands are routed to the local queue control-plane.
        /// </summary>
        [Fact]
        public async Task HandleAsync_WithPauseQueue_ShouldRouteToQueueControlPlane()
        {
            var result =
                await ExecuteQueueCommandTestAsync(
                    AiRuntimeInstanceCommandOperation.PauseQueue,
                    AiRuntimeQueueControlPlaneOperation.PauseQueue);

            Assert.Equal(AiRuntimeQueueControlPlaneOperation.PauseQueue, result.QueueResult!.Operation);
        }

        /// <summary>
        /// Verifies that resume queue commands are routed to the local queue control-plane.
        /// </summary>
        [Fact]
        public async Task HandleAsync_WithResumeQueue_ShouldRouteToQueueControlPlane()
        {
            var result =
                await ExecuteQueueCommandTestAsync(
                    AiRuntimeInstanceCommandOperation.ResumeQueue,
                    AiRuntimeQueueControlPlaneOperation.ResumeQueue);

            Assert.Equal(AiRuntimeQueueControlPlaneOperation.ResumeQueue, result.QueueResult!.Operation);
        }

        /// <summary>
        /// Verifies that cancel run commands are routed to the local queue control-plane.
        /// </summary>
        [Fact]
        public async Task HandleAsync_WithCancelRun_ShouldRouteToQueueControlPlane()
        {
            var result =
                await ExecuteQueueCommandTestAsync(
                    AiRuntimeInstanceCommandOperation.CancelRun,
                    AiRuntimeQueueControlPlaneOperation.CancelRun);

            Assert.Equal(AiRuntimeQueueControlPlaneOperation.CancelRun, result.QueueResult!.Operation);
        }

        /// <summary>
        /// Verifies that cancel queued run commands are routed to the local queue control-plane.
        /// </summary>
        [Fact]
        public async Task HandleAsync_WithCancelQueuedRun_ShouldRouteToQueueControlPlane()
        {
            var result =
                await ExecuteQueueCommandTestAsync(
                    AiRuntimeInstanceCommandOperation.CancelQueuedRun,
                    AiRuntimeQueueControlPlaneOperation.CancelQueuedRun);

            Assert.Equal(AiRuntimeQueueControlPlaneOperation.CancelQueuedRun, result.QueueResult!.Operation);
        }

        /// <summary>
        /// Verifies that dispatch commands fail cleanly when the dispatch request is missing.
        /// </summary>
        [Fact]
        public async Task HandleAsync_WithDispatchRunMissingDispatchRequest_ShouldReturnFailure()
        {
            var handler =
                new AiRuntimeInstanceHttpCommandHandler(
                    new TestSharedRuntimeInstance("runtime-http-1"),
                    new TestRuntimeQueueControlPlane());

            var result =
                await handler.HandleAsync(
                    new AiRuntimeInstanceCommandRequest
                    {
                        Operation = AiRuntimeInstanceCommandOperation.DispatchRun,
                        RuntimeInstanceId = "runtime-http-1"
                    },
                    CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("dispatch-request-missing", result.FailureReason);
            Assert.Null(result.DispatchResult);
            Assert.Null(result.QueueResult);
        }

        /// <summary>
        /// Verifies that queue commands fail cleanly when the queue request is missing.
        /// </summary>
        [Fact]
        public async Task HandleAsync_WithQueueCommandMissingQueueRequest_ShouldReturnFailure()
        {
            var handler =
                new AiRuntimeInstanceHttpCommandHandler(
                    new TestSharedRuntimeInstance("runtime-http-1"),
                    new TestRuntimeQueueControlPlane());

            var result =
                await handler.HandleAsync(
                    new AiRuntimeInstanceCommandRequest
                    {
                        Operation = AiRuntimeInstanceCommandOperation.GetRunStatus,
                        RuntimeInstanceId = "runtime-http-1"
                    },
                    CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("queue-request-missing", result.FailureReason);
            Assert.Null(result.DispatchResult);
            Assert.Null(result.QueueResult);
        }

        /// <summary>
        /// Verifies that unsupported command operations return a failure result.
        /// </summary>
        [Fact]
        public async Task HandleAsync_WithUnsupportedOperation_ShouldReturnFailure()
        {
            var handler =
                new AiRuntimeInstanceHttpCommandHandler(
                    new TestSharedRuntimeInstance("runtime-http-1"),
                    new TestRuntimeQueueControlPlane());

            var result =
                await handler.HandleAsync(
                    new AiRuntimeInstanceCommandRequest
                    {
                        Operation = AiRuntimeInstanceCommandOperation.Unknown,
                        RuntimeInstanceId = "runtime-http-1"
                    },
                    CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("unsupported-command-operation", result.FailureReason);
        }

        /// <summary>
        /// Verifies that metadata is preserved on queue command results.
        /// </summary>
        [Fact]
        public async Task HandleAsync_WithQueueCommand_ShouldPreserveCommandMetadata()
        {
            var handler =
                new AiRuntimeInstanceHttpCommandHandler(
                    new TestSharedRuntimeInstance("runtime-http-1"),
                    new TestRuntimeQueueControlPlane());

            var result =
                await handler.HandleAsync(
                    new AiRuntimeInstanceCommandRequest
                    {
                        Operation = AiRuntimeInstanceCommandOperation.GetQueueStatus,
                        RuntimeInstanceId = "runtime-http-1",
                        QueueRequest = CreateQueueRequest(
                            "runtime-http-1",
                            AiRuntimeQueueControlPlaneOperation.GetQueueStatus),
                        Metadata = new Dictionary<string, string>
                        {
                            ["test.key"] = "test-value"
                        }
                    },
                    CancellationToken.None);

            Assert.True(result.Success);
            Assert.True(result.Metadata.ContainsKey("test.key"));
            Assert.Equal("test-value", result.Metadata["test.key"]);
        }

        /// <summary>
        /// Executes one queue command handler test.
        /// </summary>
        /// <param name="commandOperation">The command operation.</param>
        /// <param name="queueOperation">The expected queue operation.</param>
        /// <returns>The command result.</returns>
        private static async Task<AiRuntimeInstanceCommandResult> ExecuteQueueCommandTestAsync(
            AiRuntimeInstanceCommandOperation commandOperation,
            AiRuntimeQueueControlPlaneOperation queueOperation)
        {
            var runtimeInstanceId = "runtime-http-1";
            var sharedRuntimeInstance = new TestSharedRuntimeInstance(runtimeInstanceId);
            var queueControlPlane = new TestRuntimeQueueControlPlane();

            var handler =
                new AiRuntimeInstanceHttpCommandHandler(
                    sharedRuntimeInstance,
                    queueControlPlane);

            var result =
                await handler.HandleAsync(
                    new AiRuntimeInstanceCommandRequest
                    {
                        Operation = commandOperation,
                        RuntimeInstanceId = runtimeInstanceId,
                        QueueRequest = CreateQueueRequest(
                            runtimeInstanceId,
                            queueOperation),
                        Metadata = new Dictionary<string, string>
                        {
                            ["command.source"] = "unit-test"
                        }
                    },
                    CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(commandOperation, result.Operation);
            Assert.Equal(runtimeInstanceId, result.RuntimeInstanceId);
            Assert.Null(result.DispatchResult);
            Assert.NotNull(result.QueueResult);
            Assert.Equal(1, queueControlPlane.TotalCallCount);
            Assert.Equal(0, sharedRuntimeInstance.DispatchCallCount);

            return result;
        }

        /// <summary>
        /// Creates a dispatch command request.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <returns>The command request.</returns>
        private static AiRuntimeInstanceCommandRequest CreateDispatchCommandRequest(
            string runtimeInstanceId)
        {
            return new AiRuntimeInstanceCommandRequest
            {
                Operation = AiRuntimeInstanceCommandOperation.DispatchRun,
                RuntimeInstanceId = runtimeInstanceId,
                DispatchRequest = CreateDispatchRequest(runtimeInstanceId),
                Metadata = new Dictionary<string, string>
                {
                    ["command.source"] = "unit-test"
                }
            };
        }

        /// <summary>
        /// Creates a shared runtime instance dispatch request.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <returns>The dispatch request.</returns>
        private static AiSharedRuntimeInstanceDispatchRequest CreateDispatchRequest(
            string runtimeInstanceId)
        {
            var runRequest =
                new AiRuntimePipelineRunRequest
                {
                    PipelineName = "test-pipeline"
                };

            return new AiSharedRuntimeInstanceDispatchRequest
            {
                RuntimeInstanceId = runtimeInstanceId,
                SharedRun = new AiSharedRunRecord
                {
                    SharedRunId = "shared-run-1",
                    Status = AiSharedRunStatus.Submitted,
                    RunRequest = runRequest,
                    PipelineKey = "test-pipeline",
                    SubmittedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                },
                RunRequest = runRequest,
                ClaimToken = "claim-1",
                CorrelationId = "correlation-1",
                RequestedBy = "unit-test",
                Source = "unit-test",
                Reason = "unit-test"
            };
        }

        /// <summary>
        /// Creates a runtime queue control-plane request.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="operation">The queue operation.</param>
        /// <returns>The queue request.</returns>
        private static AiRuntimeQueueControlPlaneRequest CreateQueueRequest(
            string runtimeInstanceId,
            AiRuntimeQueueControlPlaneOperation operation)
        {
            return new AiRuntimeQueueControlPlaneRequest
            {
                Operation = operation,
                RuntimeInstanceId = runtimeInstanceId,
                RunId = "local-run-1",
                CorrelationId = "correlation-1",
                RequestedBy = "unit-test",
                Source = "unit-test",
                Reason = "unit-test"
            };
        }

        /// <summary>
        /// Test shared runtime instance.
        /// </summary>
        private sealed class TestSharedRuntimeInstance : IAiSharedRuntimeInstance
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="TestSharedRuntimeInstance"/> class.
            /// </summary>
            /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
            public TestSharedRuntimeInstance(
                string runtimeInstanceId)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

                RuntimeInstanceId = runtimeInstanceId;
                QueueControlPlane = new TestRuntimeQueueControlPlane();
            }

            /// <inheritdoc />
            public string RuntimeInstanceId { get; }

            /// <inheritdoc />
            public IAiRuntimeQueueControlPlane QueueControlPlane { get; }

            /// <summary>
            /// Gets the number of dispatch calls.
            /// </summary>
            public int DispatchCallCount { get; private set; }

            /// <inheritdoc />
            public Task<AiSharedRuntimeInstanceDispatchResult> DispatchAsync(
                AiSharedRuntimeInstanceDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);

                DispatchCallCount++;

                var now =
                    DateTimeOffset.UtcNow;

                return Task.FromResult(
                    new AiSharedRuntimeInstanceDispatchResult
                    {
                        Success = true,
                        RuntimeInstanceId = RuntimeInstanceId,
                        SharedRunId = request.SharedRun.SharedRunId,
                        LocalRunId = "local-run-1",
                        ExecutionId = "execution-1",
                        ClaimToken = request.ClaimToken,
                        Message = "Dispatch completed.",
                        StartedAtUtc = now,
                        CompletedAtUtc = now,
                        DurationMs = 0,
                        Metadata = new Dictionary<string, string>
                        {
                            ["dispatch.test"] = "true"
                        }
                    });
            }
        }

        /// <summary>
        /// Test runtime queue control-plane.
        /// </summary>
        private sealed class TestRuntimeQueueControlPlane : IAiRuntimeQueueControlPlane
        {
            /// <summary>
            /// Gets the total number of queue operation calls.
            /// </summary>
            public int TotalCallCount { get; private set; }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> ExecuteAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);

                return Task.FromResult(
                    CreateResult(request));
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> EnqueueRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                TotalCallCount++;

                return Task.FromResult(
                    CreateResult(request));
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> CancelRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                TotalCallCount++;

                return Task.FromResult(
                    CreateResult(request));
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> CancelQueuedRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                TotalCallCount++;

                return Task.FromResult(
                    CreateResult(request));
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> PauseQueueAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                TotalCallCount++;

                return Task.FromResult(
                    CreateResult(request));
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> ResumeQueueAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                TotalCallCount++;

                return Task.FromResult(
                    CreateResult(request));
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> GetRunStatusAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                TotalCallCount++;

                return Task.FromResult(
                    CreateResult(request));
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> GetQueueStatusAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                TotalCallCount++;

                return Task.FromResult(
                    CreateResult(request));
            }

            /// <summary>
            /// Creates a queue control-plane result.
            /// </summary>
            /// <param name="request">The queue request.</param>
            /// <returns>The queue result.</returns>
            private static AiRuntimeQueueControlPlaneResult CreateResult(
                AiRuntimeQueueControlPlaneRequest request)
            {
                var now =
                    DateTimeOffset.UtcNow;

                return new AiRuntimeQueueControlPlaneResult
                {
                    Operation = request.Operation,
                    Success = true,
                    Message = "Queue operation completed.",
                    RunId = request.RunId,
                    CorrelationId = request.CorrelationId,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    RequestedBy = request.RequestedBy,
                    StartedAtUtc = now,
                    CompletedAtUtc = now,
                    DurationMs = 0
                };
            }
        }
    }
}