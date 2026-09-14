using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.AI.Runtime.Invocation.Mcp;

namespace Multiplexed.AI.Tests.Runtime.Invocation.OutboundMcp
{
    /// <summary>Logical identity is independent of attempts; intent changes stay visible under the same effect id.</summary>
    public sealed class AiMcpEffectIdentityTests
    {
        [Fact]
        public void Golden_Vector_Freezes_The_Ordinal_Canonical_Hash_Contract()
        {
            var effect = AiMcpEffectIdentities.Create(Request());
            Assert.Equal(1, effect.SchemaVersion);
            Assert.Equal("mcp-effect-v1-031f16d7b3adad22ea86bea4d4aa1389f44fe0060ef76d50bb6b90ea509836a6", effect.EffectId);
            Assert.Equal("sha256:cc0164cfa2c2ec7b72bb1a788de3988fd068af74de482b085483ad15b093d650", effect.RequestDigest);
        }

        [Fact]
        public void Attempt_Deadline_And_Envelope_Version_Are_Not_Logical_Identity()
        {
            var request = Request();
            var changed = request with { RequestId = "new-attempt", DeadlineUtc = request.DeadlineUtc.AddHours(1), SchemaVersion = 1 };
            Assert.Equal(AiMcpEffectIdentities.Create(request), AiMcpEffectIdentities.Create(changed));
        }

        [Theory]
        [InlineData("tenant")]
        [InlineData("group")]
        [InlineData("execution")]
        [InlineData("step")]
        public void A_Different_Logical_Scope_Produces_A_Different_Effect(string field)
        {
            var request = Request();
            var changed = request with { Context = field switch
            {
                "tenant" => request.Context with { TenantId = "tenant-b" },
                "group" => request.Context with { TenantGroupId = "group-b" },
                "execution" => request.Context with { ExecutionId = "execution-b" },
                _ => request.Context with { StepName = "another-call-site" }
            } };
            var before = AiMcpEffectIdentities.Create(request);
            var after = AiMcpEffectIdentities.Create(changed);
            Assert.NotEqual(before.EffectId, after.EffectId);
            Assert.NotEqual(before.RequestDigest, after.RequestDigest);
        }

        [Theory]
        [InlineData("arguments")]
        [InlineData("connection")]
        [InlineData("revision")]
        [InlineData("tool")]
        [InlineData("pipeline")]
        [InlineData("version")]
        [InlineData("step-key")]
        public void Changed_Intent_Does_Not_Silently_Create_A_New_Logical_Effect(string field)
        {
            var request = Request();
            var changed = field switch
            {
                "arguments" => request with { Arguments = Json("{\"a\":2,\"z\":2}") },
                "connection" => request with { ConnectionRef = "connection-b" },
                "revision" => request with { ConnectionRevision = "revision-2" },
                "tool" => request with { Tool = "probe.other" },
                "pipeline" => request with { Context = request.Context with { PipelineName = "another" } },
                "version" => request with { Context = request.Context with { PipelineVersion = "2" } },
                _ => request with { Context = request.Context with { StepKey = "another-key" } }
            };
            var before = AiMcpEffectIdentities.Create(request);
            var after = AiMcpEffectIdentities.Create(changed);
            Assert.Equal(before.EffectId, after.EffectId);
            Assert.NotEqual(before.RequestDigest, after.RequestDigest);
        }

        [Fact]
        public void Recursive_Object_Order_Whitespace_And_String_Escapes_Are_Normalized()
        {
            var first = Request("""{ "z": [{"y":2,"x":1}], "a":"\u0061" }""");
            var second = Request("""{"a":"a","z":[{"x":1,"y":2}]}""");
            Assert.Equal(AiMcpEffectIdentities.Create(first), AiMcpEffectIdentities.Create(second));
        }

        [Fact]
        public void Array_Order_Is_Part_Of_The_Intent()
        {
            Assert.NotEqual(AiMcpEffectIdentities.Create(Request("{\"items\":[1,2]}")).RequestDigest,
                AiMcpEffectIdentities.Create(Request("{\"items\":[2,1]}")).RequestDigest);
        }

        [Theory]
        [InlineData("1.0")]
        [InlineData("1e0")]
        public void Numeric_Lexemes_Are_Not_Claimed_To_Be_Rfc8785_Equivalent(string number)
        {
            Assert.NotEqual(AiMcpEffectIdentities.Create(Request("{\"value\":1}")).RequestDigest,
                AiMcpEffectIdentities.Create(Request("{\"value\":" + number + "}")).RequestDigest);
        }

        [Theory]
        [InlineData("{\"a\":1,\"a\":2}")]
        [InlineData("{\"nested\":{\"a\":1,\"a\":2}}")]
        public void Duplicate_Argument_Properties_Are_Refused(string json)
        {
            Assert.Throws<InvalidOperationException>(() => AiMcpEffectIdentities.Create(Request(json)));
        }

        [Fact]
        public void Oversized_Arguments_Are_Refused_At_The_Existing_Mcp_Boundary()
        {
            var request = Request() with { Arguments = JsonSerializer.SerializeToElement(new { value = new string('x', 65537) }) };
            Assert.Throws<InvalidOperationException>(() => AiMcpEffectIdentities.Create(request));
        }

        [Theory]
        [InlineData("[]")]
        [InlineData("null")]
        public void Arguments_Must_Be_An_Object(string json)
        {
            Assert.Throws<InvalidOperationException>(() => AiMcpEffectIdentities.Create(Request(json)));
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, true)]
        [InlineData(2, false)]
        [InlineData(3, false)]
        [InlineData(3, true)]
        public void Unknown_Incomplete_Or_Ambiguous_Envelope_Versions_Are_Refused(int schema, bool includeEffect)
        {
            var request = Request() with { SchemaVersion = schema };
            if (includeEffect) request = request with { Effect = AiMcpEffectIdentities.Create(request) };
            Assert.Throws<InvalidOperationException>(() => AiMcpEffectIdentities.ValidateRequest(request));
        }

        [Fact]
        public void Legacy_Envelope_Remains_Readable_Without_A_New_Null_Json_Property()
        {
            var request = Request() with { SchemaVersion = 1 };
            AiMcpEffectIdentities.ValidateRequest(request);
            var json = JsonSerializer.Serialize(request);
            Assert.DoesNotContain("\"effect\"", json);
            var restored = JsonSerializer.Deserialize<AiMcpToolRequest>(json)!;
            Assert.Null(restored.Effect);
            Assert.Equal(1, restored.SchemaVersion);
            AiMcpEffectIdentities.ValidateRequest(restored);
        }

        [Fact]
        public void Effect_Envelope_Round_Trips_Without_Changing_Identity_Or_Intent()
        {
            var request = WithEffect(Request());
            var restored = JsonSerializer.Deserialize<AiMcpToolRequest>(JsonSerializer.Serialize(request))!;
            Assert.Equal(request.Effect, restored.Effect);
            Assert.Equal(request.ConnectionRevision, restored.ConnectionRevision);
            AiMcpEffectIdentities.ValidateRequest(restored);
        }

        [Theory]
        [InlineData("schema")]
        [InlineData("effect")]
        [InlineData("digest")]
        public void Caller_Supplied_Effect_Metadata_Is_Recomputed_Not_Trusted(string field)
        {
            var request = WithEffect(Request());
            request = request with { Effect = field switch
            {
                "schema" => request.Effect! with { SchemaVersion = 2 },
                "effect" => request.Effect! with { EffectId = "mcp-effect-v1-" + new string('0', 64) },
                _ => request.Effect! with { RequestDigest = "sha256:" + new string('0', 64) }
            } };
            Assert.Throws<InvalidOperationException>(() => AiMcpEffectIdentities.ValidateRequest(request));
        }

        [Fact]
        public void Scope_Encoding_Does_Not_Collapse_Concatenation_Boundaries()
        {
            var request = Request();
            var first = request with { Context = request.Context with { TenantId = "ab", ExecutionId = "c" } };
            var second = request with { Context = request.Context with { TenantId = "a", ExecutionId = "bc" } };
            Assert.NotEqual(AiMcpEffectIdentities.Create(first).EffectId, AiMcpEffectIdentities.Create(second).EffectId);
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("tenant\nname")]
        public void Incomplete_Or_Control_Character_Identity_Is_Refused(string tenant)
        {
            var request = Request();
            request = request with { Context = request.Context with { TenantId = tenant } };
            Assert.Throws<InvalidOperationException>(() => AiMcpEffectIdentities.Create(request));
        }

        [Fact]
        public void Invalid_Utf16_Identifier_Is_Not_Silently_Replaced_For_Hashing()
        {
            var request = Request();
            request = request with { Context = request.Context with { TenantId = "tenant-" + (char)0xD800 } };
            Assert.Throws<InvalidOperationException>(() => AiMcpEffectIdentities.Create(request));
        }

        internal static AiMcpToolRequest Request(string arguments = "{\"z\":2,\"a\":1}") => new(
            2, "attempt-1", DateTimeOffset.UtcNow.AddSeconds(20),
            new AiMcpToolInvocationContext("tenant-a", "group-a", "execution-a", "pipeline", "1", "publish", "mcp-tool"),
            "connection-a", "revision-1", "probe.echo", Json(arguments));

        internal static AiMcpToolRequest WithEffect(AiMcpToolRequest request) => request with
        { SchemaVersion = 2, Effect = AiMcpEffectIdentities.Create(request) };

        internal static JsonElement Json(string text)
        { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    }
}
