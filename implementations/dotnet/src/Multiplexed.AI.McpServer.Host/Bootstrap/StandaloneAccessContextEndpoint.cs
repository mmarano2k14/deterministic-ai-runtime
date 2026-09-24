using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.McpServer.Host.Configuration;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime;
using RbacExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Creates an RBAC execution-context handle from claims on an already authenticated principal.
    /// </summary>
    public static class StandaloneAccessContextEndpoint
    {
        public static void Configure(WebApplication app)
        {
            ArgumentNullException.ThrowIfNull(app);

            var matrix = app.Configuration
                .GetSection("AiMatrixHarness")
                .Get<AiMatrixHarnessOptions>()
                ?? new AiMatrixHarnessOptions();

            if (matrix.Enabled)
            {
                return;
            }

            var options = app.Services
                .GetRequiredService<IOptions<AiMcpAuthenticationOptions>>()
                .Value;

            if (!options.Enabled)
            {
                return;
            }

            app.MapPost(
                    options.AccessContextPath,
                    async (
                        HttpContext http,
                        IContextStore contexts,
                        IOptions<ContextRuntimeOptions> runtimeOptions) =>
                    {
                        if (!StandaloneAccessContextClaims.TryCreate(
                                http.User,
                                options,
                                out var claims,
                                out var error))
                        {
                            return Results.Problem(
                                title: "Access context could not be created.",
                                detail: error,
                                statusCode: StatusCodes.Status403Forbidden);
                        }

                        var namespaceEntry = new NamespaceEntry
                        {
                            Name = claims!.Namespace,
                            Trns = new HashSet<string>(
                                claims.Trns,
                                StringComparer.Ordinal)
                        };

                        var context = new RbacExecutionContext
                        {
                            ContextKey = string.Empty,
                            Project = claims.Project,
                            UserId = claims.UserId,
                            TenantId = claims.TenantId,
                            TenantGroupId = claims.TenantGroupId,
                            CurrentNamespace = claims.Namespace,
                            Namespaces = new List<NamespaceEntry>
                            {
                                namespaceEntry
                            },
                            TtlSeconds = options.ExecutionContextTtlSeconds
                        };

                        var contextKey = await contexts
                            .StoreAsync(context)
                            .ConfigureAwait(false);

                        var accessContextHeader =
                            runtimeOptions.Value.AccessContextHeader;

                        http.Response.Headers["Cache-Control"] = "no-store";
                        http.Response.Headers[accessContextHeader] = contextKey;

                        return Results.Ok(new
                        {
                            accessContext = contextKey,
                            accessContextHeader,
                            executionContextTtlSeconds = context.TtlSeconds,
                            userId = claims.UserId,
                            tenantId = claims.TenantId,
                            tenantGroupId = claims.TenantGroupId,
                            project = claims.Project,
                            @namespace = claims.Namespace
                        });
                    })
                .RequireAuthorization();
        }
    }
}
