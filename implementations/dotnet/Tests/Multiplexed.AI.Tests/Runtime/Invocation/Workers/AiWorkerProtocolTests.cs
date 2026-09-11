using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Multiplexed.AI.Runtime.Invocation.Workers;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers
{
    /// <summary>Closed framing, bounded bytes and assignment correlation without external processes.</summary>
    public sealed class AiWorkerProtocolTests
    {
        [Theory]
        [InlineData("ready")]
        [InlineData("heartbeat")]
        [InlineData("result")]
        public void Accepts_Only_Known_Correlated_Frames(string type)
        {
            var request = WorkerTestSupport.Request();
            var frame = AiWorkerInvocationProtocol.ReadFrame(WorkerTestSupport.Frame(request, type), request);
            Assert.Equal(type, frame.Type); Assert.Equal(type == "result", frame.Result is not null);
        }
        [Theory]
        [InlineData("requestId")]
        [InlineData("operationId")]
        [InlineData("workerId")]
        public void Rejects_Foreign_Assignment_Strings(string field)
        {
            var request = WorkerTestSupport.Request(); var frame = JsonNode.Parse(WorkerTestSupport.Frame(request))!;
            frame[field] = "other";
            Assert.Throws<InvalidOperationException>(() => AiWorkerInvocationProtocol.ReadFrame(frame.ToJsonString(), request));
        }
        [Theory]
        [InlineData("epoch", 2)]
        [InlineData("protocolVersion", 2)]
        public void Rejects_Foreign_Epoch_Or_Protocol(string field, int value)
        {
            var request = WorkerTestSupport.Request(); var frame = JsonNode.Parse(WorkerTestSupport.Frame(request))!;
            frame[field] = value;
            Assert.Throws<InvalidOperationException>(() => AiWorkerInvocationProtocol.ReadFrame(frame.ToJsonString(), request));
        }
        [Theory]
        [InlineData("park")]
        [InlineData("retry")]
        [InlineData("next")]
        [InlineData("grants")]
        [InlineData("continuation")]
        [InlineData("leaseToken")]
        public void Extra_Envelope_Commands_Are_Refused(string field)
        {
            var request = WorkerTestSupport.Request(); var frame = JsonNode.Parse(WorkerTestSupport.Frame(request))!;
            frame[field] = true;
            Assert.Throws<InvalidOperationException>(() => AiWorkerInvocationProtocol.ReadFrame(frame.ToJsonString(), request));
        }
        [Theory]
        [InlineData("type")]
        [InlineData("requestId")]
        [InlineData("workerId")]
        [InlineData("operationId")]
        [InlineData("protocolVersion")]
        [InlineData("epoch")]
        [InlineData("success")]
        [InlineData("payload")]
        public void Required_Envelope_Fields_Cannot_Be_Omitted(string field)
        {
            var request = WorkerTestSupport.Request(); var frame = JsonNode.Parse(WorkerTestSupport.Frame(request))!.AsObject();
            frame.Remove(field);
            Assert.Throws<InvalidOperationException>(() => AiWorkerInvocationProtocol.ReadFrame(frame.ToJsonString(), request));
        }
        [Theory]
        [InlineData("\"true\"")]
        [InlineData("1")]
        [InlineData("null")]
        [InlineData("{}")]
        public void Success_Is_An_Explicit_Boolean(string json)
        {
            var request = WorkerTestSupport.Request(); var frame = JsonNode.Parse(WorkerTestSupport.Frame(request))!;
            frame["success"] = JsonNode.Parse(json);
            Assert.Throws<InvalidOperationException>(() => AiWorkerInvocationProtocol.ReadFrame(frame.ToJsonString(), request));
        }
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Business_Result_Is_Not_Confused_With_Transport_Status(bool success)
        {
            var request = WorkerTestSupport.Request();
            var result = AiWorkerInvocationProtocol.ReadFrame(WorkerTestSupport.Frame(request, success: success), request).Result!;
            Assert.Equal(success, result.Success); Assert.Equal("{\"value\":42}", result.PayloadJson);
        }
        [Fact]
        public void Duplicate_Envelope_Field_Is_Refused()
        {
            var request = WorkerTestSupport.Request(); var frame = WorkerTestSupport.Frame(request);
            frame = "{\"success\":false," + frame[1..];
            Assert.Throws<InvalidOperationException>(() => AiWorkerInvocationProtocol.ReadFrame(frame, request));
        }
        [Fact]
        public void Duplicate_Payload_Member_Is_Refused()
        {
            var request = WorkerTestSupport.Request(); var frame = WorkerTestSupport.Frame(request).Replace("{\"value\":42}", "{\"value\":1,\"value\":2}");
            Assert.Throws<InvalidOperationException>(() => AiWorkerInvocationProtocol.ReadFrame(frame, request));
        }
        [Fact]
        public void Invalid_Json_Remains_A_Json_Failure()
        { Assert.ThrowsAny<JsonException>(() => AiWorkerInvocationProtocol.ReadFrame("{", WorkerTestSupport.Request())); }
        [Fact]
        public void Payload_Cannot_Exceed_The_Existing_Journal_Inline_Limit()
        {
            var request = WorkerTestSupport.Request();
            Assert.Throws<InvalidOperationException>(() => AiWorkerInvocationProtocol.ReadFrame(
                WorkerTestSupport.Frame(request, payload: new string('x', 262145)), request));
        }
        [Fact]
        public void Request_Contains_Only_Explicit_Portable_Projection()
        {
            var encoded = Encoding.UTF8.GetString(AiWorkerInvocationProtocol.EncodeRequest(WorkerTestSupport.Request(), 1048576));
            using var json = JsonDocument.Parse(encoded);
            Assert.Equal("invoke", json.RootElement.GetProperty("type").GetString());
            Assert.Equal("Y29kZS0x", json.RootElement.GetProperty("code").GetProperty("sources")[0].GetProperty("base64Url").GetString());
            foreach (var field in new[] { "leaseToken", "executionContextSnapshot", "permissions", "serviceProvider", "connectionString", "payloadStore", "contextKey" })
                Assert.False(encoded.Contains(field, StringComparison.OrdinalIgnoreCase));
        }
        [Fact]
        public void Encoded_Request_Limit_Is_Enforced()
        { Assert.Throws<InvalidOperationException>(() => AiWorkerInvocationProtocol.EncodeRequest(WorkerTestSupport.Request(), 1024)); }
        [Theory]
        [InlineData("{}\n", "{}")]
        [InlineData("{}\r\n", "{}")]
        [InlineData("{\"x\":\"é\"}\n", "{\"x\":\"é\"}")]
        public async Task Reader_Handles_Utf8_And_Line_Endings(string wire, string expected)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(wire)); var reader = new AiWorkerJsonLineReader(stream, 100);
            Assert.Equal(expected, await reader.ReadAsync()); Assert.Null(await reader.ReadAsync());
        }
        [Fact]
        public async Task Reader_Preserves_Remaining_Frames_In_The_Same_Buffer()
        {
            using var stream = new MemoryStream("{}\n[]\n"u8.ToArray()); var reader = new AiWorkerJsonLineReader(stream, 2);
            Assert.Equal("{}", await reader.ReadAsync()); Assert.Equal("[]", await reader.ReadAsync()); Assert.Null(await reader.ReadAsync());
        }
        [Theory]
        [InlineData("{}")]
        [InlineData("\n")]
        [InlineData("\r\n")]
        [InlineData("xxxx\n")]
        public async Task Reader_Rejects_Unterminated_Empty_Or_Oversized_Frames(string wire)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(wire)); var reader = new AiWorkerJsonLineReader(stream, 3);
            await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync());
        }
        [Fact]
        public async Task Reader_Rejects_Invalid_Utf8_Instead_Of_Replacing_Bytes()
        {
            using var stream = new MemoryStream(new byte[] { 0xc3, 0x28, 10 }); var reader = new AiWorkerJsonLineReader(stream, 100);
            await Assert.ThrowsAsync<DecoderFallbackException>(() => reader.ReadAsync());
        }
        [Fact]
        public async Task Reader_Applies_Its_Bound_Across_Buffer_Boundaries()
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 5001) + "\n"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => new AiWorkerJsonLineReader(stream, 5000).ReadAsync());
        }
        [Fact]
        public async Task Cancelled_Read_Does_Not_Consume_A_Frame()
        {
            using var stream = new MemoryStream("{}\n"u8.ToArray()); var reader = new AiWorkerJsonLineReader(stream, 100);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(new CancellationToken(true)));
            Assert.Equal("{}", await reader.ReadAsync());
        }
    }
}
