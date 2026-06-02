using System.Net.Http.Json;
using System.Text.Json;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures
{
    /// <summary>
    /// Provides a lightweight JSON-RPC MCP client for integration tests.
    /// </summary>
    public sealed class McpJsonRpcClient
    {
        private readonly HttpClient httpClient;

        public McpJsonRpcClient(
            HttpClient httpClient)
        {
            this.httpClient = httpClient
                ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public async Task<string> ListToolsAsync(
            CancellationToken cancellationToken = default)
        {
            return await PostJsonRpcAsync(
                new
                {
                    jsonrpc = "2.0",
                    id = "1",
                    method = "tools/list"
                },
                cancellationToken);
        }

        public async Task<string> CallToolAsync(
            string toolName,
            object? arguments = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

            return await PostJsonRpcAsync(
                new
                {
                    jsonrpc = "2.0",
                    id = Guid.NewGuid().ToString("N"),
                    method = "tools/call",
                    @params = new
                    {
                        name = toolName,
                        arguments = arguments ?? new { }
                    }
                },
                cancellationToken);
        }

        public async Task<JsonDocument> CallToolAsJsonAsync(
            string toolName,
            object? arguments = null,
            CancellationToken cancellationToken = default)
        {
            var json = await CallToolAsync(
                toolName,
                arguments,
                cancellationToken);

            return JsonDocument.Parse(json);
        }

        private async Task<string> PostJsonRpcAsync(
            object payload,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "/mcp")
            {
                Content = JsonContent.Create(payload)
            };

            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");

            var response = await httpClient.SendAsync(
                request,
                cancellationToken);

            var content = await response.Content.ReadAsStringAsync(
                cancellationToken);

            response.EnsureSuccessStatusCode();

            return content;
        }
    }
}