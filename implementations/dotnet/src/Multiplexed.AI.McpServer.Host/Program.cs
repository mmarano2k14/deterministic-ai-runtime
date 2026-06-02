using Multiplexed.AI.McpServer.Host.Configuration;

namespace Multiplexed.AI.McpServer.Host
{
    /// <summary>
    /// Application entry point.
    /// </summary>
    public partial class Program 
    {
        /// <summary>
        /// Application entry point.
        /// </summary>
        /// <param name="args">Command line arguments.</param>
        /// <returns>The application exit code.</returns>
        public static async Task Main(
            string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Host.UseDefaultServiceProvider(options =>
            {
                options.ValidateOnBuild = false;
            });

            var hostOptions = builder.Configuration
                .GetSection("AiMcpHost")
                .Get<AiMcpHostOptions>()
                ?? new AiMcpHostOptions();

            Bootstrap.HostModeValidator.Validate(hostOptions);

            Bootstrap.HostConfiguration.Configure(builder);

            Bootstrap.ServiceRegistration.Configure(
                builder.Services,
                builder.Configuration);

            var app = builder.Build();

            Bootstrap.ApplicationConfiguration.Configure(app);

            await app.RunAsync().ConfigureAwait(false);
        }
    }
}