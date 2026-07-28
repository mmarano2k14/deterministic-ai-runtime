using Microsoft.Extensions.Options;
using Multiplexed.AI.McpServer.Host.Configuration;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Grpc;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Http;
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
        public static void Configure(
            WebApplication app)
        {
            ArgumentNullException.ThrowIfNull(app);

            var hostOptions =
                app.Services
                    .GetRequiredService<IOptions<AiMcpHostOptions>>()
                    .Value;

            app.MapHealthChecks("/health");

            switch (hostOptions.Mode)
            {
                case AiMcpHostMode.ControlPlaneOnly:
                case AiMcpHostMode.ControlPlaneWithLocalRuntimeInstances:
                case AiMcpHostMode.ControlPlaneWithHttpRuntimeInstances:
                case AiMcpHostMode.ControlPlaneWithGrpcRuntimeInstances:
                    app.UseAuthentication();
                    app.UseWhen(
                        context =>
                            context.Request.Path
                                .StartsWithSegments("/mcp"),
                        branch =>
                        {
                            branch.UseMiddleware<
                                ExecutionContextMiddleware>();
                            branch.UseMiddleware<
                                NamespaceGuardMiddleware>();
                        });
                    app.UseAuthorization();
                    app.MapMcp("/mcp");
                    break;

                case AiMcpHostMode.RuntimeInstanceOnly:
                    ConfigureRuntimeInstanceEndpoints(app);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported MCP host mode '{hostOptions.Mode}'.");
            }
        }

        /// <summary>
        /// Configures either a single runtime endpoint or the stable Runtime Pool endpoint.
        /// </summary>
        private static void ConfigureRuntimeInstanceEndpoints(
            WebApplication app)
        {
            var poolOptions =
                app.Configuration
                    .GetSection("AiKubernetesRuntimePoolInPod")
                    .Get<AiKubernetesRuntimePoolInPodOptions>();

            if (poolOptions?.Enabled == true)
            {
                ConfigureRuntimePoolEndpoints(
                    app,
                    poolOptions);
                return;
            }

            var disableRuntimeCommandEndpoint =
                app.Configuration.GetValue<bool>(
                    "Tests:DisableRuntimeCommandEndpoint");

            if (disableRuntimeCommandEndpoint)
            {
                return;
            }

            var transportName =
                ResolveRuntimeCommandTransportName(
                    app.Configuration);

            switch (transportName)
            {
                case "http":
                    app.MapAiRuntimeInstanceHttpCommandEndpoint();
                    break;

                case "grpc":
                    app.MapAiRuntimeInstanceGrpcCommandService();
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported runtime command transport '{transportName}'.");
            }
        }

        /// <summary>
        /// Maps the stable exact router and Kubernetes readiness endpoint.
        /// </summary>
        private static void ConfigureRuntimePoolEndpoints(
            WebApplication app,
            AiKubernetesRuntimePoolInPodOptions options)
        {
            switch (options.TransportName
                .Trim()
                .ToLowerInvariant())
            {
                case "http":
                    app.MapAiRuntimePoolHttpCommandEndpoint();
                    break;

                case "grpc":
                    app.MapAiRuntimePoolGrpcCommandService();
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported Runtime Pool transport '{options.TransportName}'.");
            }

            app.MapGet(
                "/runtime-pool/readiness",
                async (
                    IAiRuntimeProcessPoolManager manager,
                    CancellationToken cancellationToken) =>
                {
                    var snapshot =
                        await manager
                            .GetSnapshotAsync(cancellationToken)
                            .ConfigureAwait(false);

                    var ready =
                        snapshot.Status
                            == AiRuntimeProcessPoolManagerStatus.Running
                        && !snapshot.IsBelowMinimumCapacity
                        && snapshot.Children.Count
                            >= snapshot.MinimumProcessCount
                        && snapshot.Children.All(
                            child =>
                                child.Status
                                == AiRuntimeProcessPoolChildStatus.Running);

                    return ready
                        ? Results.Ok(
                            new
                            {
                                ready = true,
                                snapshot.PoolId,
                                snapshot.HostId,
                                RuntimeInstanceIds =
                                    snapshot.Children
                                        .Select(
                                            child =>
                                                child.RuntimeInstanceId)
                                        .ToArray()
                            })
                        : Results.Json(
                            new
                            {
                                ready = false,
                                snapshot.PoolId,
                                snapshot.HostId,
                                Status =
                                    snapshot.Status.ToString(),
                                snapshot.IsBelowMinimumCapacity,
                                ChildCount =
                                    snapshot.Children.Count
                            },
                            statusCode:
                                StatusCodes
                                    .Status503ServiceUnavailable);
                });
        }

        /// <summary>
        /// Resolves the single-runtime command transport.
        /// </summary>
        private static string ResolveRuntimeCommandTransportName(
            IConfiguration configuration)
        {
            var transportName =
                configuration[
                    "AiRuntimeInstanceRegistration:ProviderMetadata:transport.name"];

            if (!string.IsNullOrWhiteSpace(transportName))
            {
                return transportName
                    .Trim()
                    .ToLowerInvariant();
            }

            var providerName =
                configuration[
                    "AiRuntimeInstanceRegistration:ProviderName"];

            if (!string.IsNullOrWhiteSpace(providerName))
            {
                return providerName
                    .Trim()
                    .ToLowerInvariant();
            }

            var metadataProviderName =
                configuration[
                    "AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"];

            return string.IsNullOrWhiteSpace(metadataProviderName)
                ? "http"
                : metadataProviderName
                    .Trim()
                    .ToLowerInvariant();
        }
    }
}
