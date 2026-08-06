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

            Bootstrap.HostConfiguration.Configure(builder);

            var hostOptions = builder.Configuration
                .GetSection("AiMcpHost")
                .Get<AiMcpHostOptions>()
                ?? new AiMcpHostOptions();

            Console.WriteLine(
                $"[PROGRAM] Final host mode before validation and service registration: '{hostOptions.Mode}'.");

            Bootstrap.HostModeValidator.Validate(hostOptions);

            Bootstrap.ServiceRegistration.Configure(
                builder.Services,
                builder.Configuration);

            Bootstrap.KubernetesRuntimePoolBootstrapRegistration.Configure(
                builder.Services,
                builder.Configuration);

            Bootstrap.ProcessRuntimePoolBootstrapRegistration.Configure(
                builder.Services,
                builder.Configuration);

            var app = builder.Build();

            Bootstrap.ApplicationConfiguration.Configure(app);

            await app.RunAsync().ConfigureAwait(false);
        }
    }
}