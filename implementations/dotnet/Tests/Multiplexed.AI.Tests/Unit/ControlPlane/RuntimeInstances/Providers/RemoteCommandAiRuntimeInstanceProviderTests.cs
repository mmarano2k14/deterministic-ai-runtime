using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Unit tests for <see cref="RemoteCommandAiRuntimeInstanceProvider"/>.
    /// </summary>
    public sealed class RemoteCommandAiRuntimeInstanceProviderTests
    {
        /// <summary>
        /// Verifies that the provider handles descriptors marked as remote-command.
        /// </summary>
        [Fact]
        public void CanHandle_WithRemoteCommandProviderMetadata_ShouldReturnTrue()
        {
            var provider =
                new RemoteCommandAiRuntimeInstanceProvider(
                    new TestRuntimeInstanceCommandTransport());

            var canHandle =
                provider.CanHandle(
                    CreateDescriptor());

            Assert.True(canHandle);
        }

        /// <summary>
        /// Verifies that the provider rejects non remote-command descriptors.
        /// </summary>
        [Fact]
        public void CanHandle_WithLocalProviderMetadata_ShouldReturnFalse()
        {
            var provider =
                new RemoteCommandAiRuntimeInstanceProvider(
                    new TestRuntimeInstanceCommandTransport());

            var descriptor =
                CreateDescriptor(
                    providerName: "local");

            var canHandle =
                provider.CanHandle(
                    descriptor);

            Assert.False(canHandle);
        }

        /// <summary>
        /// Verifies that dispatch is sent through the command transport.
        /// </summary>
        [Fact]
        public async Task DispatchAsync_ShouldSendDispatchCommandThroughTransport()
        {
            var runtimeInstanceId = "mcp-runtime-remote-1";
            var transport = new TestRuntimeInstanceCommandTransport();

            var expectedDispatchResult =
                CreateDispatchResult(
                    runtimeInstanceId,
                    success: true);

            transport.NextResult =
                new AiRuntimeInstanceCommandResult
                {
                    Success = true,
                    Operation = AiRuntimeInstanceCommandOperation.DispatchRun,
                    RuntimeInstanceId = runtimeInstanceId,
                    DispatchResult = expectedDispatchResult,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    DurationMs = 0
                };

            var provider =
                new RemoteCommandAiRuntimeInstanceProvider(
                    transport);

            var result =
                await provider.DispatchAsync(
                    CreateDescriptor(runtimeInstanceId),
                    CreateDispatchRequest(runtimeInstanceId),
                    CancellationToken.None);

            Assert.True(result.Success);
            Assert.Same(expectedDispatchResult, result);
            Assert.Equal(1, transport.SendCallCount);
            Assert.NotNull(transport.LastRequest);
            Assert.Equal(AiRuntimeInstanceCommandOperation.DispatchRun, transport.LastRequest!.Operation);
            Assert.Equal(runtimeInstanceId, transport.LastRequest.RuntimeInstanceId);
            Assert.NotNull(transport.LastRequest.DispatchRequest);
            Assert.Null(transport.LastRequest.QueueRequest);
        }

        /// <summary>
        /// Verifies that get run status is sent through the command transport.
        /// </summary>
        [Fact]
        public async Task GetRunStatusAsync_ShouldSendGetRunStatusCommandThroughTransport()
        {
            var result =
                await ExecuteQueueCommandTestAsync(
                    AiRuntimeInstanceCommandOperation.GetRunStatus,
                    AiRuntimeQueueControlPlaneOperation.GetRunStatus,
                    (provider, descriptor, request) =>
                        provider.GetRunStatusAsync(
                            descriptor,
                            request,
                            CancellationToken.None));

            Assert.Equal(AiRuntimeQueueControlPlaneOperation.GetRunStatus, result.Operation);
        }

        /// <summary>
        /// Verifies that get queue status is sent through the command transport.
        /// </summary>
        [Fact]
        public async Task GetQueueStatusAsync_ShouldSendGetQueueStatusCommandThroughTransport()
        {
            var result =
                await ExecuteQueueCommandTestAsync(
                    AiRuntimeInstanceCommandOperation.GetQueueStatus,
                    AiRuntimeQueueControlPlaneOperation.GetQueueStatus,
                    (provider, descriptor, request) =>
                        provider.GetQueueStatusAsync(
                            descriptor,
                            request,
                            CancellationToken.None));

            Assert.Equal(AiRuntimeQueueControlPlaneOperation.GetQueueStatus, result.Operation);
        }

        /// <summary>
        /// Verifies that pause queue is sent through the command transport.
        /// </summary>
        [Fact]
        public async Task PauseQueueAsync_ShouldSendPauseQueueCommandThroughTransport()
        {
            var result =
                await ExecuteQueueCommandTestAsync(
                    AiRuntimeInstanceCommandOperation.PauseQueue,
                    AiRuntimeQueueControlPlaneOperation.PauseQueue,
                    (provider, descriptor, request) =>
                        provider.PauseQueueAsync(
                            descriptor,
                            request,
                            CancellationToken.None));

            Assert.Equal(AiRuntimeQueueControlPlaneOperation.PauseQueue, result.Operation);
        }

        /// <summary>
        /// Verifies that resume queue is sent through the command transport.
        /// </summary>
        [Fact]
        public async Task ResumeQueueAsync_ShouldSendResumeQueueCommandThroughTransport()
        {
            var result =
                await ExecuteQueueCommandTestAsync(
                    AiRuntimeInstanceCommandOperation.ResumeQueue,
                    AiRuntimeQueueControlPlaneOperation.ResumeQueue,
                    (provider, descriptor, request) =>
                        provider.ResumeQueueAsync(
                            descriptor,
                            request,
                            CancellationToken.None));

            Assert.Equal(AiRuntimeQueueControlPlaneOperation.ResumeQueue, result.Operation);
        }

        /// <summary>
        /// Verifies that cancel run is sent through the command transport.
        /// </summary>
        [Fact]
        public async Task CancelRunAsync_ShouldSendCancelRunCommandThroughTransport()
        {
            var result =
                await ExecuteQueueCommandTestAsync(
                    AiRuntimeInstanceCommandOperation.CancelRun,
                    AiRuntimeQueueControlPlaneOperation.CancelRun,
                    (provider, descriptor, request) =>
                        provider.CancelRunAsync(
                            descriptor,
                            request,
                            CancellationToken.None));

            Assert.Equal(AiRuntimeQueueControlPlaneOperation.CancelRun, result.Operation);
        }

        /// <summary>
        /// Verifies that cancel queued run is sent through the command transport.
        /// </summary>
        [Fact]
        public async Task CancelQueuedRunAsync_ShouldSendCancelQueuedRunCommandThroughTransport()
        {
            var result =
                await ExecuteQueueCommandTestAsync(
                    AiRuntimeInstanceCommandOperation.CancelQueuedRun,
                    AiRuntimeQueueControlPlaneOperation.CancelQueuedRun,
                    (provider, descriptor, request) =>
                        provider.CancelQueuedRunAsync(
                            descriptor,
                            request,
                            CancellationToken.None));

            Assert.Equal(AiRuntimeQueueControlPlaneOperation.CancelQueuedRun, result.Operation);
        }

        /// <summary>
        /// Verifies that dispatch returns a failure result when the transport does not return a dispatch result.
        /// </summary>
        [Fact]
        public async Task DispatchAsync_WhenTransportReturnsNoDispatchResult_ShouldReturnFailure()
        {
            var runtimeInstanceId = "mcp-runtime-remote-1";
            var transport = new TestRuntimeInstanceCommandTransport
            {
                NextResult = new AiRuntimeInstanceCommandResult
                {
                    Success = false,
                    Operation = AiRuntimeInstanceCommandOperation.DispatchRun,
                    RuntimeInstanceId = runtimeInstanceId,
                    FailureReason = "missing-dispatch-result",
                    Message = "No dispatch result.",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    DurationMs = 0
                }
            };

            var provider =
                new RemoteCommandAiRuntimeInstanceProvider(
                    transport);

            var result =
                await provider.DispatchAsync(
                    CreateDescriptor(runtimeInstanceId),
                    CreateDispatchRequest(runtimeInstanceId),
                    CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("missing-dispatch-result", result.FailureReason);
            Assert.Equal(runtimeInstanceId, result.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that queue operations return a failure result when the transport does not return a queue result.
        /// </summary>
        [Fact]
        public async Task GetRunStatusAsync_WhenTransportReturnsNoQueueResult_ShouldReturnFailure()
        {
            var runtimeInstanceId = "mcp-runtime-remote-1";
            var transport = new TestRuntimeInstanceCommandTransport
            {
                NextResult = new AiRuntimeInstanceCommandResult
                {
                    Success = false,
                    Operation = AiRuntimeInstanceCommandOperation.GetRunStatus,
                    RuntimeInstanceId = runtimeInstanceId,
                    FailureReason = "missing-queue-result",
                    Message = "No queue result.",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    DurationMs = 0
                }
            };

            var provider =
                new RemoteCommandAiRuntimeInstanceProvider(
                    transport);

            var result =
                await provider.GetRunStatusAsync(
                    CreateDescriptor(runtimeInstanceId),
                    CreateQueueRequest(
                        runtimeInstanceId,
                        AiRuntimeQueueControlPlaneOperation.GetRunStatus),
                    CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("missing-queue-result", result.FailureReason);
            Assert.Equal(runtimeInstanceId, result.RuntimeInstanceId);
        }

        /// <summary>
        /// Executes one queue command provider test.
        /// </summary>
        private static async Task<AiRuntimeQueueControlPlaneResult> ExecuteQueueCommandTestAsync(
            AiRuntimeInstanceCommandOperation expectedCommandOperation,
            AiRuntimeQueueControlPlaneOperation queueOperation,
            Func<
                RemoteCommandAiRuntimeInstanceProvider,
                AiRuntimeInstanceCapacityDescriptor,
                AiRuntimeQueueControlPlaneRequest,
                Task<AiRuntimeQueueControlPlaneResult>> action)
        {
            var runtimeInstanceId = "mcp-runtime-remote-1";
            var transport = new TestRuntimeInstanceCommandTransport();

            var expectedQueueResult =
                CreateQueueResult(
                    runtimeInstanceId,
                    queueOperation,
                    success: true);

            transport.NextResult =
                new AiRuntimeInstanceCommandResult
                {
                    Success = true,
                    Operation = expectedCommandOperation,
                    RuntimeInstanceId = runtimeInstanceId,
                    QueueResult = expectedQueueResult,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    DurationMs = 0
                };

            var provider =
                new RemoteCommandAiRuntimeInstanceProvider(
                    transport);

            var descriptor =
                CreateDescriptor(runtimeInstanceId);

            var request =
                CreateQueueRequest(
                    runtimeInstanceId,
                    queueOperation);

            var result =
                await action(
                    provider,
                    descriptor,
                    request);

            Assert.True(result.Success);
            Assert.Same(expectedQueueResult, result);
            Assert.Equal(1, transport.SendCallCount);
            Assert.NotNull(transport.LastRequest);
            Assert.Equal(expectedCommandOperation, transport.LastRequest!.Operation);
            Assert.Equal(runtimeInstanceId, transport.LastRequest.RuntimeInstanceId);
            Assert.Null(transport.LastRequest.DispatchRequest);
            Assert.NotNull(transport.LastRequest.QueueRequest);

            return result;
        }

        /// <summary>
        /// Creates a runtime instance capacity descriptor.
        /// </summary>
        private static AiRuntimeInstanceCapacityDescriptor CreateDescriptor(
            string runtimeInstanceId = "mcp-runtime-remote-1",
            string providerName = "remote-command")
        {
            return new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = runtimeInstanceId,
                Metadata = new Dictionary<string, string>
                {
                    [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = providerName
                }
            };
        }

        /// <summary>
        /// Creates a shared runtime instance dispatch request.
        /// </summary>
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
        /// Creates a dispatch result.
        /// </summary>
        private static AiSharedRuntimeInstanceDispatchResult CreateDispatchResult(
            string runtimeInstanceId,
            bool success)
        {
            var now =
                DateTimeOffset.UtcNow;

            return new AiSharedRuntimeInstanceDispatchResult
            {
                Success = success,
                RuntimeInstanceId = runtimeInstanceId,
                SharedRunId = "shared-run-1",
                LocalRunId = "local-run-1",
                ExecutionId = "execution-1",
                ClaimToken = "claim-1",
                StartedAtUtc = now,
                CompletedAtUtc = now,
                DurationMs = 0
            };
        }

        /// <summary>
        /// Creates a queue control-plane result.
        /// </summary>
        private static AiRuntimeQueueControlPlaneResult CreateQueueResult(
            string runtimeInstanceId,
            AiRuntimeQueueControlPlaneOperation operation,
            bool success)
        {
            var now =
                DateTimeOffset.UtcNow;

            return new AiRuntimeQueueControlPlaneResult
            {
                Operation = operation,
                Success = success,
                Message = "Test operation completed.",
                RunId = "local-run-1",
                CorrelationId = "correlation-1",
                RuntimeInstanceId = runtimeInstanceId,
                RequestedBy = "unit-test",
                StartedAtUtc = now,
                CompletedAtUtc = now,
                DurationMs = 0
            };
        }

        /// <summary>
        /// Test runtime instance command transport.
        /// </summary>
        private sealed class TestRuntimeInstanceCommandTransport : IAiRuntimeInstanceCommandTransport
        {
            /// <summary>
            /// Gets or sets the next result returned by the transport.
            /// </summary>
            public AiRuntimeInstanceCommandResult? NextResult { get; set; }

            /// <summary>
            /// Gets the last request sent to the transport.
            /// </summary>
            public AiRuntimeInstanceCommandRequest? LastRequest { get; private set; }

            /// <summary>
            /// Gets the number of transport send calls.
            /// </summary>
            public int SendCallCount { get; private set; }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceCommandResult> SendAsync(
                AiRuntimeInstanceCommandRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);

                SendCallCount++;
                LastRequest = request;

                if (NextResult is not null)
                {
                    return Task.FromResult(NextResult);
                }

                var now =
                    DateTimeOffset.UtcNow;

                return Task.FromResult(
                    new AiRuntimeInstanceCommandResult
                    {
                        Success = false,
                        Operation = request.Operation,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        FailureReason = "test-result-not-configured",
                        Message = "Test transport result was not configured.",
                        StartedAtUtc = now,
                        CompletedAtUtc = now,
                        DurationMs = 0
                    });
            }
        }
    }
}