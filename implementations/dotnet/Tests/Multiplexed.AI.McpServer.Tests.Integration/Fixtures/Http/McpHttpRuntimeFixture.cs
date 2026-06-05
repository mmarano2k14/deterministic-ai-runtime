namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Http
{
    /// <summary>
    /// Provides a two-host integration fixture for HTTP runtime provider scenarios.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This fixture starts two independent test hosts:
    /// </para>
    ///
    /// <list type="number">
    /// <item>
    /// <description>
    /// An MCP control-plane host exposing <c>/mcp</c> and configured with
    /// <c>ControlPlaneWithHttpRuntimeInstances</c>.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// A runtime-instance-only host exposing <c>/runtime-instance/commands</c>.
    /// </description>
    /// </item>
    /// </list>
    /// </remarks>
    public sealed class McpHttpRuntimeFixture : IAsyncLifetime
    {
        /// <summary>
        /// Gets the MCP control-plane host.
        /// </summary>
        public McpHttpRuntimeTestHost? ControlPlaneHost { get; private set; }

        /// <summary>
        /// Gets the runtime-instance-only HTTP host.
        /// </summary>
        public RuntimeInstanceHttpTestHost? RuntimeHost { get; private set; }

        /// <summary>
        /// Gets the HTTP client used to call the MCP control-plane host.
        /// </summary>
        public HttpClient? ControlPlaneClient { get; private set; }

        /// <summary>
        /// Gets the HTTP client used to call the runtime-instance-only host.
        /// </summary>
        public HttpClient? RuntimeClient { get; private set; }

        /// <summary>
        /// Gets the JSON-RPC MCP test client.
        /// </summary>
        public McpTestClient Mcp { get; private set; } = default!;

        /// <inheritdoc />
        public Task InitializeAsync()
        {
            RuntimeHost =
                new RuntimeInstanceHttpTestHost();

            RuntimeClient =
                RuntimeHost.CreateClient();

            ControlPlaneHost =
                new McpHttpRuntimeTestHost();

            ControlPlaneClient =
                ControlPlaneHost.CreateClient();

            Mcp =
                new McpTestClient(
                    ControlPlaneClient);

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task DisposeAsync()
        {
            ControlPlaneClient?.Dispose();
            RuntimeClient?.Dispose();

            if (ControlPlaneHost is not null)
            {
                await ControlPlaneHost
                    .DisposeAsync()
                    .ConfigureAwait(false);
            }

            if (RuntimeHost is not null)
            {
                await RuntimeHost
                    .DisposeAsync()
                    .ConfigureAwait(false);
            }
        }
    }
}