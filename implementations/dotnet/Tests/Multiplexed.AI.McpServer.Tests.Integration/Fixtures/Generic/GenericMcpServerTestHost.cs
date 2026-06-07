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

        public GenericMcpServerTestHost(
            IReadOnlyDictionary<string, string?> settings,
            HttpClient? runtimeClient = null)
        {
            this.settings =
                settings
                ?? throw new ArgumentNullException(nameof(settings));

            this.runtimeClient =
                runtimeClient;
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

            builder.ConfigureServices(services =>
            {
                if (runtimeClient is not null)
                {
                    services.AddSingleton(runtimeClient);

                    services.AddSingleton<IHttpClientFactory>(
                        new TestRuntimeHttpClientFactory(
                            runtimeClient));

                    Console.WriteLine(
                        "[TEST MCP HOST] Runtime HTTP client injected into control-plane host.");
                }
            });
        }
    }
}