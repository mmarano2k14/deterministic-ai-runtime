using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.AI.McpServer.Host;
using Multiplexed.AI.McpServer.Tests.Integration.Auth;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Http;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic
{
    /// <summary>
    /// Provides a generic MCP server host for startup-bound integration tests.
    /// </summary>
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

            builder.ConfigureTestServices(services =>
            {
                services
                    .AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme =
                            FakeAuthHandler.AuthenticationScheme;

                        options.DefaultChallengeScheme =
                            FakeAuthHandler.AuthenticationScheme;

                        options.DefaultScheme =
                            FakeAuthHandler.AuthenticationScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, FakeAuthHandler>(
                        FakeAuthHandler.AuthenticationScheme,
                        _ => { });

                services.AddAuthorization();
            });

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

        private static string NormalizeKeySegment(
            string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);

            return value
                .Trim()
                .Replace(" ", "-", StringComparison.Ordinal)
                .Replace("\\", "/", StringComparison.Ordinal);
        }

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