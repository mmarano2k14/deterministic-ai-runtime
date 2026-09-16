using System.Text.Json;
using Multiplexed.AI.Sdk.Contracts.Common;
using Multiplexed.AI.Sdk.Errors;
using Multiplexed.AI.Sdk.Transport;

namespace Multiplexed.AI.Sdk.Tests.Transport
{
    /// <summary>Validates fail-closed MCP transport behavior without requiring a live server.</summary>
    public sealed class AiSdkMcpHttpTransportTests
    {
        [Fact]
        public async Task Transport_Rejects_Unsupported_Protocol_Before_Network_Access()
        {
            var transport = new AiSdkMcpHttpTransport(new Uri("http://127.0.0.1:1/mcp"));
            using var document = JsonDocument.Parse("{\"executionId\":\"exec-1\"}");

            var response = await transport.InvokeAsync(new AiSdkTransportRequest
            {
                ProtocolVersion = AiSdkProtocolVersions.Current + 1,
                Operation = AiSdkOperationNames.ObserveExecution,
                Arguments = document.RootElement.Clone()
            });

            Assert.False(response.IsSuccess);
            Assert.Equal(AiSdkErrorKind.UnsupportedSchema, response.Error?.Kind);
            Assert.Equal("unsupported_protocol", response.Error?.Code);
        }

        [Fact]
        public async Task Transport_Rejects_Unknown_Operation_Before_Network_Access()
        {
            var transport = new AiSdkMcpHttpTransport(new Uri("http://127.0.0.1:1/mcp"));
            using var document = JsonDocument.Parse("{}");

            var response = await transport.InvokeAsync(new AiSdkTransportRequest
            {
                Operation = "sdk.unknown",
                Arguments = document.RootElement.Clone()
            });

            Assert.False(response.IsSuccess);
            Assert.Equal(AiSdkErrorKind.InvalidRequest, response.Error?.Kind);
            Assert.Equal("unknown_operation", response.Error?.Code);
        }

        [Fact]
        public void Transport_Requires_Absolute_Http_Endpoint()
        {
            Assert.Throws<ArgumentException>(() => new AiSdkMcpHttpTransport(new Uri("relative", UriKind.Relative)));
            Assert.Throws<ArgumentException>(() => new AiSdkMcpHttpTransport(new Uri("file:///tmp/mcp")));
        }

        [Fact]
        public void Transport_Rejects_Invalid_Retry_Configuration()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiSdkMcpHttpTransport(
                new Uri("https://runtime.example/mcp"),
                new AiSdkTransportOptions { SafeReadMaxAttempts = 0 }));
        }
    }
}
