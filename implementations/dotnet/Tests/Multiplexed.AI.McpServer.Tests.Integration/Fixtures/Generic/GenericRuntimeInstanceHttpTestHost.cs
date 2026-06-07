using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.AI.McpServer.Host;
using Multiplexed.AI.Runtime.ControlPlane.DI;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic
{
    /// <summary>
    /// Provides a generic runtime-instance HTTP test host where all startup-bound
    /// settings are supplied explicitly by the test.
    /// </summary>
    public sealed class GenericRuntimeInstanceHttpTestHost
        : WebApplicationFactory<Program>
    {
        private readonly IReadOnlyDictionary<string, string?> settings;

        /// <summary>
        /// Initializes a new instance of the <see cref="GenericRuntimeInstanceHttpTestHost"/> class.
        /// </summary>
        /// <param name="settings">
        /// Host settings to apply before application startup.
        /// </param>
        public GenericRuntimeInstanceHttpTestHost(
            IReadOnlyDictionary<string, string?> settings)
        {
            this.settings =
                settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <inheritdoc />
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
                services.AddAiHttpRuntimeInstanceProvider();
            });
        }
    }
}