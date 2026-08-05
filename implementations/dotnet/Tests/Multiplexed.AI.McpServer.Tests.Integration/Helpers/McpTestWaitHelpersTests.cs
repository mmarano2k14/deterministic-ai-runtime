using System.Net;
using System.Text;
using System.Text.Json;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;

namespace Multiplexed.AI.McpServer.Tests.Integration.Helpers
{
    /// <summary>
    /// Verifies MCP wait-helper backpressure behavior.
    /// </summary>
    public sealed class McpTestWaitHelpersTests
    {
        [Fact]
        public async Task WaitForTerminalRuntimeRunStatusesAsync_Should_Retry_Transient_429_And_Return_Terminal_Status()
        {
            var handler =
                new RuntimeStatusHandler(
                    tooManyRequestsBeforeSuccess: 2);

            using var httpClient =
                new HttpClient(handler)
                {
                    BaseAddress =
                        new Uri("http://localhost")
                };

            var mcp =
                new McpTestClient(httpClient);

            var statuses =
                await McpTestWaitHelpers
                    .WaitForTerminalRuntimeRunStatusesAsync(
                        mcp,
                        new[]
                        {
                            CreateDispatchedRun(
                                "shared-run-1",
                                "runtime-1",
                                "local-run-1",
                                "execution-1")
                        },
                        TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);

            var status =
                Assert.Single(statuses);

            Assert.True(status.Success);
            Assert.Equal("completed", status.RunState?.Status);
            Assert.Equal(3, handler.RequestCount);
        }

        [Fact]
        public async Task WaitForTerminalRuntimeRunStatusesAsync_Should_Stop_After_Bounded_Consecutive_429()
        {
            var handler =
                new RuntimeStatusHandler(
                    tooManyRequestsBeforeSuccess:
                        int.MaxValue);

            using var httpClient =
                new HttpClient(handler)
                {
                    BaseAddress =
                        new Uri("http://localhost")
                };

            var mcp =
                new McpTestClient(httpClient);

            var exception =
                await Assert.ThrowsAsync<
                    HttpRequestException>(
                    () =>
                        McpTestWaitHelpers
                            .WaitForTerminalRuntimeRunStatusesAsync(
                                mcp,
                                new[]
                                {
                                    CreateDispatchedRun(
                                        "shared-run-throttled",
                                        "runtime-1",
                                        "local-run-throttled",
                                        "execution-throttled")
                                },
                                TimeSpan.FromMinutes(45)));

            Assert.Equal(
                HttpStatusCode.TooManyRequests,
                exception.StatusCode);

            Assert.Contains(
                "after '6' consecutive attempts",
                exception.Message,
                StringComparison.Ordinal);

            Assert.Equal(6, handler.RequestCount);
        }

        [Fact]
        public async Task WaitForTerminalRuntimeRunStatusesAsync_Should_Not_Repoll_Already_Terminal_Runs()
        {
            var handler =
                new PerRunRuntimeStatusHandler();

            using var httpClient =
                new HttpClient(handler)
                {
                    BaseAddress =
                        new Uri("http://localhost")
                };

            var mcp =
                new McpTestClient(httpClient);

            var statuses =
                await McpTestWaitHelpers
                    .WaitForTerminalRuntimeRunStatusesAsync(
                        mcp,
                        new[]
                        {
                            CreateDispatchedRun(
                                "shared-run-terminal",
                                "runtime-1",
                                "local-run-terminal",
                                "execution-terminal"),
                            CreateDispatchedRun(
                                "shared-run-progress",
                                "runtime-1",
                                "local-run-progress",
                                "execution-progress")
                        },
                        TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);

            Assert.Equal(2, statuses.Count);
            Assert.All(
                statuses,
                status =>
                    Assert.Equal(
                        "completed",
                        status.RunState?.Status));

            Assert.Equal(
                1,
                handler.GetRequestCount(
                    "local-run-terminal"));

            Assert.Equal(
                2,
                handler.GetRequestCount(
                    "local-run-progress"));
        }

        private static AiSharedRunRecord CreateDispatchedRun(
            string sharedRunId,
            string runtimeInstanceId,
            string localRunId,
            string executionId)
        {
            var now =
                DateTimeOffset.UtcNow;

            var context =
                new ExecutionContextSnapshot
                {
                    ContextKey = "context-key",
                    Project = "project",
                    UserId = "user",
                    TenantId = "tenant",
                    TenantGroupId = "tenant-group",
                    CurrentNamespace = "default",
                    Namespaces = new List<NamespaceEntry>(),
                    TtlSeconds = 300
                };

            return new AiSharedRunRecord
            {
                SharedRunId = sharedRunId,
                Status = AiSharedRunStatus.Dispatched,
                RunRequest =
                    new AiRuntimePipelineRunRequest
                    {
                        PipelineName = "pipeline",
                        ExecutionContextSnapshot =
                            context
                    },
                ExecutionContextSnapshot = context,
                AssignedRuntimeInstanceId =
                    runtimeInstanceId,
                LocalRunId = localRunId,
                ExecutionId = executionId,
                SubmittedAtUtc = now,
                UpdatedAtUtc = now
            };
        }

        private static HttpResponseMessage CreateTerminalResponse(
            string runId)
        {
            var result =
                new AiRuntimeQueueControlPlaneResult
                {
                    Operation =
                        AiRuntimeQueueControlPlaneOperation
                            .GetRunStatus,
                    Success = true,
                    RuntimeInstanceId = "runtime-1",
                    RunId = runId,
                    ExecutionId =
                        $"execution-{runId}",
                    RunState =
                        new AiRuntimePipelineRunState
                        {
                            RunId = runId,
                            ExecutionId =
                                $"execution-{runId}",
                            RuntimeInstanceId =
                                "runtime-1",
                            Status = "completed"
                        }
                };

            return CreateToolResponse(result);
        }

        private static HttpResponseMessage CreateRunningResponse(
            string runId)
        {
            var result =
                new AiRuntimeQueueControlPlaneResult
                {
                    Operation =
                        AiRuntimeQueueControlPlaneOperation
                            .GetRunStatus,
                    Success = true,
                    RuntimeInstanceId = "runtime-1",
                    RunId = runId,
                    ExecutionId =
                        $"execution-{runId}",
                    RunState =
                        new AiRuntimePipelineRunState
                        {
                            RunId = runId,
                            ExecutionId =
                                $"execution-{runId}",
                            RuntimeInstanceId =
                                "runtime-1",
                            Status = "running",
                            IsRunning = true
                        }
                };

            return CreateToolResponse(result);
        }

        private static HttpResponseMessage CreateToolResponse<T>(
            T result)
        {
            var resultText =
                JsonSerializer.Serialize(result);

            var rpc =
                JsonSerializer.Serialize(
                    new
                    {
                        jsonrpc = "2.0",
                        id = "test",
                        result = new
                        {
                            content = new[]
                            {
                                new
                                {
                                    type = "text",
                                    text = resultText
                                }
                            },
                            isError = false
                        }
                    });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content =
                    new StringContent(
                        rpc,
                        Encoding.UTF8,
                        "application/json")
            };
        }

        private sealed class RuntimeStatusHandler :
            HttpMessageHandler
        {
            private readonly int
                tooManyRequestsBeforeSuccess;

            private int requestCount;

            public RuntimeStatusHandler(
                int tooManyRequestsBeforeSuccess)
            {
                this.tooManyRequestsBeforeSuccess =
                    tooManyRequestsBeforeSuccess;
            }

            public int RequestCount =>
                Volatile.Read(ref requestCount);

            protected override Task<HttpResponseMessage>
                SendAsync(
                    HttpRequestMessage request,
                    CancellationToken cancellationToken)
            {
                var current =
                    Interlocked.Increment(
                        ref requestCount);

                if (current <=
                    tooManyRequestsBeforeSuccess)
                {
                    return Task.FromResult(
                        new HttpResponseMessage(
                            HttpStatusCode.TooManyRequests)
                        {
                            Content =
                                new StringContent(
                                    "transient backpressure")
                        });
                }

                return Task.FromResult(
                    CreateTerminalResponse(
                        "local-run-1"));
            }
        }

        private sealed class PerRunRuntimeStatusHandler :
            HttpMessageHandler
        {
            private readonly object sync = new();

            private readonly Dictionary<string, int>
                counts =
                    new(
                        StringComparer.Ordinal);

            public int GetRequestCount(
                string runId)
            {
                lock (sync)
                {
                    return counts.TryGetValue(
                        runId,
                        out var count)
                        ? count
                        : 0;
                }
            }

            protected override async Task<HttpResponseMessage>
                SendAsync(
                    HttpRequestMessage request,
                    CancellationToken cancellationToken)
            {
                var body =
                    await request.Content!
                        .ReadAsStringAsync(
                            cancellationToken)
                        .ConfigureAwait(false);

                using var document =
                    JsonDocument.Parse(body);

                var runId =
                    document.RootElement
                        .GetProperty("params")
                        .GetProperty("arguments")
                        .GetProperty("request")
                        .GetProperty("RunId")
                        .GetString()
                    ?? throw new InvalidOperationException(
                        "RunId missing from MCP request.");

                int count;

                lock (sync)
                {
                    counts.TryGetValue(
                        runId,
                        out count);

                    count++;
                    counts[runId] = count;
                }

                if (string.Equals(
                        runId,
                        "local-run-progress",
                        StringComparison.Ordinal) &&
                    count == 1)
                {
                    return CreateRunningResponse(runId);
                }

                return CreateTerminalResponse(runId);
            }
        }
    }
}
