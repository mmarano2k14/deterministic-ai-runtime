using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
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
        /// Verifies that a Runtime Pool member is dispatched through the stable Runtime Pool router
        /// instead of the single-runtime HTTP command endpoint.
        /// </summary>
        [Fact]
        public async Task DispatchAsync_WithRuntimePoolDescriptor_ShouldPostToStableRuntimePoolEndpoint()
        {
            var runtimeInstanceId =
                "runtime-http-pool-1";

            var handler =
                new TestHttpMessageHandler
                {
                    Response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(
                            new AiRuntimeInstanceCommandResult
                            {
                                Success = true,
                                Operation =
                                    AiRuntimeInstanceCommandOperation
                                        .DispatchRun,
                                RuntimeInstanceId = runtimeInstanceId,
                                DispatchResult = CreateDispatchResult(
                                    runtimeInstanceId,
                                    success: true),
                                StartedAtUtc = DateTimeOffset.UtcNow,
                                CompletedAtUtc = DateTimeOffset.UtcNow,
                                DurationMs = 0
                            })
                    }
                };

            var provider =
                CreateProvider(handler);

            var descriptor =
                CreateDescriptor(
                    runtimeInstanceId,
                    endpoint: "http://127.0.0.1:64158",
                    poolId: "runtime-pool-1",
                    additionalMetadata:
                        new Dictionary<string, string>
                        {
                            ["runtime.pool.id"] =
                                "runtime-pool-1",
                            ["host.creation.mode"] =
                                "KubernetesPool",
                            ["hostType"] =
                                "runtime-instance-kubernetes-pool"
                        });

            var result =
                await provider.DispatchAsync(
                    descriptor,
                    CreateDispatchRequest(runtimeInstanceId),
                    CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(runtimeInstanceId, result.RuntimeInstanceId);
            Assert.Equal(1, handler.SendCallCount);
            Assert.NotNull(handler.LastRequest);
            Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
            Assert.Equal(
                "http://127.0.0.1:64158/runtime-pool/commands",
                handler.LastRequest.RequestUri!.ToString());
        }

        /// <summary>
        /// Verifies that Runtime Pool routing is selected from canonical PoolId membership even
        /// when optional host metadata has not yet converged.
        /// </summary>
        [Fact]
        public async Task GetRunStatusAsync_WithPoolIdOnly_ShouldPostToStableRuntimePoolEndpoint()
        {
            var runtimeInstanceId =
                "runtime-http-pool-1";

            var handler =
                new TestHttpMessageHandler
                {
                    Response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(
                            new AiRuntimeInstanceCommandResult
                            {
                                Success = true,
                                Operation =
                                    AiRuntimeInstanceCommandOperation
                                        .GetRunStatus,
                                RuntimeInstanceId = runtimeInstanceId,
                                QueueResult = CreateQueueResult(
                                    runtimeInstanceId,
                                    AiRuntimeQueueControlPlaneOperation
                                        .GetRunStatus,
                                    success: true),
                                StartedAtUtc = DateTimeOffset.UtcNow,
                                CompletedAtUtc = DateTimeOffset.UtcNow,
                                DurationMs = 0
                            })
                    }
                };

            var provider =
                CreateProvider(handler);

            var descriptor =
                CreateDescriptor(
                    runtimeInstanceId,
                    endpoint: "http://127.0.0.1:64158",
                    poolId: "runtime-pool-1");

            var result =
                await provider.GetRunStatusAsync(
                    descriptor,
                    CreateQueueRequest(
                        runtimeInstanceId,
                        AiRuntimeQueueControlPlaneOperation
                            .GetRunStatus),
                    CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(1, handler.SendCallCount);
            Assert.NotNull(handler.LastRequest);
            Assert.Equal(
                "http://127.0.0.1:64158/runtime-pool/commands",
                handler.LastRequest!.RequestUri!.ToString());
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
                CreateProvider(
                    handler,
                    new AiHttpRuntimeInstanceProviderOptions
                    {
                        EnableRetry = false,
                        MaxRetryAttempts = 0
                    });

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
        /// Verifies that HTTP provider unavailable failures are retried when retry is enabled.
        /// </summary>
        [Fact]
        public async Task DispatchAsync_Should_Retry_When_HttpRequestException_Is_Thrown_And_Retry_Is_Enabled()
        {
            var handler =
                new ThrowingHttpMessageHandler(
                    _ =>
                    {
                        throw new HttpRequestException(
                            "Remote runtime unavailable.");
                    });

            var provider =
                CreateProvider(
                    handler,
                    new AiHttpRuntimeInstanceProviderOptions
                    {
                        EnableRetry = true,
                        MaxRetryAttempts = 2,
                        RetryBaseDelay = TimeSpan.Zero,
                        RetryMaxDelay = TimeSpan.Zero,
                        DispatchTimeout = TimeSpan.FromSeconds(5)
                    });

            var runtimeInstanceId =
                "runtime-http-1";

            var result =
                await provider.DispatchAsync(
                    CreateDescriptor(
                        runtimeInstanceId,
                        endpoint: "http://localhost:5001"),
                    CreateDispatchRequest(runtimeInstanceId),
                    CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(
                AiHttpRuntimeDispatchFailureReasons.ProviderUnavailable,
                result.FailureReason);
            Assert.Equal(3, handler.SendCallCount);
        }

        /// <summary>
        /// Verifies that non-retryable HTTP client failures are not retried.
        /// </summary>
        [Fact]
        public async Task DispatchAsync_Should_Not_Retry_When_HttpClientFailure_Is_NonRetryable()
        {
            var runtimeInstanceId =
                "runtime-http-1";

            var handler =
                new TestHttpMessageHandler
                {
                    Response = new HttpResponseMessage(HttpStatusCode.BadRequest)
                };

            var provider =
                CreateProvider(
                    handler,
                    new AiHttpRuntimeInstanceProviderOptions
                    {
                        EnableRetry = true,
                        MaxRetryAttempts = 2,
                        RetryBaseDelay = TimeSpan.Zero,
                        RetryMaxDelay = TimeSpan.Zero,
                        DispatchTimeout = TimeSpan.FromSeconds(5)
                    });

            var result =
                await provider.DispatchAsync(
                    CreateDescriptor(runtimeInstanceId),
                    CreateDispatchRequest(runtimeInstanceId),
                    CancellationToken.None);

            Assert.False(result.Success);

            Assert.Equal(
                AiHttpRuntimeDispatchFailureReasons.NonRetryableHttpError,
                result.FailureReason);

            Assert.Equal(
                1,
                handler.SendCallCount);
        }

        /// <summary>
        /// Verifies that retryable HTTP server failures are retried and can succeed on a later attempt.
        /// </summary>
        [Fact]
        public async Task DispatchAsync_Should_Retry_And_Succeed_When_HttpServerFailure_Is_Followed_By_Success()
        {
            var runtimeInstanceId =
                "runtime-http-1";

            var expectedDispatchResult =
                CreateDispatchResult(
                    runtimeInstanceId,
                    success: true);

            var handler =
                new SequenceHttpMessageHandler(
                    new HttpResponseMessage(HttpStatusCode.InternalServerError),
                    new HttpResponseMessage(HttpStatusCode.OK)
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
                    });

            var provider =
                CreateProvider(
                    handler,
                    new AiHttpRuntimeInstanceProviderOptions
                    {
                        EnableRetry = true,
                        MaxRetryAttempts = 2,
                        RetryBaseDelay = TimeSpan.Zero,
                        RetryMaxDelay = TimeSpan.Zero,
                        DispatchTimeout = TimeSpan.FromSeconds(5)
                    });

            var result =
                await provider.DispatchAsync(
                    CreateDescriptor(runtimeInstanceId),
                    CreateDispatchRequest(runtimeInstanceId),
                    CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(runtimeInstanceId, result.RuntimeInstanceId);
            Assert.Equal(2, handler.SendCallCount);
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
        /// Verifies that HTTP dispatch timeouts are not retried by default.
        /// </summary>
        [Fact]
        public async Task DispatchAsync_Should_Not_Retry_When_Timeouts_Are_Not_Retryable()
        {
            var runtimeInstanceId =
                "runtime-http-1";

            var handler =
                new DelayedHttpMessageHandler(
                    TimeSpan.FromMilliseconds(200));

            var provider =
                CreateProvider(
                    handler,
                    new AiHttpRuntimeInstanceProviderOptions
                    {
                        EnableRetry = true,
                        MaxRetryAttempts = 2,
                        RetryBaseDelay = TimeSpan.Zero,
                        RetryMaxDelay = TimeSpan.Zero,
                        RetryTimeouts = false,
                        DispatchTimeout = TimeSpan.FromMilliseconds(25)
                    });

            var result =
                await provider.DispatchAsync(
                    CreateDescriptor(runtimeInstanceId),
                    CreateDispatchRequest(runtimeInstanceId),
                    CancellationToken.None);

            Assert.False(result.Success);

            Assert.Equal(
                AiHttpRuntimeDispatchFailureReasons.Timeout,
                result.FailureReason);

            Assert.Equal(
                1,
                handler.SendCallCount);
        }

        /// <summary>
        /// Verifies that HTTP dispatch timeouts are retried when timeout retry is explicitly enabled.
        /// </summary>
        [Fact]
        public async Task DispatchAsync_Should_Retry_When_Timeouts_Are_Retryable()
        {
            var runtimeInstanceId =
                "runtime-http-1";

            var expectedDispatchResult =
                CreateDispatchResult(
                    runtimeInstanceId,
                    success: true);

            var handler =
                new TimeoutThenSuccessHttpMessageHandler(
                    TimeSpan.FromMilliseconds(200),
                    new HttpResponseMessage(HttpStatusCode.OK)
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
                    });

            var provider =
                CreateProvider(
                    handler,
                    new AiHttpRuntimeInstanceProviderOptions
                    {
                        EnableRetry = true,
                        MaxRetryAttempts = 2,
                        RetryBaseDelay = TimeSpan.Zero,
                        RetryMaxDelay = TimeSpan.Zero,
                        RetryTimeouts = true,
                        DispatchTimeout = TimeSpan.FromMilliseconds(25)
                    });

            var result =
                await provider.DispatchAsync(
                    CreateDescriptor(runtimeInstanceId),
                    CreateDispatchRequest(runtimeInstanceId),
                    CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(runtimeInstanceId, result.RuntimeInstanceId);
            Assert.Equal(2, handler.SendCallCount);
        }

        /// <summary>
        /// Verifies that the HTTP circuit breaker opens after the configured failure threshold
        /// and prevents the next HTTP call from reaching the remote runtime endpoint.
        /// </summary>
        [Fact]
        public async Task DispatchAsync_Should_Return_CircuitOpen_When_CircuitBreaker_Is_Open()
        {
            var runtimeInstanceId =
                "runtime-http-1";

            var handler =
                new SequenceHttpMessageHandler(
                    new HttpResponseMessage(HttpStatusCode.InternalServerError),
                    new HttpResponseMessage(HttpStatusCode.InternalServerError),
                    new HttpResponseMessage(HttpStatusCode.InternalServerError));

            var provider =
                CreateProvider(
                    handler,
                    new AiHttpRuntimeInstanceProviderOptions
                    {
                        EnableRetry = false,
                        EnableCircuitBreaker = true,
                        CircuitBreakerFailureThreshold = 2,
                        CircuitBreakerBreakDuration = TimeSpan.FromMinutes(1),
                        DispatchTimeout = TimeSpan.FromSeconds(5)
                    });

            var descriptor =
                CreateDescriptor(runtimeInstanceId);

            var firstResult =
                await provider.DispatchAsync(
                    descriptor,
                    CreateDispatchRequest(runtimeInstanceId),
                    CancellationToken.None);

            var secondResult =
                await provider.DispatchAsync(
                    descriptor,
                    CreateDispatchRequest(runtimeInstanceId),
                    CancellationToken.None);

            var thirdResult =
                await provider.DispatchAsync(
                    descriptor,
                    CreateDispatchRequest(runtimeInstanceId),
                    CancellationToken.None);

            Assert.False(firstResult.Success);

            Assert.Equal(
                AiHttpRuntimeDispatchFailureReasons.HttpError,
                firstResult.FailureReason);

            Assert.False(secondResult.Success);

            Assert.Equal(
                AiHttpRuntimeDispatchFailureReasons.HttpError,
                secondResult.FailureReason);

            Assert.False(thirdResult.Success);

            Assert.Equal(
                AiHttpRuntimeDispatchFailureReasons.CircuitOpen,
                thirdResult.FailureReason);

            Assert.Equal(
                2,
                handler.SendCallCount);
        }

        /// <summary>
        /// Verifies that the HTTP circuit breaker does not block calls when it is disabled.
        /// </summary>
        [Fact]
        public async Task DispatchAsync_Should_Not_Return_CircuitOpen_When_CircuitBreaker_Is_Disabled()
        {
            var runtimeInstanceId =
                "runtime-http-1";

            var handler =
                new SequenceHttpMessageHandler(
                    new HttpResponseMessage(HttpStatusCode.InternalServerError),
                    new HttpResponseMessage(HttpStatusCode.InternalServerError),
                    new HttpResponseMessage(HttpStatusCode.InternalServerError));

            var provider =
                CreateProvider(
                    handler,
                    new AiHttpRuntimeInstanceProviderOptions
                    {
                        EnableRetry = false,
                        EnableCircuitBreaker = false,
                        CircuitBreakerFailureThreshold = 1,
                        CircuitBreakerBreakDuration = TimeSpan.FromMinutes(1),
                        DispatchTimeout = TimeSpan.FromSeconds(5)
                    });

            var descriptor =
                CreateDescriptor(runtimeInstanceId);

            var firstResult =
                await provider.DispatchAsync(
                    descriptor,
                    CreateDispatchRequest(runtimeInstanceId),
                    CancellationToken.None);

            var secondResult =
                await provider.DispatchAsync(
                    descriptor,
                    CreateDispatchRequest(runtimeInstanceId),
                    CancellationToken.None);

            var thirdResult =
                await provider.DispatchAsync(
                    descriptor,
                    CreateDispatchRequest(runtimeInstanceId),
                    CancellationToken.None);

            Assert.False(firstResult.Success);

            Assert.Equal(
                AiHttpRuntimeDispatchFailureReasons.HttpError,
                firstResult.FailureReason);

            Assert.False(secondResult.Success);

            Assert.Equal(
                AiHttpRuntimeDispatchFailureReasons.HttpError,
                secondResult.FailureReason);

            Assert.False(thirdResult.Success);

            Assert.Equal(
                AiHttpRuntimeDispatchFailureReasons.HttpError,
                thirdResult.FailureReason);

            Assert.Equal(
                3,
                handler.SendCallCount);
        }

        /// <summary>
        /// Verifies that a successful HTTP command resets the circuit breaker failure count.
        /// </summary>
        [Fact]
        public async Task DispatchAsync_Should_Reset_CircuitBreaker_Failures_After_Success()
        {
            var runtimeInstanceId =
                "runtime-http-1";

            var handler =
                new SequenceHttpMessageHandler(
                    new HttpResponseMessage(HttpStatusCode.InternalServerError),
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(
                            new AiRuntimeInstanceCommandResult
                            {
                                Success = true,
                                Operation = AiRuntimeInstanceCommandOperation.DispatchRun,
                                RuntimeInstanceId = runtimeInstanceId,
                                DispatchResult = CreateDispatchResult(
                                    runtimeInstanceId,
                                    success: true),
                                StartedAtUtc = DateTimeOffset.UtcNow,
                                CompletedAtUtc = DateTimeOffset.UtcNow,
                                DurationMs = 0
                            })
                    },
                    new HttpResponseMessage(HttpStatusCode.InternalServerError));

            var provider =
                CreateProvider(
                    handler,
                    new AiHttpRuntimeInstanceProviderOptions
                    {
                        EnableRetry = false,
                        EnableCircuitBreaker = true,
                        CircuitBreakerFailureThreshold = 2,
                        CircuitBreakerBreakDuration = TimeSpan.FromMinutes(1),
                        DispatchTimeout = TimeSpan.FromSeconds(5)
                    });

            var descriptor =
                CreateDescriptor(runtimeInstanceId);

            var firstResult =
                await provider.DispatchAsync(
                    descriptor,
                    CreateDispatchRequest(runtimeInstanceId),
                    CancellationToken.None);

            var secondResult =
                await provider.DispatchAsync(
                    descriptor,
                    CreateDispatchRequest(runtimeInstanceId),
                    CancellationToken.None);

            var thirdResult =
                await provider.DispatchAsync(
                    descriptor,
                    CreateDispatchRequest(runtimeInstanceId),
                    CancellationToken.None);

            Assert.False(firstResult.Success);

            Assert.Equal(
                AiHttpRuntimeDispatchFailureReasons.HttpError,
                firstResult.FailureReason);

            Assert.True(secondResult.Success);

            Assert.False(thirdResult.Success);

            Assert.Equal(
                AiHttpRuntimeDispatchFailureReasons.HttpError,
                thirdResult.FailureReason);

            Assert.Equal(
                3,
                handler.SendCallCount);
        }

        /// <summary>
        /// Test HTTP message handler that returns HTTP responses in sequence.
        /// </summary>
        private sealed class SequenceHttpMessageHandler : HttpMessageHandler
        {
            /// <summary>
            /// The response queue.
            /// </summary>
            private readonly Queue<HttpResponseMessage> responses;

            /// <summary>
            /// Initializes a new instance of the <see cref="SequenceHttpMessageHandler"/> class.
            /// </summary>
            /// <param name="responses">The HTTP responses returned in order.</param>
            public SequenceHttpMessageHandler(
                params HttpResponseMessage[] responses)
            {
                this.responses =
                    new Queue<HttpResponseMessage>(
                        responses ?? Array.Empty<HttpResponseMessage>());
            }

            /// <summary>
            /// Gets the number of send calls.
            /// </summary>
            public int SendCallCount { get; private set; }

            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(request);

                SendCallCount++;

                if (this.responses.Count == 0)
                {
                    return Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.InternalServerError));
                }

                return Task.FromResult(
                    this.responses.Dequeue());
            }
        }



        /// <summary>
        /// Creates an HTTP runtime instance provider using the supplied HTTP message handler.
        /// </summary>
        /// <param name="handler">The HTTP message handler used by the test HTTP client.</param>
        /// <returns>The HTTP runtime instance provider.</returns>
        private static HttpAiRuntimeInstanceProvider CreateProvider(
            HttpMessageHandler handler)
        {
            return CreateProvider(
                handler,
                new AiHttpRuntimeInstanceProviderOptions());
        }

        /// <summary>
        /// Creates an HTTP runtime instance provider using the supplied HTTP message handler and options.
        /// </summary>
        /// <param name="handler">The HTTP message handler used by the test HTTP client.</param>
        /// <param name="options">The HTTP provider options.</param>
        /// <returns>The HTTP runtime instance provider.</returns>
        private static HttpAiRuntimeInstanceProvider CreateProvider(
            HttpMessageHandler handler,
            AiHttpRuntimeInstanceProviderOptions options)
        {
            return new HttpAiRuntimeInstanceProvider(
                new HttpClient(handler),
                NullLogger<HttpAiRuntimeInstanceProvider>.Instance,
                Options.Create(options));
        }

        /// <summary>
        /// Creates a runtime instance capacity descriptor.
        /// </summary>
        private static AiRuntimeInstanceCapacityDescriptor CreateDescriptor(
            string runtimeInstanceId = "runtime-http-1",
            string providerName = "http",
            string endpoint = "http://runtime-http-1:8080",
            bool includeEndpoint = true,
            string? poolId = null,
            IReadOnlyDictionary<string, string>? additionalMetadata = null)
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

            if (additionalMetadata is not null)
            {
                foreach (var item in additionalMetadata)
                {
                    metadata[item.Key] =
                        item.Value;
                }
            }

            return new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = runtimeInstanceId,
                PoolId = poolId,
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
                    ExecutionContextSnapshot = CreateExecutionContextSnapshot(),
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
        /// Creates an execution context snapshot for test dispatch records.
        /// </summary>
        /// <returns>The execution context snapshot.</returns>
        private static ExecutionContextSnapshot CreateExecutionContextSnapshot()
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = Guid.NewGuid().ToString("N"),
                Project = "distributed-deterministic-ai-runtime",
                UserId = "unit-test",
                TenantId = "tenant-id-xxxx",
                TenantGroupId = "tenant-group-id-xxx",
                CurrentNamespace = "mcp-ai-runtime",
                Namespaces = new List<NamespaceEntry>
            {
                new()
                {
                    Name = "mcp-ai-runtime",
                    Trns = new HashSet<string>
                    {
                        "trn:distributed-deterministic-ai-runtime:shared-run:execution:submit"
                    }
                }
            },
                InFlightCount = 0,
                TtlSeconds = 300,
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Test HTTP message handler that delays before returning a response.
        /// </summary>
        private sealed class DelayedHttpMessageHandler : HttpMessageHandler
        {
            /// <summary>
            /// The response delay.
            /// </summary>
            private readonly TimeSpan delay;

            /// <summary>
            /// Initializes a new instance of the <see cref="DelayedHttpMessageHandler"/> class.
            /// </summary>
            /// <param name="delay">The response delay.</param>
            public DelayedHttpMessageHandler(
                TimeSpan delay)
            {
                this.delay =
                    delay;
            }

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

                await Task.Delay(
                        this.delay,
                        cancellationToken)
                    .ConfigureAwait(false);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        new AiRuntimeInstanceCommandResult
                        {
                            Success = true,
                            Operation = AiRuntimeInstanceCommandOperation.DispatchRun,
                            RuntimeInstanceId = "runtime-http-1",
                            StartedAtUtc = DateTimeOffset.UtcNow,
                            CompletedAtUtc = DateTimeOffset.UtcNow,
                            DurationMs = 0
                        })
                };
            }
        }

        /// <summary>
        /// Test HTTP message handler that times out once and then returns a successful response.
        /// </summary>
        private sealed class TimeoutThenSuccessHttpMessageHandler : HttpMessageHandler
        {
            /// <summary>
            /// The first attempt delay.
            /// </summary>
            private readonly TimeSpan firstAttemptDelay;

            /// <summary>
            /// The success response returned after the first timeout.
            /// </summary>
            private readonly HttpResponseMessage successResponse;

            /// <summary>
            /// Initializes a new instance of the <see cref="TimeoutThenSuccessHttpMessageHandler"/> class.
            /// </summary>
            /// <param name="firstAttemptDelay">The first attempt delay.</param>
            /// <param name="successResponse">The success response.</param>
            public TimeoutThenSuccessHttpMessageHandler(
                TimeSpan firstAttemptDelay,
                HttpResponseMessage successResponse)
            {
                this.firstAttemptDelay =
                    firstAttemptDelay;

                this.successResponse =
                    successResponse
                    ?? throw new ArgumentNullException(nameof(successResponse));
            }

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

                if (SendCallCount == 1)
                {
                    await Task.Delay(
                            this.firstAttemptDelay,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                return this.successResponse;
            }
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

        /// <summary>
        /// Test HTTP message handler that throws an exception for each send attempt.
        /// </summary>
        private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
        {
            /// <summary>
            /// Send callback used by this handler.
            /// </summary>
            private readonly Func<HttpRequestMessage, Exception> exceptionFactory;

            /// <summary>
            /// Initializes a new instance of the <see cref="ThrowingHttpMessageHandler"/> class.
            /// </summary>
            /// <param name="exceptionFactory">The exception factory.</param>
            public ThrowingHttpMessageHandler(
                Func<HttpRequestMessage, Exception> exceptionFactory)
            {
                this.exceptionFactory =
                    exceptionFactory
                    ?? throw new ArgumentNullException(nameof(exceptionFactory));
            }

            /// <summary>
            /// Gets the number of send calls.
            /// </summary>
            public int SendCallCount { get; private set; }

            /// <inheritdoc />
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(request);

                SendCallCount++;

                throw this.exceptionFactory(
                    request);
            }
        }
    }

}
