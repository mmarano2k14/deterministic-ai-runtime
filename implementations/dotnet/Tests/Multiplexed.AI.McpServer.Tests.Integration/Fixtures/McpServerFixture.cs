namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures
{
    /// <summary>
    /// Provides a shared MCP server host and HTTP client for integration tests.
    /// </summary>
    public sealed class McpServerFixture : IAsyncLifetime
    {
        /// <summary>
        /// Gets the MCP server test host.
        /// </summary>
        public McpServerTestHost Host { get; private set; } = default!;

        /// <summary>
        /// Gets the HTTP client used to call the MCP server.
        /// </summary>
        public HttpClient Client { get; private set; } = default!;

        /// <summary>
        /// Gets the JSON-RPC MCP client.
        /// </summary>
        public McpTestClient Mcp { get; private set; } = default!;


        /// <inheritdoc />
        public Task InitializeAsync()
        {
            Host = new McpServerTestHost();

            Client = Host.CreateClient();

            Mcp = new McpTestClient(Client);

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task DisposeAsync()
        {
            Client.Dispose();

            await Host.DisposeAsync().ConfigureAwait(false);
        }
    }
}