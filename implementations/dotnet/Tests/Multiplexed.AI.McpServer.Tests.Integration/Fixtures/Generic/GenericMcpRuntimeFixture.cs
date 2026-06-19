using Multiplexed.AI.McpServer.Tests.Integration.Auth;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic
{
    /// <summary>
    /// Provides a generic multi-host integration fixture for runtime provider scenarios.
    /// </summary>
    public sealed class GenericMcpRuntimeFixture : IAsyncLifetime
    {
        private const string ControlPlaneIdSettingKey =
            "AiEngine:ControlPlane:ControlPlaneId";

        private const string RuntimeInstanceIdSettingKey =
            "AiRuntimeInstanceRegistration:RuntimeInstanceId";

        private readonly IReadOnlyDictionary<string, string?> controlPlaneSettings;
        private readonly IReadOnlyList<IReadOnlyDictionary<string, string?>> runtimeInstanceSettings;
        private readonly string? rbacTenantId;
        private readonly string? rbacTenantGroupId;

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
        public IServiceProvider Services =>
            ControlPlaneHost?.Services
            ?? throw new InvalidOperationException(
                "The MCP control-plane host has not been initialized.");

        /// <summary>
        /// Initializes a new instance of the <see cref="GenericMcpRuntimeFixture"/> class
        /// with one runtime-instance host.
        /// </summary>
        public GenericMcpRuntimeFixture(
            IReadOnlyDictionary<string, string?> controlPlaneSettings,
            IReadOnlyDictionary<string, string?> runtimeInstanceSettings)
            : this(
                controlPlaneSettings,
                new[] { runtimeInstanceSettings },
                rbacTenantId: null,
                rbacTenantGroupId: null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GenericMcpRuntimeFixture"/> class
        /// with one runtime-instance host and an explicit RBAC tenant context.
        /// </summary>
        public GenericMcpRuntimeFixture(
            IReadOnlyDictionary<string, string?> controlPlaneSettings,
            IReadOnlyDictionary<string, string?> runtimeInstanceSettings,
            string? rbacTenantId,
            string? rbacTenantGroupId = null)
            : this(
                controlPlaneSettings,
                new[] { runtimeInstanceSettings },
                rbacTenantId,
                rbacTenantGroupId)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GenericMcpRuntimeFixture"/> class
        /// with multiple runtime-instance hosts.
        /// </summary>
        public GenericMcpRuntimeFixture(
            IReadOnlyDictionary<string, string?> controlPlaneSettings,
            IReadOnlyCollection<IReadOnlyDictionary<string, string?>> runtimeInstanceSettings)
            : this(
                controlPlaneSettings,
                runtimeInstanceSettings,
                rbacTenantId: null,
                rbacTenantGroupId: null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GenericMcpRuntimeFixture"/> class
        /// with multiple runtime-instance hosts and an explicit RBAC tenant context.
        /// </summary>
        public GenericMcpRuntimeFixture(
            IReadOnlyDictionary<string, string?> controlPlaneSettings,
            IReadOnlyCollection<IReadOnlyDictionary<string, string?>> runtimeInstanceSettings,
            string? rbacTenantId,
            string? rbacTenantGroupId = null)
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

            this.rbacTenantId = rbacTenantId;
            this.rbacTenantGroupId = rbacTenantGroupId;

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
        public async Task InitializeAsync()
        {
            ControlPlaneHost =
                new GenericMcpServerTestHost(
                    controlPlaneSettings,
                    runtimeClientsByRuntimeInstanceId);

            ControlPlaneClient =
                ControlPlaneHost.CreateClient();

            Mcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        ControlPlaneHost,
                        ControlPlaneClient,
                        McpRbacTestContextFactory.DefaultUserId,
                        rbacTenantId,
                        rbacTenantGroupId)
                    .ConfigureAwait(false);

            Console.WriteLine(
                $"[GENERIC MCP FIXTURE] Configured MCP RBAC headers. UserId='{McpRbacTestContextFactory.DefaultUserId}', TenantId='{rbacTenantId ?? McpRbacTestContextFactory.DefaultTenantId}'.");

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
        }

        /// <summary>
        /// Disposes the MCP control-plane host, runtime-instance hosts, and all associated clients.
        /// </summary>
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