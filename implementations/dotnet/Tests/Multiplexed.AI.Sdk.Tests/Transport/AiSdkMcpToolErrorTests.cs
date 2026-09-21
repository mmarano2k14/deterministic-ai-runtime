using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Multiplexed.AI.Sdk.Errors;
using Multiplexed.AI.Sdk.Transport;

namespace Multiplexed.AI.Sdk.Tests.Transport
{
    /// <summary>Checks bounded diagnostics from one MCP tool-error response, without a server or engine reference.</summary>
    public sealed class AiSdkMcpToolErrorTests
    {
        [Fact]
        public void Remote_Tool_Error_Preserves_Classification_Without_Enabling_Retry()
        {
            var response = Normalize(new CallToolResult { IsError = true, Content = [] });
            Assert.False(response.IsSuccess);
            Assert.NotNull(response.Error);
            Assert.Equal(AiSdkErrorKind.RemoteFailure, response.Error.Kind);
            Assert.Equal("remote_tool_error", response.Error.Code);
            Assert.False(response.Error.IsRetryable);
            Assert.Equal("The remote SDK operation 'sdk.publish_pipeline' returned an error.", response.Error.Message);
        }

        [Fact]
        public void Remote_Tool_Error_Preserves_Nonempty_Text_In_Order()
        {
            var error = Normalize(new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "first" },
                    new TextContentBlock { Text = " " }, new TextContentBlock { Text = "second" }]
            }).Error!;
            Assert.Equal(new[] { "first", "second" },
                error.Details["remoteContent"].EnumerateArray().Select(item => item.GetString()).ToArray());
        }

        [Fact]
        public void Remote_Tool_Error_Bounds_Text_Count_And_Length()
        {
            var result = new CallToolResult
            {
                IsError = true,
                Content = [.. Enumerable.Range(0, 12).Select(_ =>
                    new TextContentBlock { Text = new string('x', 5000) })]
            };
            var content = Normalize(result).Error!.Details["remoteContent"];
            Assert.Equal(8, content.GetArrayLength());
            Assert.All(content.EnumerateArray(), item => Assert.Equal(4096, item.GetString()!.Length));
        }

        [Fact]
        public void Remote_Tool_Error_Clones_Structured_Content_Before_Response_Disposal()
        {
            AiSdkTransportResponse response;
            using (var document = JsonDocument.Parse("{\"reason\":\"publication refused\"}"))
            {
                response = Normalize(new CallToolResult
                {
                    IsError = true, Content = [], StructuredContent = document.RootElement
                });
            }
            Assert.Equal("publication refused",
                response.Error!.Details["remoteStructuredContent"].GetProperty("reason").GetString());
        }

        [Fact]
        public void Remote_Tool_Error_Omits_Oversized_Structured_Content()
        {
            var result = new CallToolResult
            {
                IsError = true, Content = [new TextContentBlock { Text = "bounded text remains" }],
                StructuredContent = JsonSerializer.SerializeToElement(new { reason = new string('x', 8193) })
            };
            var error = Normalize(result).Error!;
            Assert.False(error.Details.ContainsKey("remoteStructuredContent"));
            Assert.Equal("bounded text remains", error.Details["remoteContent"][0].GetString());
        }

        [Theory]
        [InlineData("[]")]
        [InlineData("null")]
        [InlineData("42")]
        public void Remote_Tool_Error_Omits_Nonobject_Structured_Content(string json)
        {
            using var document = JsonDocument.Parse(json);
            var response = Normalize(new CallToolResult
            {
                IsError = true, Content = [], StructuredContent = document.RootElement
            });
            Assert.Empty(response.Error!.Details);
        }

        [Fact]
        public void Remote_Tool_Error_Without_Diagnostic_Content_Remains_An_Error()
        {
            var response = Normalize(new CallToolResult { IsError = true, Content = [] });
            Assert.False(response.IsSuccess);
            Assert.Empty(response.Error!.Details);
        }

        [Fact]
        public void Remote_Tool_Error_With_Null_Content_Does_Not_Lose_Structured_Diagnostic()
        {
            var response = Normalize(new CallToolResult
            {
                IsError = true, Content = null!,
                StructuredContent = JsonSerializer.SerializeToElement(new { message = "refused" })
            });
            Assert.Equal("refused",
                response.Error!.Details["remoteStructuredContent"].GetProperty("message").GetString());
        }

        private static AiSdkTransportResponse Normalize(CallToolResult result)
        {
            // Keep normalization private; avoid exposing a diagnostic-only SDK public contract.
            var method = typeof(AiSdkMcpHttpTransport).GetMethod("CreateRemoteToolFailure",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            return Assert.IsType<AiSdkTransportResponse>(method.Invoke(null, ["sdk.publish_pipeline", result]));
        }
    }
}
