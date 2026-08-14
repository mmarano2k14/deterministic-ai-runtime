using System.Net;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures
{
    /// <summary>
    /// Verifies access-context rotation behavior in the MCP integration client.
    /// </summary>
    public sealed class McpTestClientRotationTests
    {
        [Fact]
        public async Task ListToolsAsync_Should_Use_Rotated_AccessContext_On_Next_Request()
        {
            var handler =
                new RotatingAccessContextHandler();

            using var httpClient =
                new HttpClient(handler)
                {
                    BaseAddress =
                        new Uri("http://localhost")
                };

            var mcp =
                new McpTestClient(httpClient);

            mcp.SetRbacHeaders(
                "X-Access-Context",
                "ctx-original",
                "test-user");

            await mcp
                .ListToolsAsync()
                .ConfigureAwait(false);

            await mcp
                .ListToolsAsync()
                .ConfigureAwait(false);

            Assert.Equal(
                new[]
                {
                    "ctx-original",
                    "ctx-rotated"
                },
                handler.ObservedContextKeys);
        }

        private sealed class RotatingAccessContextHandler :
            HttpMessageHandler
        {
            private readonly List<string>
                observedContextKeys = new();

            public IReadOnlyList<string>
                ObservedContextKeys =>
                    observedContextKeys;

            protected override Task<HttpResponseMessage>
                SendAsync(
                    HttpRequestMessage request,
                    CancellationToken cancellationToken)
            {
                var contextKey =
                    request.Headers
                        .GetValues(
                            "X-Access-Context")
                        .Single();

                observedContextKeys.Add(
                    contextKey);

                var response =
                    new HttpResponseMessage(
                        HttpStatusCode.OK)
                    {
                        Content =
                            new StringContent(
                                "{\"jsonrpc\":\"2.0\",\"id\":\"test\",\"result\":{}}")
                    };

                if (observedContextKeys.Count == 1)
                {
                    response.Headers.TryAddWithoutValidation(
                        "X-Access-Context",
                        "ctx-rotated");
                }

                return Task.FromResult(
                    response);
            }
        }
    }
}
