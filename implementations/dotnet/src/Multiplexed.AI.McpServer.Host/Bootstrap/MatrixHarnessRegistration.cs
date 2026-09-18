using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Multiplexed.AI.McpServer.DependencyInjection;
using Multiplexed.AI.McpServer.Host.Configuration;
using Multiplexed.AI.McpServer.Invocation.Outbound;
using Multiplexed.AI.Runtime.Invocation.Mcp;
using Multiplexed.AI.Runtime.Invocation.Mcp.Durable.DI;
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

            if (!Uri.TryCreate(options.EffectProbeMcpEndpoint, UriKind.Absolute, out var effectProbeEndpoint))
            {
                throw new InvalidOperationException("AiMatrixHarness requires an absolute MCP effect probe endpoint.");
            }

            services.AddAiDurableMcpEffectEvidence();
            services.AddAiOutboundMcpToolExecution(
                new AiOutboundMcpToolExecutionOptions
                {
                    AllowUnencryptedLoopback = true,
                    ConnectionTimeout = TimeSpan.FromSeconds(2),
                    Connections =
                    [
                        new AiOutboundMcpConnectionRegistration
                        {
                            TenantId = options.TenantId,
                            TenantGroupId = options.TenantGroupId,
                            ConnectionRef = "matrix-effect-probe",
                            Revision = "v1",
                            Endpoint = effectProbeEndpoint,
                            Tools =
                            [
                                new AiOutboundMcpToolRegistration("probe.fail-count", "mcp-effect", "probe", "invoke"),
                                new AiOutboundMcpToolRegistration("probe.slow-count", "mcp-effect", "probe", "invoke")
                            ]
                        }
                    ]
                },
                new AiMcpStepInvocationOptions
                {
                    InvocationTimeout = TimeSpan.FromSeconds(2)
                });

            services.AddSingleton<MatrixRecoveryProbe>();
            services.AddSingleton<MatrixJournalResultAcceptanceProbe>();
            services.AddHostedService<MatrixHarnessBootstrapHostedService>();
        }
    }
}
