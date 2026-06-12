using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.AI.McpServer.Host;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Http;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic
{
    /// <summary>
    /// Provides a generic MCP server host for startup-bound integration tests.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Starts an MCP host in control-plane mode for integration tests.
    /// - Applies all test-provided configuration before application startup.
    /// - Injects one or more runtime HTTP clients into the control-plane host when the
    ///   scenario uses HTTP runtime instances.
    ///
    /// IMPORTANT:
    /// - This host does not generate configuration values itself.
    /// - The caller must provide a logical control-plane identifier.
    /// - The same logical control-plane identifier must be used by all runtime-instance
    ///   hosts participating in the same scenario.
    /// - Local runtime-instance scenarios do not need runtime HTTP clients.
    /// - HTTP runtime-instance clients are optional at MCP startup time. This allows
    ///   tests to start the MCP control-plane host first, then start runtime hosts later
    ///   and populate the shared mutable client dictionary.
    /// </remarks>
    public sealed class GenericMcpServerTestHost
        : WebApplicationFactory<Program>
    {
        private const string ControlPlaneIdSettingKey =
            "AiEngine:ControlPlane:ControlPlaneId";

        private const string RegistrationControlPlaneIdSettingKey =
            "AiRuntimeInstanceRegistration:ControlPlaneId";

        private const string RuntimeInstanceIdSettingKey =
            "AiRuntimeInstanceRegistration:RuntimeInstanceId";

        private const string EngineRuntimeInstanceIdSettingKey =
            "AiEngine:RuntimeInstanceId";

        private const string HostModeSettingKey =
            "AiMcpHost:Mode";

        private const string HttpControlPlaneMode =
            "ControlPlaneWithHttpRuntimeInstances";

        private const string LocalControlPlaneMode =
            "ControlPlaneWithLocalRuntimeInstances";

        private readonly IReadOnlyDictionary<string, string?> settings;
        private readonly HttpClient? runtimeClient;
        private readonly IReadOnlyDictionary<string, HttpClient> runtimeClientsByRuntimeInstanceId;

        /// <summary>
        /// Initializes a new instance of the <see cref="GenericMcpServerTestHost"/> class.
        /// </summary>
        /// <param name="settings">The test host settings.</param>
        /// <param name="runtimeClient">The optional runtime HTTP client used by single-runtime HTTP provider tests.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="settings"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when required MCP control-plane settings are missing or inconsistent.
        /// </exception>
        public GenericMcpServerTestHost(
            IReadOnlyDictionary<string, string?> settings,
            HttpClient? runtimeClient = null)
        {
            this.settings =
                settings
                ?? throw new ArgumentNullException(nameof(settings));

            ValidateSettings(
                this.settings);

            this.runtimeClient =
                runtimeClient;

            runtimeClientsByRuntimeInstanceId =
                runtimeClient is null
                    ? new Dictionary<string, HttpClient>()
                    : new Dictionary<string, HttpClient>
                    {
                        ["default"] = runtimeClient
                    };
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GenericMcpServerTestHost"/> class
        /// with multiple runtime HTTP clients.
        /// </summary>
        /// <remarks>
        /// This overload is useful when a test only needs multiple clients available but
        /// does not require deterministic routing by runtime instance id.
        ///
        /// The list may be empty when the MCP control-plane host must start before runtime
        /// hosts. In that case the injected HTTP client factory will throw only if a runtime
        /// client is requested before one has been registered.
        /// </remarks>
        /// <param name="settings">The test host settings.</param>
        /// <param name="runtimeClients">The runtime HTTP clients.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="settings"/> or <paramref name="runtimeClients"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when required MCP settings are missing.
        /// </exception>
        public GenericMcpServerTestHost(
            IReadOnlyDictionary<string, string?> settings,
            IReadOnlyList<HttpClient> runtimeClients)
        {
            this.settings =
                settings
                ?? throw new ArgumentNullException(nameof(settings));

            ValidateSettings(
                this.settings);

            ArgumentNullException.ThrowIfNull(runtimeClients);

            runtimeClient =
                runtimeClients.Count == 0
                    ? null
                    : runtimeClients[0];

            var clients =
                runtimeClients
                    .Select(
                        (client, index) => new
                        {
                            RuntimeInstanceId = $"runtime-http-{index + 1}",
                            Client = client
                        })
                    .ToDictionary(
                        item => item.RuntimeInstanceId,
                        item => item.Client,
                        StringComparer.Ordinal);

            if (runtimeClient is not null)
            {
                clients["default"] =
                    runtimeClient;
            }

            runtimeClientsByRuntimeInstanceId =
                clients;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GenericMcpServerTestHost"/> class
        /// with runtime HTTP clients keyed by runtime instance id.
        /// </summary>
        /// <remarks>
        /// This overload is the preferred mode for HTTP multi-runtime provider tests.
        /// It allows the injected <see cref="IHttpClientFactory"/> to return the correct
        /// runtime host client based on the requested client name.
        ///
        /// The dictionary may be empty at MCP startup time when tests intentionally start
        /// the MCP control-plane before runtime hosts. The dictionary is kept by reference,
        /// so callers may populate it after runtime hosts are created.
        /// </remarks>
        /// <param name="settings">The test host settings.</param>
        /// <param name="runtimeClientsByRuntimeInstanceId">The runtime HTTP clients keyed by runtime instance id.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="settings"/> or
        /// <paramref name="runtimeClientsByRuntimeInstanceId"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when required MCP settings are missing or when a runtime client key is empty.
        /// </exception>
        public GenericMcpServerTestHost(
            IReadOnlyDictionary<string, string?> settings,
            IReadOnlyDictionary<string, HttpClient> runtimeClientsByRuntimeInstanceId)
        {
            this.settings =
                settings
                ?? throw new ArgumentNullException(nameof(settings));

            ValidateSettings(
                this.settings);

            this.runtimeClientsByRuntimeInstanceId =
                runtimeClientsByRuntimeInstanceId
                ?? throw new ArgumentNullException(nameof(runtimeClientsByRuntimeInstanceId));

            foreach (var pair in runtimeClientsByRuntimeInstanceId)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    throw new ArgumentException(
                        "Runtime HTTP client dictionary keys must be non-empty runtime instance identifiers.",
                        nameof(runtimeClientsByRuntimeInstanceId));
                }

                ArgumentNullException.ThrowIfNull(pair.Value);
            }

            runtimeClient =
                runtimeClientsByRuntimeInstanceId.Values.FirstOrDefault();
        }

        /// <summary>
        /// Configures the MCP test web host.
        /// </summary>
        /// <param name="builder">The web host builder.</param>
        protected override void ConfigureWebHost(
            IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");

            foreach (var setting in settings)
            {
                builder.UseSetting(
                    setting.Key,
                    setting.Value);
            }

            builder.ConfigureServices(services =>
            {
                if (runtimeClientsByRuntimeInstanceId.Count == 1 &&
                    runtimeClient is not null)
                {
                    services.AddSingleton(
                        runtimeClient);

                    services.AddSingleton<IHttpClientFactory>(
                        new TestRuntimeHttpClientFactory(
                            runtimeClient));

                    Console.WriteLine(
                        "[TEST MCP HOST] Single runtime HTTP client injected into control-plane host.");

                    return;
                }

                services.AddSingleton(
                    runtimeClientsByRuntimeInstanceId);

                services.AddSingleton<IReadOnlyDictionary<string, HttpClient>>(
                    runtimeClientsByRuntimeInstanceId);

                services.AddSingleton<IHttpClientFactory>(
                    new MultiRuntimeHttpClientFactory(
                        runtimeClientsByRuntimeInstanceId));

                Console.WriteLine(
                    $"[TEST MCP HOST] Runtime HTTP client factory injected into control-plane host. RuntimeClientCount='{runtimeClientsByRuntimeInstanceId.Count}', RuntimeInstances='{string.Join(", ", runtimeClientsByRuntimeInstanceId.Keys)}'.");
            });
        }

        /// <summary>
        /// Validates required MCP control-plane settings before the test host starts.
        /// </summary>
        /// <param name="settings">The MCP host settings.</param>
        /// <exception cref="ArgumentException">
        /// Thrown when required settings are missing or inconsistent.
        /// </exception>
        private static void ValidateSettings(
            IReadOnlyDictionary<string, string?> settings)
        {
            var mode =
                GetRequiredSetting(
                    settings,
                    HostModeSettingKey);

            if (!IsSupportedControlPlaneMode(mode))
            {
                throw new ArgumentException(
                    $"Generic MCP server test host requires '{HostModeSettingKey}' to be either " +
                    $"'{HttpControlPlaneMode}' or '{LocalControlPlaneMode}', but found '{mode}'.",
                    nameof(settings));
            }

            var controlPlaneId =
                GetRequiredSetting(
                    settings,
                    ControlPlaneIdSettingKey);

            var registrationControlPlaneId =
                GetRequiredSetting(
                    settings,
                    RegistrationControlPlaneIdSettingKey);

            if (!string.Equals(
                    NormalizeKeySegment(controlPlaneId),
                    NormalizeKeySegment(registrationControlPlaneId),
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Control-plane id mismatch. Setting '{ControlPlaneIdSettingKey}' is '{controlPlaneId}', " +
                    $"but '{RegistrationControlPlaneIdSettingKey}' is '{registrationControlPlaneId}'.",
                    nameof(settings));
            }

            var registrationRuntimeInstanceId =
                GetRequiredSetting(
                    settings,
                    RuntimeInstanceIdSettingKey);

            var engineRuntimeInstanceId =
                GetRequiredSetting(
                    settings,
                    EngineRuntimeInstanceIdSettingKey);

            if (!string.Equals(
                    NormalizeKeySegment(registrationRuntimeInstanceId),
                    NormalizeKeySegment(engineRuntimeInstanceId),
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Runtime instance id mismatch. Setting '{RuntimeInstanceIdSettingKey}' is '{registrationRuntimeInstanceId}', " +
                    $"but '{EngineRuntimeInstanceIdSettingKey}' is '{engineRuntimeInstanceId}'.",
                    nameof(settings));
            }
        }

        /// <summary>
        /// Determines whether the configured MCP host mode is a supported control-plane mode.
        /// </summary>
        /// <param name="mode">The configured MCP host mode.</param>
        /// <returns><c>true</c> when the mode is supported; otherwise, <c>false</c>.</returns>
        private static bool IsSupportedControlPlaneMode(
            string mode)
        {
            return string.Equals(
                    mode,
                    HttpControlPlaneMode,
                    StringComparison.Ordinal)
                || string.Equals(
                    mode,
                    LocalControlPlaneMode,
                    StringComparison.Ordinal);
        }

        /// <summary>
        /// Gets a required setting value from a settings dictionary.
        /// </summary>
        /// <param name="settings">The settings dictionary.</param>
        /// <param name="key">The required setting key.</param>
        /// <returns>The required setting value.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when the setting is missing or empty.
        /// </exception>
        private static string GetRequiredSetting(
            IReadOnlyDictionary<string, string?> settings,
            string key)
        {
            if (!settings.TryGetValue(key, out var value) ||
                string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    $"Required MCP control-plane host setting '{key}' is missing.",
                    nameof(settings));
            }

            return value;
        }

        /// <summary>
        /// Normalizes a value for stable test-host comparisons.
        /// </summary>
        /// <param name="value">The value to normalize.</param>
        /// <returns>The normalized value.</returns>
        private static string NormalizeKeySegment(
            string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);

            return value
                .Trim()
                .Replace(" ", "-", StringComparison.Ordinal)
                .Replace("\\", "/", StringComparison.Ordinal);
        }

        /// <summary>
        /// Provides an <see cref="IHttpClientFactory"/> implementation for multi-runtime HTTP tests.
        /// </summary>
        /// <remarks>
        /// The HTTP runtime provider should request a client by runtime instance id when
        /// dispatching to a selected runtime instance.
        ///
        /// The factory intentionally supports an initially empty dictionary so the MCP
        /// control-plane host can start before runtime hosts. The dictionary is read at
        /// request time, not captured as a fixed fallback at construction time.
        /// </remarks>
        private sealed class MultiRuntimeHttpClientFactory : IHttpClientFactory
        {
            private readonly IReadOnlyDictionary<string, HttpClient> clientsByRuntimeInstanceId;
            private readonly HttpClient startupRoutingClient;

            public MultiRuntimeHttpClientFactory(
                IReadOnlyDictionary<string, HttpClient> clientsByRuntimeInstanceId)
            {
                this.clientsByRuntimeInstanceId =
                    clientsByRuntimeInstanceId
                    ?? throw new ArgumentNullException(nameof(clientsByRuntimeInstanceId));

                startupRoutingClient =
                    new HttpClient(
                        new RuntimeClientRoutingHandler(
                            clientsByRuntimeInstanceId))
                    {
                        BaseAddress = new Uri("http://localhost")
                    };
            }

            public HttpClient CreateClient(
                string name)
            {
                if (!string.IsNullOrWhiteSpace(name) &&
                    clientsByRuntimeInstanceId.TryGetValue(
                        name,
                        out var client))
                {
                    return client;
                }

                if (!string.IsNullOrWhiteSpace(name))
                {
                    var matchingClient =
                        clientsByRuntimeInstanceId
                            .FirstOrDefault(pair =>
                                name.Contains(
                                    pair.Key,
                                    StringComparison.Ordinal));

                    if (matchingClient.Value is not null)
                    {
                        return matchingClient.Value;
                    }
                }

                if (clientsByRuntimeInstanceId.TryGetValue(
                        "default",
                        out var defaultClient))
                {
                    return defaultClient;
                }

                var fallbackClient =
                    clientsByRuntimeInstanceId.Values.FirstOrDefault();

                if (fallbackClient is not null)
                {
                    return fallbackClient;
                }

                return startupRoutingClient;
            }

            private sealed class RuntimeClientRoutingHandler : HttpMessageHandler
            {
                private readonly IReadOnlyDictionary<string, HttpClient> clientsByRuntimeInstanceId;

                public RuntimeClientRoutingHandler(
                    IReadOnlyDictionary<string, HttpClient> clientsByRuntimeInstanceId)
                {
                    this.clientsByRuntimeInstanceId =
                        clientsByRuntimeInstanceId
                        ?? throw new ArgumentNullException(nameof(clientsByRuntimeInstanceId));
                }

                protected override async Task<HttpResponseMessage> SendAsync(
                    HttpRequestMessage request,
                    CancellationToken cancellationToken)
                {
                    var client =
                        ResolveClient();

                    var forwardedRequest =
                        await CloneRequestAsync(
                                request,
                                cancellationToken)
                            .ConfigureAwait(false);

                    if (forwardedRequest.RequestUri is not null &&
                        forwardedRequest.RequestUri.IsAbsoluteUri &&
                        string.Equals(
                            forwardedRequest.RequestUri.Host,
                            "localhost",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        forwardedRequest.RequestUri =
                            new Uri(
                                forwardedRequest.RequestUri.PathAndQuery,
                                UriKind.Relative);
                    }

                    return await client
                        .SendAsync(
                            forwardedRequest,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                private HttpClient ResolveClient()
                {
                    if (clientsByRuntimeInstanceId.TryGetValue(
                            "default",
                            out var defaultClient))
                    {
                        return defaultClient;
                    }

                    var fallbackClient =
                        clientsByRuntimeInstanceId.Values.FirstOrDefault();

                    if (fallbackClient is not null)
                    {
                        return fallbackClient;
                    }

                    throw new InvalidOperationException(
                        "No runtime HTTP client is available yet. The MCP control-plane was started before runtime hosts, but a runtime HTTP request was sent before any runtime client was registered.");
                }

                private static async Task<HttpRequestMessage> CloneRequestAsync(
                    HttpRequestMessage request,
                    CancellationToken cancellationToken)
                {
                    var clone =
                        new HttpRequestMessage(
                            request.Method,
                            request.RequestUri);

                    foreach (var header in request.Headers)
                    {
                        clone.Headers.TryAddWithoutValidation(
                            header.Key,
                            header.Value);
                    }

                    foreach (var option in request.Options)
                    {
                        clone.Options.TryAdd(
                            option.Key,
                            option.Value);
                    }

                    if (request.Content is not null)
                    {
                        var contentBytes =
                            await request.Content
                                .ReadAsByteArrayAsync(cancellationToken)
                                .ConfigureAwait(false);

                        clone.Content =
                            new ByteArrayContent(contentBytes);

                        foreach (var header in request.Content.Headers)
                        {
                            clone.Content.Headers.TryAddWithoutValidation(
                                header.Key,
                                header.Value);
                        }
                    }

                    return clone;
                }
            }
        }
    }
}
