using Microsoft.Extensions.Logging.Abstractions;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http;
using System.Net;
using System.Net.Http.Json;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Unit tests for <see cref="HttpAiRuntimeInstanceProvider"/>.
    /// </summary>
    public sealed class HttpAiRuntimeInstanceProviderTests
    {
        /// <summary>
        /// Verifies that the HTTP provider handles descriptors marked with provider.name=http.
        /// </summary>
        [Fact]
        public void CanHandle_WithHttpProviderMetadata_ShouldReturnTrue()
        {
            var provider =
                CreateProvider(new TestHttpMessageHandler());

            var canHandle =
                provider.CanHandle(
                    CreateDescriptor());

            Assert.True(canHandle);
        }

        /// <summary>
        /// Verifies that the HTTP provider rejects non-HTTP provider descriptors.
        /// </summary>
        [Fact]
        public void CanHandle_WithLocalProviderMetadata_ShouldReturnFalse()
        {
            var provider =
                CreateProvider(new TestHttpMessageHandler());

            var descriptor =
                CreateDescriptor(
                    providerName: "local");

            var canHandle =
                provider.CanHandle(
                    descriptor);

            Assert.False(canHandle);
        }

        /// <summary>
        /// Verifies that dispatch sends an HTTP command request to the runtime command endpoint.
        /// </summary>
        [Fact]
        public async Task DispatchAsync_ShouldPostDispatchCommandToRuntimeEndpoint()
        {
            var runtimeInstanceId = "runtime-http-1";
            var expectedDispatchResult =
                CreateDispatchResult(runtimeInstanceId, success: true);

            var handler =
                new TestHttpMessageHandler
                {
                    Response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(
                            new AiRuntimeInstanceCommandResult
                            {
                                Success = true,
                                Operation = AiRuntimeInstanceCommandOperation.DispatchRun,
                                RuntimeInstanceId = runtimeInstanceId,
                                DispatchResult = expectedDispatchResult,
                                StartedAtUtc = DateTimeOffset.UtcNow,
                                CompletedAtUtc = DateTimeOffset.UtcNow,
                                DurationMs = 0
                            })
                    }
                };

            var provider =
                CreateProvider(handler);

            var result =
                await provider.DispatchAsync(
                    CreateDescriptor(runtimeInstanceId),
                    CreateDispatchRequest(runtimeInstanceId),
                    CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(runtimeInstanceId, result.RuntimeInstanceId);
            Assert.Equal(1, handler.SendCallCount);
            Assert.NotNull(handler.LastRequest);
            Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
            Assert.Equal(
                "http://runtime-http-1:8080/runtime-instance/commands",
                handler.LastRequest.RequestUri!.ToString());
        }

        /// <summary>
        /// Verifies that get run status sends an HTTP command request.
        /// </summary>
        [Fact]
        public async Task GetRunStatusAsync_ShouldPostGetRunStatusCommandToRuntimeEndpoint()
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
        /// Verifies that get queue status sends an HTTP command request.
        /// </summary>
        [Fact]
        public async Task GetQueueStatusAsync_ShouldPostGetQueueStatusCommandToRuntimeEndpoint()
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
        /// Verifies that pause queue sends an HTTP command request.
        /// </summary>
        [Fact]
        public async Task PauseQueueAsync_ShouldPostPauseQueueCommandToRuntimeEndpoint()
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
        /// Verifies that resume queue sends an HTTP command request.
        /// </summary>
        [Fact]
        public async Task ResumeQueueAsync_ShouldPostResumeQueueCommandToRuntimeEndpoint()
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
        /// Verifies that cancel run sends an HTTP command request.
        /// </summary>
        [Fact]
        public async Task CancelRunAsync_ShouldPostCancelRunCommandToRuntimeEndpoint()
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
        /// Verifies that cancel queued run sends an HTTP command request.
        /// </summary>
        [Fact]
        public async Task CancelQueuedRunAsync_ShouldPostCancelQueuedRunCommandToRuntimeEndpoint()
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
        /// Verifies that dispatch returns a failure result when the HTTP endpoint metadata is missing.
        /// </summary>
        [Fact]
        public async Task DispatchAsync_WithMissingEndpoint_ShouldReturnFailure()
        {
            var runtimeInstanceId = "runtime-http-1";

            var provider =
                CreateProvider(new TestHttpMessageHandler());

            var descriptor =
                CreateDescriptor(
                    runtimeInstanceId,
                    includeEndpoint: false);

            var result =
                await provider.DispatchAsync(
                    descriptor,
                    CreateDispatchRequest(runtimeInstanceId),
                    CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("http-endpoint-missing", result.FailureReason);
            Assert.Equal(runtimeInstanceId, result.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that queue operations return a failure result when the HTTP endpoint is invalid.
        /// </summary>
        [Fact]
        public async Task GetRunStatusAsync_WithInvalidEndpoint_ShouldReturnFailure()
        {
            var runtimeInstanceId = "runtime-http-1";

            var provider =
                CreateProvider(new TestHttpMessageHandler());

            var descriptor =
                CreateDescriptor(
                    runtimeInstanceId,
                    endpoint: "not-a-valid-uri");

            var result =
                await provider.GetRunStatusAsync(
                    descriptor,
                    CreateQueueRequest(
                        runtimeInstanceId,
                        AiRuntimeQueueControlPlaneOperation.GetRunStatus),
                    CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("http-endpoint-invalid", result.FailureReason);
            Assert.Equal(runtimeInstanceId, result.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that HTTP non-success status returns a failed dispatch result.
        /// </summary>
        [Fact]
        public async Task DispatchAsync_WithHttpFailure_ShouldReturnFailure()
        {
            var runtimeInstanceId = "runtime-http-1";

            var handler =
                new TestHttpMessageHandler
                {
                    Response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
                };

            var provider =
                CreateProvider(handler);

            var result =
                await provider.DispatchAsync(
                    CreateDescriptor(runtimeInstanceId),
                    CreateDispatchRequest(runtimeInstanceId),
                    CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("http-command-failed", result.FailureReason);
            Assert.Equal(runtimeInstanceId, result.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that an empty HTTP response body returns a failed queue result.
        /// </summary>
        [Fact]
        public async Task GetRunStatusAsync_WithEmptyResponseBody_ShouldReturnFailure()
        {
            var runtimeInstanceId = "runtime-http-1";

            var handler =
                new TestHttpMessageHandler
                {
                    Response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(string.Empty)
                    }
                };

            var provider =
                CreateProvider(handler);

            var result =
                await provider.GetRunStatusAsync(
                    CreateDescriptor(runtimeInstanceId),
                    CreateQueueRequest(
                        runtimeInstanceId,
                        AiRuntimeQueueControlPlaneOperation.GetRunStatus),
                    CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("http-command-exception", result.FailureReason);
            Assert.Equal(runtimeInstanceId, result.RuntimeInstanceId);
        }

        /// <summary>
        /// Executes a queue command provider test.
        /// </summary>
        private static async Task<AiRuntimeQueueControlPlaneResult> ExecuteQueueCommandTestAsync(
            AiRuntimeInstanceCommandOperation expectedCommandOperation,
            AiRuntimeQueueControlPlaneOperation queueOperation,
            Func<
                HttpAiRuntimeInstanceProvider,
                AiRuntimeInstanceCapacityDescriptor,
                AiRuntimeQueueControlPlaneRequest,
                Task<AiRuntimeQueueControlPlaneResult>> action)
        {
            var runtimeInstanceId = "runtime-http-1";
            var expectedQueueResult =
                CreateQueueResult(
                    runtimeInstanceId,
                    queueOperation,
                    success: true);

            var handler =
                new TestHttpMessageHandler
                {
                    Response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(
                            new AiRuntimeInstanceCommandResult
                            {
                                Success = true,
                                Operation = expectedCommandOperation,
                                RuntimeInstanceId = runtimeInstanceId,
                                QueueResult = expectedQueueResult,
                                StartedAtUtc = DateTimeOffset.UtcNow,
                                CompletedAtUtc = DateTimeOffset.UtcNow,
                                DurationMs = 0
                            })
                    }
                };

            var provider =
                CreateProvider(handler);

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
            Assert.Equal(runtimeInstanceId, result.RuntimeInstanceId);
            Assert.Equal(1, handler.SendCallCount);
            Assert.NotNull(handler.LastRequest);
            Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
            Assert.Equal(
                "http://runtime-http-1:8080/runtime-instance/commands",
                handler.LastRequest.RequestUri!.ToString());

            var command =
                await handler.ReadLastCommandAsync();

            Assert.NotNull(command);
            Assert.Equal(expectedCommandOperation, command!.Operation);
            Assert.Equal(runtimeInstanceId, command.RuntimeInstanceId);
            Assert.Null(command.DispatchRequest);
            Assert.NotNull(command.QueueRequest);

            return result;
        }

        /// <summary>
        /// Creates an HTTP runtime instance provider.
        /// </summary>
        private static HttpAiRuntimeInstanceProvider CreateProvider(
            HttpMessageHandler handler)
        {
            return new HttpAiRuntimeInstanceProvider(
                new HttpClient(handler),
                NullLogger<HttpAiRuntimeInstanceProvider>.Instance);
        }

        /// <summary>
        /// Creates a runtime instance capacity descriptor.
        /// </summary>
        private static AiRuntimeInstanceCapacityDescriptor CreateDescriptor(
            string runtimeInstanceId = "runtime-http-1",
            string providerName = "http",
            string endpoint = "http://runtime-http-1:8080",
            bool includeEndpoint = true)
        {
            var metadata =
                new Dictionary<string, string>
                {
                    [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = providerName
                };

            if (includeEndpoint)
            {
                metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] =
                    endpoint;
            }

            return new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = runtimeInstanceId,
                Metadata = metadata
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
        /// Test HTTP message handler.
        /// </summary>
        private sealed class TestHttpMessageHandler : HttpMessageHandler
        {
            /// <summary>
            /// Gets or sets the response returned by this handler.
            /// </summary>
            public HttpResponseMessage? Response { get; set; }

            /// <summary>
            /// Gets the last HTTP request sent through this handler.
            /// </summary>
            public HttpRequestMessage? LastRequest { get; private set; }

            /// <summary>
            /// Gets the last HTTP request body.
            /// </summary>
            public string? LastRequestBody { get; private set; }

            /// <summary>
            /// Gets the number of send calls.
            /// </summary>
            public int SendCallCount { get; private set; }

            /// <inheritdoc />
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(request);

                SendCallCount++;
                LastRequest = request;

                if (request.Content is not null)
                {
                    LastRequestBody =
                        await request.Content
                            .ReadAsStringAsync(cancellationToken)
                            .ConfigureAwait(false);
                }

                return Response ??
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(
                            new AiRuntimeInstanceCommandResult
                            {
                                Success = false,
                                Operation = AiRuntimeInstanceCommandOperation.Unknown,
                                RuntimeInstanceId = "unknown",
                                FailureReason = "test-response-not-configured",
                                Message = "Test response was not configured.",
                                StartedAtUtc = DateTimeOffset.UtcNow,
                                CompletedAtUtc = DateTimeOffset.UtcNow,
                                DurationMs = 0
                            })
                    };
            }

            /// <summary>
            /// Reads the last command request sent through this handler.
            /// </summary>
            /// <returns>The last runtime instance command request.</returns>
            public async Task<AiRuntimeInstanceCommandRequest?> ReadLastCommandAsync()
            {
                if (string.IsNullOrWhiteSpace(LastRequestBody))
                {
                    return null;
                }

                using var stream =
                    new MemoryStream(
                        System.Text.Encoding.UTF8.GetBytes(LastRequestBody));

                var options =
                    new System.Text.Json.JsonSerializerOptions(
                        System.Text.Json.JsonSerializerDefaults.Web)
                    {
                        PropertyNameCaseInsensitive = true
                    };

                return await System.Text.Json.JsonSerializer.DeserializeAsync<AiRuntimeInstanceCommandRequest>(
                    stream,
                    options);
            }
        }
    }
}