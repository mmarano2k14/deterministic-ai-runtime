using System.Text.Json;
using System.Text.Json.Nodes;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.AI.Runtime.Invocation.Mcp;
using static Multiplexed.AI.Tests.Runtime.Invocation.McpStepTestSupport;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    /// <summary>Closed response validation, cancellation and technical failures without retries.</summary>
    public sealed class AiMcpStepFailureTests
    {
        [Theory]
        [InlineData("missing-version")]
        [InlineData("wrong-version")]
        [InlineData("wrong-request")]
        [InlineData("missing-error")]
        [InlineData("string-error")]
        [InlineData("missing-content")]
        [InlineData("null-content")]
        [InlineData("string-content")]
        [InlineData("block-without-type")]
        [InlineData("text-without-text")]
        [InlineData("structured-array")]
        [InlineData("structured-null")]
        [InlineData("park")]
        [InlineData("payload")]
        [InlineData("huge")]
        public async Task Malformed_Response_Cannot_Become_A_Successful_Step(string invalid)
        {
            var transport = new Transport { Handler = (request, _) =>
            {
                var root = JsonNode.Parse(Response(request).GetRawText())!.AsObject();
                switch (invalid)
                {
                    case "missing-version": root.Remove("schemaVersion"); break;
                    case "wrong-version": root["schemaVersion"] = 2; break;
                    case "wrong-request": root["requestId"] = "other"; break;
                    case "missing-error": root.Remove("isError"); break;
                    case "string-error": root["isError"] = "false"; break;
                    case "missing-content": root.Remove("content"); break;
                    case "null-content": root["content"] = null; break;
                    case "string-content": root["content"] = "text"; break;
                    case "block-without-type": root["content"] = JsonNode.Parse("[{}]"); break;
                    case "text-without-text": root["content"] = JsonNode.Parse("[{\"type\":\"text\"}]"); break;
                    case "structured-array": root["structuredContent"] = new JsonArray(); break;
                    case "structured-null": root["structuredContent"] = null; break;
                    case "park": root["outcome"] = "Park"; break;
                    case "payload": root["payload"] = JsonNode.Parse("{\"artifactId\":\"foreign\"}"); break;
                    case "huge": root["structuredContent"] = new JsonObject { ["value"] = new string('x', 65536) }; break;
                }
                return Task.FromResult(Json(root.ToJsonString()));
            } };
            using var fixture = await CreateAsync(transport: transport);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.InvokeAsync());
            Assert.Single(transport.Calls);
        }

        [Theory]
        [InlineData("null")]
        [InlineData("[]")]
        [InlineData("true")]
        public async Task Non_Object_Response_Is_A_Technical_Error(string json)
        {
            var transport = new Transport { Handler = (_, _) => Task.FromResult(Json(json)) };
            using var fixture = await CreateAsync(transport: transport);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InvokeAsync());
            Assert.Single(transport.Calls);
        }

        [Fact]
        public async Task Duplicate_Response_Properties_Are_Refused()
        {
            var transport = new Transport { Handler = (request, _) =>
                Task.FromResult(Json(Response(request).GetRawText().Replace("\"isError\":false", "\"isError\":false,\"isError\":true", StringComparison.Ordinal))) };
            using var fixture = await CreateAsync(transport: transport);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InvokeAsync());
            Assert.Single(transport.Calls);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Technical_Errors_Propagate_Without_An_Adapter_Retry(bool resolution)
        {
            var error = new IOException("test transport failure");
            var resolver = new Resolver(); var transport = new Transport();
            if (resolution) resolver.Handler = (_, _) => Task.FromException<AiMcpToolBinding?>(error);
            else transport.Handler = (_, _) => Task.FromException<JsonElement>(error);
            using var fixture = await CreateAsync(resolver: resolver, transport: transport);
            Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => fixture.InvokeAsync()));
            Assert.Single(resolver.Calls); Assert.Equal(resolution ? 0 : 1, transport.Calls.Count);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task A_Null_Dependency_Task_Is_Not_An_Empty_Success(bool resolution)
        {
            var resolver = new Resolver(); var transport = new Transport();
            if (resolution) resolver.Handler = (_, _) => null!;
            else transport.Handler = (_, _) => null!;
            using var fixture = await CreateAsync(resolver: resolver, transport: transport);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InvokeAsync());
            Assert.Equal(resolution ? 0 : 1, transport.Calls.Count);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Precancelled_Execution_Or_Call_Does_Not_Resolve_A_Target(bool execution)
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            using var fixture = await CreateAsync(runtimeCancellation: execution ? cancelled.Token : default);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.InvokeAsync(execution ? default : cancelled.Token));
            Assert.Empty(fixture.Resolver.Calls); Assert.Empty(fixture.Transport.Calls);
        }

        [Fact]
        public async Task Unrequested_Transport_Cancellation_Is_A_Technical_Failure()
        {
            var transport = new Transport
            {
                Handler = (_, _) => Task.FromCanceled<JsonElement>(new CancellationToken(canceled: true))
            };
            using var fixture = await CreateAsync(transport: transport);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InvokeAsync());
            Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
            Assert.Single(transport.Calls);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Caller_Or_Execution_Cancellation_Is_Not_A_Timeout_Or_Tool_Error(bool execution)
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var transport = new Transport { Handler = (_, _) => { entered.TrySetResult(true); return pending.Task; } };
            using var cancellation = new CancellationTokenSource();
            using var fixture = await CreateAsync(transport: transport, runtimeCancellation: execution ? cancellation.Token : default);
            var call = fixture.InvokeAsync(execution ? default : cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
            Assert.True(transport.LastToken.IsCancellationRequested);
            pending.TrySetResult(Response(Assert.Single(transport.Calls)));
            Assert.Single(transport.Calls);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Server_Deadline_Bounds_Uncooperative_Dependencies_And_Does_Not_Accept_Late_Results(bool resolution)
        {
            var resolverPending = new TaskCompletionSource<AiMcpToolBinding?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var transportPending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var resolver = new Resolver(); var transport = new Transport();
            if (resolution) resolver.Handler = (_, _) => resolverPending.Task;
            else transport.Handler = (_, _) => transportPending.Task;
            using var fixture = await CreateAsync(resolver: resolver, transport: transport,
                options: new AiMcpStepInvocationOptions { InvocationTimeout = TimeSpan.FromSeconds(2) });
            await Assert.ThrowsAsync<TimeoutException>(() => fixture.InvokeAsync());
            var requested = Assert.Single(resolver.Calls);
            Assert.Equal(resolution ? 0 : 1, transport.Calls.Count);
            if (resolution) resolverPending.TrySetResult(Target(requested));
            else transportPending.TrySetResult(Response(Assert.Single(transport.Calls)));
            Assert.Equal(resolution ? 0 : 1, transport.Calls.Count);
            Assert.Null(fixture.Context.StepState.Result);
        }
    }
}
