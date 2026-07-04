using Microsoft.Extensions.Options;
using Multiplexed.AI.McpServer.Host.Configuration;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http;
using Multiplexed.Rbac.Core.Runtime;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Configures the HTTP application pipeline.
    /// </summary>
    public static class ApplicationConfiguration
    {
        /// <summary>
        /// Configures application endpoints according to the MCP host mode.
        /// </summary>
        /// <param name="app">The web application.</param>
        public static void Configure(
            WebApplication app)
        {
            ArgumentNullException.ThrowIfNull(app);

            var hostOptions =
                app.Services
                    .GetRequiredService<IOptions<AiMcpHostOptions>>()
                    .Value;

            Console.WriteLine($"[APP CONFIG] Mode='{hostOptions.Mode}'");

            app.MapHealthChecks("/health");

            switch (hostOptions.Mode)
            {
                case AiMcpHostMode.ControlPlaneOnly:
                case AiMcpHostMode.ControlPlaneWithLocalRuntimeInstances:
                case AiMcpHostMode.ControlPlaneWithHttpRuntimeInstances:
                case AiMcpHostMode.ControlPlaneWithGrpcRuntimeInstances:
                    Console.WriteLine("[APP CONFIG] Mapping MCP endpoint '/mcp'.");

                    app.UseAuthentication();

                    app.UseWhen(
                        context => context.Request.Path.StartsWithSegments("/mcp"),
                        branch =>
                        {
                            branch.UseMiddleware<ExecutionContextMiddleware>();
                            branch.UseMiddleware<NamespaceGuardMiddleware>();
                        });

                    app.UseAuthorization();

                    app.MapMcp("/mcp");

                    break;

                case AiMcpHostMode.RuntimeInstanceOnly:
                    ConfigureRuntimeInstanceEndpoints(
                        app);

                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported MCP host mode '{hostOptions.Mode}'.");
            }
        }

        /// <summary>
        /// Configures runtime-instance command endpoints according to the runtime transport configuration.
        /// </summary>
        /// <param name="app">The web application.</param>
        private static void ConfigureRuntimeInstanceEndpoints(
            WebApplication app)
        {
            ArgumentNullException.ThrowIfNull(app);

            var disableRuntimeCommandEndpoint =
                app.Configuration.GetValue<bool>("Tests:DisableRuntimeCommandEndpoint");

            if (disableRuntimeCommandEndpoint)
            {
                Console.WriteLine(
                    "[APP CONFIG] Runtime command endpoint disabled by Tests:DisableRuntimeCommandEndpoint.");

                return;
            }

            var transportName =
                ResolveRuntimeCommandTransportName(
                    app.Configuration);

            switch (transportName)
            {
                case "http":
                    Console.WriteLine(
                        "[APP CONFIG] Mapping runtime HTTP command endpoint '/runtime-instance/commands'.");

                    app.MapAiRuntimeInstanceHttpCommandEndpoint();
                    break;

                case "grpc":
                    Console.WriteLine(
                        "[APP CONFIG] Mapping runtime gRPC command service.");

                    app.MapAiRuntimeInstanceGrpcCommandService();
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported runtime command transport '{transportName}'.");
            }
        }

        /// <summary>
        /// Resolves the command transport used by a runtime-instance-only host.
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        /// <returns>The normalized runtime command transport name.</returns>
        private static string ResolveRuntimeCommandTransportName(
            IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            var transportName =
                configuration["AiRuntimeInstanceRegistration:ProviderMetadata:transport.name"];

            if (!string.IsNullOrWhiteSpace(transportName))
            {
                return transportName.Trim().ToLowerInvariant();
            }

            var providerName =
                configuration["AiRuntimeInstanceRegistration:ProviderName"];

            if (!string.IsNullOrWhiteSpace(providerName))
            {
                return providerName.Trim().ToLowerInvariant();
            }

            var providerMetadataName =
                configuration["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"];

            if (!string.IsNullOrWhiteSpace(providerMetadataName))
            {
                return providerMetadataName.Trim().ToLowerInvariant();
            }

            return "http";
        }
    }
}