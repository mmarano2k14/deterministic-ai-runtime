using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Multiplexed.AI.McpServer.Host.Configuration;
using Multiplexed.Rbac.Core.Runtime;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>Opt-in infrastructure glue for the reproducible external-SDK runtime matrix.</summary>
    public static class MatrixHarnessRegistration
    {
        public static void Configure(IServiceCollection services, IConfiguration configuration)
        {
            var options = configuration.GetSection("AiMatrixHarness").Get<AiMatrixHarnessOptions>()
                ?? new AiMatrixHarnessOptions();
            if (!options.Enabled)
            {
                return;
            }

            services
                .AddAuthentication(authentication =>
                {
                    authentication.DefaultAuthenticateScheme = MatrixStaticBearerAuthenticationHandler.SchemeName;
                    authentication.DefaultChallengeScheme = MatrixStaticBearerAuthenticationHandler.SchemeName;
                    authentication.DefaultScheme = MatrixStaticBearerAuthenticationHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, MatrixStaticBearerAuthenticationHandler>(
                    MatrixStaticBearerAuthenticationHandler.SchemeName,
                    _ => { });
            services.AddAuthorization();

            // Rotation is intentionally disabled only for this harness. The current public SDK transports carry an
            // explicit access-context handle but do not yet negotiate rotated handles across MCP HTTP exchanges.
            services.PostConfigure<ContextRuntimeOptions>(runtime =>
            {
                runtime.EnableRotation = false;
                runtime.AllowClientMaxInFlightOverride = false;
                runtime.AllowClientRotationOverlapOverride = false;
                runtime.MaxInFlightPerContextKey = 16;
            });

            services.AddHostedService<MatrixHarnessBootstrapHostedService>();
        }
    }
}
