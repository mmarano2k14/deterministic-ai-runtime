using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic
{
    /// <summary>
    /// Provides a generic multi-host integration fixture for runtime provider scenarios.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Starts one MCP control-plane host.
    /// - Starts one or more runtime-instance-only HTTP hosts.
    /// - Ensures all hosts participating in the same scenario share the same logical
    ///   control-plane identifier.
    ///
    /// IMPORTANT:
    /// - The single-runtime constructor is preserved for existing tests.
    /// - The multi-runtime constructor is used by provider-based dispatch tests.
    /// - The MCP control-plane host is started before runtime-instance hosts so that
    ///   discovery can be published before runtime instances require discovery.
    /// - Runtime HTTP clients are stored in a mutable dictionary injected into the
    ///   MCP control-plane host. The dictionary is populated after runtime hosts start.
    /// - All hosts in one fixture must use the same
    ///   <c>AiEngine:ControlPlane:ControlPlaneId</c>.
    /// </remarks>
    public sealed class GenericMcpRuntimeFixture : IAsyncLifetime
    {
        private const string ControlPlaneIdSettingKey =
            "AiEngine:ControlPlane:ControlPlaneId";

        private const string RuntimeInstanceIdSettingKey =
            "AiRuntimeInstanceRegistration:RuntimeInstanceId";

        private readonly IReadOnlyDictionary<string, string?> controlPlaneSettings;
        private readonly IReadOnlyList<IReadOnlyDictionary<string, string?>> runtimeInstanceSettings;
        private readonly Dictionary<string, HttpClient> runtimeClientsByRuntimeInstanceId =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Gets the logical control-plane identifier shared by all hosts in this fixture.
        /// </summary>
        public string ControlPlaneId { get; }

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
        /// Gets the initialized service provider for the MCP control-plane host.
        /// </summary>
        /// <remarks>
        /// This is mainly used by integration tests that need to verify the final
        /// dependency injection graph after host startup, for example whether Redis-backed
        /// control-plane stores replaced the default in-memory stores.
        /// </remarks>
        public IServiceProvider Services =>
            ControlPlaneHost?.Services
            ?? throw new InvalidOperationException(
                "The MCP control-plane host has not been initialized.");

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
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="controlPlaneSettings"/> or
        /// <paramref name="runtimeInstanceSettings"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when no runtime-instance settings are provided, when the control-plane
        /// identifier is missing, or when one runtime host uses a different control-plane
        /// identifier from the MCP control-plane host.
        /// </exception>
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

            ControlPlaneId =
                GetRequiredSetting(
                    this.controlPlaneSettings,
                    ControlPlaneIdSettingKey,
                    nameof(controlPlaneSettings));

            this.runtimeInstanceSettings =
                runtimeInstanceSettings.ToArray();

            ValidateRuntimeInstanceControlPlaneIds(
                ControlPlaneId,
                this.runtimeInstanceSettings);
        }

        /// <summary>
        /// Starts the MCP control-plane host first, then starts the runtime-instance hosts.
        /// </summary>
        /// <returns>A task representing the asynchronous initialization operation.</returns>
        public Task InitializeAsync()
        {
            ControlPlaneHost =
                new GenericMcpServerTestHost(
                    controlPlaneSettings,
                    runtimeClientsByRuntimeInstanceId);

            ControlPlaneClient =
                ControlPlaneHost.CreateClient();

            Mcp =
                new McpTestClient(
                    ControlPlaneClient);

            var runtimeHosts =
                new List<GenericRuntimeInstanceHttpTestHost>(
                    runtimeInstanceSettings.Count);

            var runtimeClients =
                new List<HttpClient>(
                    runtimeInstanceSettings.Count);

            for (var index = 0; index < runtimeInstanceSettings.Count; index++)
            {
                var settings =
                    runtimeInstanceSettings[index];

                var runtimeInstanceId =
                    GetRequiredSetting(
                        settings,
                        RuntimeInstanceIdSettingKey,
                        $"runtimeInstanceSettings[{index}]");

                var runtimeHost =
                    new GenericRuntimeInstanceHttpTestHost(
                        settings);

                var runtimeClient =
                    runtimeHost.CreateClient();

                runtimeHosts.Add(
                    runtimeHost);

                runtimeClients.Add(
                    runtimeClient);

                runtimeClientsByRuntimeInstanceId[runtimeInstanceId] =
                    runtimeClient;

                runtimeClientsByRuntimeInstanceId[$"runtime-http-{index + 1}"] =
                    runtimeClient;

                runtimeClientsByRuntimeInstanceId[$"default-{index + 1}"] =
                    runtimeClient;

                if (index == 0)
                {
                    runtimeClientsByRuntimeInstanceId["default"] =
                        runtimeClient;
                }
            }

            RuntimeHosts =
                runtimeHosts;

            RuntimeClients =
                runtimeClients;

            RuntimeHost =
                RuntimeHosts[0];

            RuntimeClient =
                RuntimeClients[0];

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

            runtimeClientsByRuntimeInstanceId.Clear();
        }

        /// <summary>
        /// Validates that every runtime-instance host uses the same logical control-plane
        /// identifier as the MCP control-plane host.
        /// </summary>
        /// <param name="expectedControlPlaneId">The expected logical control-plane identifier.</param>
        /// <param name="runtimeSettings">The runtime-instance settings collection.</param>
        /// <exception cref="ArgumentException">
        /// Thrown when a runtime-instance settings dictionary is missing a control-plane
        /// identifier or uses a different one.
        /// </exception>
        private static void ValidateRuntimeInstanceControlPlaneIds(
            string expectedControlPlaneId,
            IReadOnlyList<IReadOnlyDictionary<string, string?>> runtimeSettings)
        {
            for (var index = 0; index < runtimeSettings.Count; index++)
            {
                var runtimeControlPlaneId =
                    GetRequiredSetting(
                        runtimeSettings[index],
                        ControlPlaneIdSettingKey,
                        $"runtimeInstanceSettings[{index}]");

                if (!string.Equals(
                        NormalizeControlPlaneId(runtimeControlPlaneId),
                        NormalizeControlPlaneId(expectedControlPlaneId),
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Runtime-instance settings at index {index} use control-plane id " +
                        $"'{runtimeControlPlaneId}', but the MCP control-plane host uses " +
                        $"'{expectedControlPlaneId}'. All hosts in one fixture must share the same control-plane id.",
                        nameof(runtimeSettings));
                }
            }
        }

        /// <summary>
        /// Gets a required setting value from a settings dictionary.
        /// </summary>
        /// <param name="settings">The settings dictionary.</param>
        /// <param name="key">The setting key.</param>
        /// <param name="sourceName">The source name used for diagnostics.</param>
        /// <returns>The required setting value.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when the setting is missing or empty.
        /// </exception>
        private static string GetRequiredSetting(
            IReadOnlyDictionary<string, string?> settings,
            string key,
            string sourceName)
        {
            if (!settings.TryGetValue(key, out var value) ||
                string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    $"Required setting '{key}' is missing from {sourceName}.",
                    sourceName);
            }

            return value;
        }

        /// <summary>
        /// Normalizes a logical control-plane identifier for fixture-level comparisons.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <returns>The normalized control-plane identifier.</returns>
        private static string NormalizeControlPlaneId(
            string controlPlaneId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);

            return controlPlaneId
                .Trim()
                .Replace(" ", "-", StringComparison.Ordinal)
                .Replace("\\", "/", StringComparison.Ordinal);
        }
    }
}
