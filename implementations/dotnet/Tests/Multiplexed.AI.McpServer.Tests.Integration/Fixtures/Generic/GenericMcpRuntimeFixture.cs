using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic
{
    /// <summary>
    /// Provides a generic multi-host integration fixture for runtime provider scenarios.
    /// </summary>
    /// <remarks>
    /// The fixture starts one MCP control-plane host and one or more runtime-instance-only
    /// HTTP hosts.
    ///
    /// The single-runtime constructor is preserved for existing tests. The multi-runtime
    /// constructor is used by provider-based dispatch tests that need to validate that
    /// several remote runtime instances can register, receive dispatches, execute local
    /// runs, and report capacity independently.
    /// </remarks>
    public sealed class GenericMcpRuntimeFixture : IAsyncLifetime
    {
        private readonly IReadOnlyDictionary<string, string?> controlPlaneSettings;
        private readonly IReadOnlyList<IReadOnlyDictionary<string, string?>> runtimeInstanceSettings;

        /// <summary>
        /// Gets the MCP control-plane host.
        /// </summary>
        public GenericMcpServerTestHost? ControlPlaneHost { get; private set; }

        /// <summary>
        /// Gets the first runtime-instance host.
        /// </summary>
        /// <remarks>
        /// This property is kept for backward compatibility with tests that were written
        /// before multi-runtime fixture support.
        /// </remarks>
        public GenericRuntimeInstanceHttpTestHost? RuntimeHost { get; private set; }

        /// <summary>
        /// Gets all runtime-instance hosts started by this fixture.
        /// </summary>
        public IReadOnlyList<GenericRuntimeInstanceHttpTestHost> RuntimeHosts { get; private set; } =
            Array.Empty<GenericRuntimeInstanceHttpTestHost>();

        /// <summary>
        /// Gets the HTTP client connected to the MCP control-plane host.
        /// </summary>
        public HttpClient? ControlPlaneClient { get; private set; }

        /// <summary>
        /// Gets the HTTP client connected to the first runtime-instance host.
        /// </summary>
        /// <remarks>
        /// This property is kept for backward compatibility with tests that were written
        /// before multi-runtime fixture support.
        /// </remarks>
        public HttpClient? RuntimeClient { get; private set; }

        /// <summary>
        /// Gets all HTTP clients connected to runtime-instance hosts.
        /// </summary>
        public IReadOnlyList<HttpClient> RuntimeClients { get; private set; } =
            Array.Empty<HttpClient>();

        /// <summary>
        /// Gets the MCP test client connected to the control-plane host.
        /// </summary>
        public McpTestClient Mcp { get; private set; } = default!;

        /// <summary>
        /// Initializes a new instance of the <see cref="GenericMcpRuntimeFixture"/> class
        /// with one runtime-instance host.
        /// </summary>
        /// <param name="controlPlaneSettings">The MCP control-plane host settings.</param>
        /// <param name="runtimeInstanceSettings">The runtime-instance host settings.</param>
        public GenericMcpRuntimeFixture(
            IReadOnlyDictionary<string, string?> controlPlaneSettings,
            IReadOnlyDictionary<string, string?> runtimeInstanceSettings)
            : this(
                controlPlaneSettings,
                new[] { runtimeInstanceSettings })
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GenericMcpRuntimeFixture"/> class
        /// with multiple runtime-instance hosts.
        /// </summary>
        /// <param name="controlPlaneSettings">The MCP control-plane host settings.</param>
        /// <param name="runtimeInstanceSettings">The runtime-instance host settings collection.</param>
        public GenericMcpRuntimeFixture(
            IReadOnlyDictionary<string, string?> controlPlaneSettings,
            IReadOnlyCollection<IReadOnlyDictionary<string, string?>> runtimeInstanceSettings)
        {
            this.controlPlaneSettings =
                controlPlaneSettings
                ?? throw new ArgumentNullException(nameof(controlPlaneSettings));

            ArgumentNullException.ThrowIfNull(runtimeInstanceSettings);

            if (runtimeInstanceSettings.Count == 0)
            {
                throw new ArgumentException(
                    "At least one runtime-instance settings dictionary is required.",
                    nameof(runtimeInstanceSettings));
            }

            this.runtimeInstanceSettings =
                runtimeInstanceSettings.ToArray();
        }

        /// <summary>
        /// Starts the runtime-instance hosts, then starts the MCP control-plane host.
        /// </summary>
        /// <returns>A task representing the asynchronous initialization operation.</returns>
        public Task InitializeAsync()
        {
            var runtimeHosts =
                new List<GenericRuntimeInstanceHttpTestHost>(
                    runtimeInstanceSettings.Count);

            var runtimeClients =
                new List<HttpClient>(
                    runtimeInstanceSettings.Count);

            foreach (var settings in runtimeInstanceSettings)
            {
                var runtimeHost =
                    new GenericRuntimeInstanceHttpTestHost(
                        settings);

                var runtimeClient =
                    runtimeHost.CreateClient();

                runtimeHosts.Add(
                    runtimeHost);

                runtimeClients.Add(
                    runtimeClient);
            }

            RuntimeHosts =
                runtimeHosts;

            RuntimeClients =
                runtimeClients;

            RuntimeHost =
                RuntimeHosts[0];

            RuntimeClient =
                RuntimeClients[0];

            ControlPlaneHost =
                new GenericMcpServerTestHost(
                    controlPlaneSettings,
                    RuntimeClients);

            ControlPlaneClient =
                ControlPlaneHost.CreateClient();

            Mcp =
                new McpTestClient(
                    ControlPlaneClient);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Disposes the MCP control-plane host, runtime-instance hosts, and all associated clients.
        /// </summary>
        /// <returns>A task representing the asynchronous dispose operation.</returns>
        public async Task DisposeAsync()
        {
            ControlPlaneClient?.Dispose();

            foreach (var runtimeClient in RuntimeClients)
            {
                runtimeClient.Dispose();
            }

            if (ControlPlaneHost is not null)
            {
                await ControlPlaneHost
                    .DisposeAsync()
                    .ConfigureAwait(false);
            }

            foreach (var runtimeHost in RuntimeHosts.Reverse())
            {
                await runtimeHost
                    .DisposeAsync()
                    .ConfigureAwait(false);
            }
        }
    }
}