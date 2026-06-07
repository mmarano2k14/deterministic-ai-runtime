using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic
{
    /// <summary>
    /// Provides a generic two-host integration fixture for runtime provider scenarios.
    /// </summary>
    public sealed class GenericMcpRuntimeFixture : IAsyncLifetime
    {
        private readonly IReadOnlyDictionary<string, string?> controlPlaneSettings;
        private readonly IReadOnlyDictionary<string, string?> runtimeInstanceSettings;

        public GenericMcpServerTestHost? ControlPlaneHost { get; private set; }

        public GenericRuntimeInstanceHttpTestHost? RuntimeHost { get; private set; }

        public HttpClient? ControlPlaneClient { get; private set; }

        public HttpClient? RuntimeClient { get; private set; }

        public McpTestClient Mcp { get; private set; } = default!;

        public GenericMcpRuntimeFixture(
            IReadOnlyDictionary<string, string?> controlPlaneSettings,
            IReadOnlyDictionary<string, string?> runtimeInstanceSettings)
        {
            this.controlPlaneSettings =
                controlPlaneSettings
                ?? throw new ArgumentNullException(nameof(controlPlaneSettings));

            this.runtimeInstanceSettings =
                runtimeInstanceSettings
                ?? throw new ArgumentNullException(nameof(runtimeInstanceSettings));
        }

        public Task InitializeAsync()
        {
            RuntimeHost =
                new GenericRuntimeInstanceHttpTestHost(
                    runtimeInstanceSettings);

            RuntimeClient =
                RuntimeHost.CreateClient();

            ControlPlaneHost =
                new GenericMcpServerTestHost(
                    controlPlaneSettings,
                    RuntimeClient);

            ControlPlaneClient =
                ControlPlaneHost.CreateClient();

            Mcp =
                new McpTestClient(
                    ControlPlaneClient);

            return Task.CompletedTask;
        }

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