using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.McpServer.Host.Configuration;
using Multiplexed.Rbac.Core.ExecutionContext;
using RbacExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>Seeds one isolated RBAC context and publishes non-secret runtime-discovery evidence for matrix clients.</summary>
    public sealed class MatrixHarnessBootstrapHostedService : IHostedService
    {
        private readonly IContextStore _contexts;
        private readonly IConfiguration _configuration;
        private readonly AiHostedInvocationEnvironmentSet _environments;

        public MatrixHarnessBootstrapHostedService(
            IContextStore contexts,
            IConfiguration configuration,
            AiHostedInvocationEnvironmentSet environments)
        {
            _contexts = contexts;
            _configuration = configuration;
            _environments = environments;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var options = _configuration.GetSection("AiMatrixHarness").Get<AiMatrixHarnessOptions>()
                ?? new AiMatrixHarnessOptions();
            if (!options.Enabled)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(options.ManifestPath) || string.IsNullOrWhiteSpace(options.BearerToken))
            {
                throw new InvalidOperationException("AiMatrixHarness requires an explicit manifest path and bearer token.");
            }

            var namespaceEntry = new NamespaceEntry
            {
                Name = options.Namespace,
                Trns = new HashSet<string>(new[]
                {
                    Trn(options, "code", "publication", "publish"),
                    Trn(options, "code", "publication", "read"),
                    Trn(options, "code", "publication", "execute"),
                    Trn(options, "shared-run", "execution", "submit"),
                    Trn(options, "execution", "control", "read"),
                    Trn(options, "execution", "control", "cancel")
                }, StringComparer.Ordinal)
            };
            var context = new RbacExecutionContext
            {
                ContextKey = string.Empty,
                Project = options.Project,
                UserId = options.UserId,
                TenantId = options.TenantId,
                TenantGroupId = options.TenantGroupId,
                CurrentNamespace = options.Namespace,
                Namespaces = new List<NamespaceEntry> { namespaceEntry }
            };
            var contextKey = await _contexts.StoreAsync(context).ConfigureAwait(false);

            var manifest = new
            {
                schemaVersion = 1,
                endpoint = options.PublicEndpoint,
                bearerToken = options.BearerToken,
                accessContext = contextKey,
                accessContextHeader = "X-Access-Context",
                topology = options.Topology,
                provider = options.Provider,
                environmentRefs = new
                {
                    dotnet = _environments.DotNetReference,
                    typescript = _environments.TypeScriptReference,
                    python = _environments.PythonReference
                }
            };
            var path = Path.GetFullPath(options.ManifestPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private static string Trn(AiMatrixHarnessOptions options, string resource, string feature, string action) =>
            $"trn:{options.Project}:{options.Namespace}:{resource}:{feature}:{action}";
    }
}
