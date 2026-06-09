using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.AI.McpServer.Host;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Http;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic
{
    /// <summary>
    /// Provides a generic MCP server host for startup-bound integration tests.
    /// All configuration is supplied explicitly by the test through
    /// <see cref="IWebHostBuilder.UseSetting(string, string?)"/>.
    /// </summary>
    public sealed class GenericMcpServerTestHost
        : WebApplicationFactory<Program>
    {
        private readonly IReadOnlyDictionary<string, string?> settings;
        private readonly HttpClient? runtimeClient;
        private readonly IReadOnlyDictionary<string, HttpClient> runtimeClientsByRuntimeInstanceId;

        /// <summary>
        /// Initializes a new instance of the <see cref="GenericMcpServerTestHost"/> class.
        /// </summary>
        /// <param name="settings">The test host settings.</param>
        /// <param name="runtimeClient">The optional runtime HTTP client used by single-runtime HTTP provider tests.</param>
        public GenericMcpServerTestHost(
            IReadOnlyDictionary<string, string?> settings,
            HttpClient? runtimeClient = null)
        {
            this.settings =
                settings
                ?? throw new ArgumentNullException(nameof(settings));

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
        /// For provider dispatch tests that must route to a specific runtime instance,
        /// prefer the overload that receives a dictionary keyed by runtime instance id.
        /// </remarks>
        /// <param name="settings">The test host settings.</param>
        /// <param name="runtimeClients">The runtime HTTP clients.</param>
        public GenericMcpServerTestHost(
            IReadOnlyDictionary<string, string?> settings,
            IReadOnlyList<HttpClient> runtimeClients)
        {
            this.settings =
                settings
                ?? throw new ArgumentNullException(nameof(settings));

            ArgumentNullException.ThrowIfNull(runtimeClients);

            if (runtimeClients.Count == 0)
            {
                throw new ArgumentException(
                    "At least one runtime HTTP client is required.",
                    nameof(runtimeClients));
            }

            runtimeClient =
                runtimeClients[0];

            runtimeClientsByRuntimeInstanceId =
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
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="GenericMcpServerTestHost"/> class
        /// with runtime HTTP clients keyed by runtime instance id.
        /// </summary>
        /// <remarks>
        /// This overload is the preferred mode for HTTP multi-runtime provider tests.
        /// It allows the injected <see cref="IHttpClientFactory"/> to return the correct
        /// runtime host client based on the requested client name.
        /// </remarks>
        /// <param name="settings">The test host settings.</param>
        /// <param name="runtimeClientsByRuntimeInstanceId">The runtime HTTP clients keyed by runtime instance id.</param>
        public GenericMcpServerTestHost(
            IReadOnlyDictionary<string, string?> settings,
            IReadOnlyDictionary<string, HttpClient> runtimeClientsByRuntimeInstanceId)
        {
            this.settings =
                settings
                ?? throw new ArgumentNullException(nameof(settings));

            this.runtimeClientsByRuntimeInstanceId =
                runtimeClientsByRuntimeInstanceId
                ?? throw new ArgumentNullException(nameof(runtimeClientsByRuntimeInstanceId));

            if (runtimeClientsByRuntimeInstanceId.Count == 0)
            {
                throw new ArgumentException(
                    "At least one runtime HTTP client is required.",
                    nameof(runtimeClientsByRuntimeInstanceId));
            }

            runtimeClient =
                runtimeClientsByRuntimeInstanceId.Values.First();
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
                if (runtimeClientsByRuntimeInstanceId.Count == 0)
                {
                    return;
                }

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

                services.AddSingleton<IReadOnlyDictionary<string, HttpClient>>(
                    runtimeClientsByRuntimeInstanceId);

                services.AddSingleton<IHttpClientFactory>(
                    new MultiRuntimeHttpClientFactory(
                        runtimeClientsByRuntimeInstanceId));

                Console.WriteLine(
                    $"[TEST MCP HOST] Multi-runtime HTTP clients injected into control-plane host. RuntimeInstances='{string.Join(", ", runtimeClientsByRuntimeInstanceId.Keys)}'.");
            });
        }

        /// <summary>
        /// Provides an <see cref="IHttpClientFactory"/> implementation for multi-runtime HTTP tests.
        /// </summary>
        /// <remarks>
        /// The HTTP runtime provider should request a client by runtime instance id when
        /// dispatching to a selected runtime instance.
        ///
        /// If the provider requests an unnamed client and only one runtime client exists,
        /// the default client is returned. If multiple clients exist and the requested
        /// name is empty, the first registered client is returned for backward compatibility,
        /// but true multi-runtime routing requires a runtime-instance-specific client name.
        /// </remarks>
        private sealed class MultiRuntimeHttpClientFactory : IHttpClientFactory
        {
            private readonly IReadOnlyDictionary<string, HttpClient> clientsByRuntimeInstanceId;
            private readonly HttpClient fallbackClient;

            /// <summary>
            /// Initializes a new instance of the <see cref="MultiRuntimeHttpClientFactory"/> class.
            /// </summary>
            /// <param name="clientsByRuntimeInstanceId">The HTTP clients keyed by runtime instance id.</param>
            public MultiRuntimeHttpClientFactory(
                IReadOnlyDictionary<string, HttpClient> clientsByRuntimeInstanceId)
            {
                this.clientsByRuntimeInstanceId =
                    clientsByRuntimeInstanceId
                    ?? throw new ArgumentNullException(nameof(clientsByRuntimeInstanceId));

                if (clientsByRuntimeInstanceId.Count == 0)
                {
                    throw new ArgumentException(
                        "At least one runtime HTTP client is required.",
                        nameof(clientsByRuntimeInstanceId));
                }

                fallbackClient =
                    clientsByRuntimeInstanceId.Values.First();
            }

            /// <summary>
            /// Creates or returns an HTTP client for the requested runtime instance.
            /// </summary>
            /// <param name="name">The requested client name. For multi-runtime tests this should be the runtime instance id.</param>
            /// <returns>The matching HTTP client.</returns>
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

                return fallbackClient;
            }
        }
    }
}