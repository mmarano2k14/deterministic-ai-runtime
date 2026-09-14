using System.Text.Json;
using System.Text.Json.Nodes;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Engine.Steps;
using static Multiplexed.AI.Tests.Runtime.Invocation.McpStepTestSupport;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    /// <summary>Real helper resolution and stable result mapping, with detached portable data.</summary>
    public sealed class AiMcpStepInputResultTests
    {
        [Fact]
        public async Task Declared_Inputs_Are_Resolved_Without_Automatically_Exporting_Config_Or_State()
        {
            var input = new Dictionary<string, object?>
            {
                ["score"] = "state.score", ["summary"] = "steps.before.result.data.summary", ["literal"] = "document"
            };
            using var fixture = await CreateAsync(McpStepTestSupport.Pipeline(Step(input: input,
                config: new Dictionary<string, object?> { ["credential"] = "must-not-be-exported" })));
            fixture.Context.State.Data["score"] = 7;
            fixture.Context.State.Data["private"] = "must-not-be-exported";
            fixture.Context.State.Steps["before"] = new AiStepState
            {
                StepName = "before", Result = AiStepResult.Ok(data: new() { ["summary"] = "ready" })
            };
            await fixture.InvokeAsync();
            var request = Assert.Single(fixture.Transport.Calls);
            Assert.Equal(7, request.Arguments.GetProperty("score").GetInt32());
            Assert.Equal("ready", request.Arguments.GetProperty("summary").GetString());
            Assert.Equal("document", request.Arguments.GetProperty("literal").GetString());
            Assert.Equal(3, request.Arguments.EnumerateObject().Count());
            var json = JsonSerializer.Serialize(request);
            Assert.DoesNotContain("must-not-be-exported", json);
            Assert.DoesNotContain("trn:", json);
            Assert.DoesNotContain("Namespaces", json);
            Assert.Equal("connection/v1", request.ConnectionRevision);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Input_Payloads_Take_Precedence_Over_Inline_Values(bool artifact)
        {
            using var fixture = await CreateAsync(McpStepTestSupport.Pipeline(Step(input: new Dictionary<string, object?> { ["document"] = "inline" })));
            fixture.Payloads.Artifacts["blob-1"] = "materialized";
            fixture.Context.StepState.InputPayloads = new()
            {
                ["document"] = artifact ? AiStoredPayload.Artifact("blob-1") : AiStoredPayload.Inline("materialized")
            };
            await fixture.InvokeAsync();
            Assert.Equal("materialized", Assert.Single(fixture.Transport.Calls).Arguments.GetProperty("document").GetString());
            Assert.Equal(1, fixture.Payloads.Calls);
        }

        [Fact]
        public async Task Input_Keys_Cannot_Overwrite_The_Trusted_Request_Identity()
        {
            var input = new Dictionary<string, object?> { ["tenantId"] = "admin", ["executionId"] = "foreign" };
            using var fixture = await CreateAsync(McpStepTestSupport.Pipeline(Step(input: input)));
            await fixture.InvokeAsync();
            var request = Assert.Single(fixture.Transport.Calls);
            Assert.Equal("admin", request.Arguments.GetProperty("tenantId").GetString());
            Assert.Equal("tenant-1", request.Context.TenantId);
            Assert.Equal("execution-1", request.Context.ExecutionId);
        }

        [Fact]
        public async Task Missing_Input_Artifact_Fails_Before_The_Tool_Is_Invoked()
        {
            using var fixture = await CreateAsync();
            fixture.Context.StepState.InputPayloads = new() { ["document"] = AiStoredPayload.Artifact("missing") };
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InvokeAsync());
            Assert.Equal(1, fixture.Payloads.Calls);
            Assert.Empty(fixture.Transport.Calls);
        }

        [Fact]
        public async Task Existing_Unresolved_Path_Fallback_Is_Preserved()
        {
            using var fixture = await CreateAsync(McpStepTestSupport.Pipeline(Step(input: new Dictionary<string, object?> { ["path"] = "state.missing" })));
            await fixture.InvokeAsync();
            Assert.Equal("state.missing", Assert.Single(fixture.Transport.Calls).Arguments.GetProperty("path").GetString());
        }

        [Fact]
        public async Task Transport_Receives_A_Detached_Input_Snapshot()
        {
            var nested = new Dictionary<string, object?> { ["value"] = "before" };
            var transport = new Transport { Handler = (request, _) =>
            {
                nested["value"] = "after";
                Assert.Equal("before", request.Arguments.GetProperty("nested").GetProperty("value").GetString());
                return Task.FromResult(Response(request));
            } };
            using var fixture = await CreateAsync(McpStepTestSupport.Pipeline(Step(input: new Dictionary<string, object?> { ["nested"] = nested })), transport: transport);
            await fixture.InvokeAsync();
            Assert.Equal("before", Assert.Single(transport.Calls).Arguments.GetProperty("nested").GetProperty("value").GetString());
        }

        [Theory]
        [InlineData("object")]
        [InlineData("nan")]
        [InlineData("infinity")]
        public async Task Arbitrary_Clr_Values_And_Non_Finite_Numbers_Are_Not_Serialized(string kind)
        {
            var dangerous = new DangerousObject();
            object value = kind switch { "nan" => double.NaN, "infinity" => float.PositiveInfinity, _ => dangerous };
            using var fixture = await CreateAsync(McpStepTestSupport.Pipeline(Step(input: new Dictionary<string, object?> { ["value"] = value })));
            await Assert.ThrowsAsync<NotSupportedException>(() => fixture.InvokeAsync());
            Assert.Equal(0, dangerous.Reads); Assert.Empty(fixture.Transport.Calls);
        }

        [Theory]
        [InlineData("depth")]
        [InlineData("size")]
        [InlineData("undefined")]
        public async Task Invalid_Or_Excessive_Json_Does_Not_Reach_The_Transport(string kind)
        {
            object value;
            if (kind == "size") value = new string('x', 65536);
            else if (kind == "undefined") value = new Dictionary<string, object?> { ["bad"] = default(JsonElement) };
            else
            {
                var cycle = new List<object?>(); cycle.Add(cycle); value = cycle;
            }
            using var fixture = await CreateAsync(McpStepTestSupport.Pipeline(Step(input: new Dictionary<string, object?> { ["value"] = value })));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.InvokeAsync());
            Assert.Empty(fixture.Transport.Calls);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task Tool_Result_Has_Stable_Value_Output_Data_And_Outcome(bool error, bool structured)
        {
            var transport = new Transport { Handler = (request, _) => Task.FromResult(Response(request, error, structured)) };
            using var fixture = await CreateAsync(transport: transport);
            var result = await fixture.InvokeAsync();
            Assert.Equal(!error, result.Success);
            Assert.Equal(error ? AiStepExecutionOutcome.Fail : AiStepExecutionOutcome.Complete, result.EffectiveOutcome);
            Assert.Equal(error ? "tool refused" : "published", result.Output);
            Assert.Equal(error ? "tool refused" : null, result.Error);
            Assert.Null(result.Payload); Assert.Null(result.DataPayloads);
            var primary = Assert.IsType<JsonElement>(result.Value);
            Assert.Equal(structured ? JsonValueKind.Object : JsonValueKind.Array, primary.ValueKind);
            Assert.Equal(1, Assert.IsType<JsonElement>(result.Data["content"]).GetArrayLength());
            Assert.Equal(structured, result.Data.ContainsKey("structuredContent"));
            var roundtrip = JsonSerializer.Deserialize<AiStepResult>(JsonSerializer.Serialize(result))!;
            Assert.Equal(result.EffectiveOutcome, roundtrip.EffectiveOutcome);
            Assert.Equal(result.Output, roundtrip.Output);
            Assert.Equal(result.Error, roundtrip.Error);
            Assert.Equal(primary.GetRawText(), Assert.IsType<JsonElement>(roundtrip.Value).GetRawText());
            Assert.Single(transport.Calls);
        }

        [Fact]
        public async Task Content_Links_Remain_Data_And_Do_Not_Become_Fetches_Or_Payload_References()
        {
            var transport = new Transport { Handler = (request, _) => Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                schemaVersion = 1, requestId = request.RequestId, isError = false,
                content = new object[] { new { type = "resource_link", uri = "file:///not-fetched", name = "document" } }
            })) };
            using var fixture = await CreateAsync(transport: transport);
            var result = await fixture.InvokeAsync();
            Assert.True(result.Success); Assert.Null(result.Output); Assert.Null(result.Payload);
            Assert.Equal("file:///not-fetched", Assert.IsType<JsonElement>(result.Value)[0].GetProperty("uri").GetString());
            Assert.Equal(0, fixture.Payloads.Calls); Assert.Single(transport.Calls);
        }

        [Fact]
        public async Task Empty_Tool_Error_Content_Does_Not_Become_Success()
        {
            var transport = new Transport { Handler = (request, _) => Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                schemaVersion = 1, requestId = request.RequestId, isError = true, content = Array.Empty<object>()
            })) };
            using var fixture = await CreateAsync(transport: transport);
            var result = await fixture.InvokeAsync();
            Assert.False(result.Success); Assert.Equal(AiStepExecutionOutcome.Fail, result.EffectiveOutcome);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }

        [Fact]
        public async Task Result_Detaches_From_The_Transport_Json_Document()
        {
            JsonDocument? source = null;
            var transport = new Transport { Handler = (request, _) =>
            {
                source = JsonDocument.Parse(Response(request).GetRawText());
                return Task.FromResult(source.RootElement);
            } };
            using var fixture = await CreateAsync(transport: transport);
            try
            {
                var result = await fixture.InvokeAsync();
                source!.Dispose();
                Assert.Equal(42, Assert.IsType<JsonElement>(result.Value).GetProperty("id").GetInt32());
            }
            finally { source?.Dispose(); }
        }

        [Fact]
        public async Task Existing_Claimed_Step_Executor_Calls_The_Mcp_Adapter_And_Compacts_Its_Result()
        {
            using var fixture = await CreateAsync();
            var compactor = new Ml1bInvocationTestSupport.ProbeCompactor();
            var identity = Ml1bPropertyProxy.For<IAiRuntimeInstanceIdentityDescriptor>(new Dictionary<string, object?>
            {
                ["get_RuntimeInstanceId"] = "runtime-1"
            });
            var services = Ml1bPropertyProxy.For<IAiDagExecutionEngineServices>(new Dictionary<string, object?>
            {
                ["get_RuntimeInstanceIdentity"] = identity,
                ["get_ObservabilityService"] = new Ml1bInvocationTestSupport.TestObservability(),
                ["get_PayloadCompactor"] = compactor
            });
            var executor = new AiDagClaimedStepExecutor(services);
            var result = await fixture.WithLiveAsync(fixture.Live, () => executor.ExecuteAsync(
                fixture.Context.Record, fixture.Context.State, fixture.Plan,
                new AiClaimedStep { ExecutionId = fixture.Context.ExecutionId, StepName = "publish", ClaimToken = "claim-1" },
                (_, _, _) => fixture.Context.Execution));
            Assert.True(result.Success); Assert.Equal(1, compactor.Calls);
            Assert.Single(fixture.Transport.Calls);
            Assert.Equal("publish", Assert.Single(fixture.Transport.Calls).Context.StepName);
        }

        private sealed class DangerousObject
        {
            public int Reads;
            public string Secret { get { Reads++; throw new InvalidOperationException("Must not reflect over this object."); } }
        }
    }
}
