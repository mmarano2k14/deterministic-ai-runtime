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

            if (options.ExecutionContextTtlSeconds <= 0)
            {
                throw new InvalidOperationException("AiMatrixHarness:ExecutionContextTtlSeconds must be greater than zero.");
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
                    Trn(options, "execution", "control", "cancel"),
                    Trn(options, "execution", "control", "pause"),
                    Trn(options, "execution", "control", "resume"),
                    Trn(options, "execution", "control", "input"),
                    Trn(options, "replay", "execution", "run"),
                    Trn(options, "mcp-effect", "probe", "invoke")
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
                Namespaces = new List<NamespaceEntry> { namespaceEntry },
                // Store expiry does not populate the TTL copied into durable snapshots and pool bootstrap arguments.
                TtlSeconds = options.ExecutionContextTtlSeconds
            };
            var contextKey = await _contexts.StoreAsync(context).ConfigureAwait(false);
            var containerEnvironmentRefs = _environments.ContainerRuntimes
                .GroupBy(runtime => runtime.ExecutionLanguage, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Single().Reference,
                    StringComparer.OrdinalIgnoreCase);

            var manifest = new
            {
                schemaVersion = 1,
                endpoint = options.PublicEndpoint,
                bearerToken = options.BearerToken,
                accessContext = contextKey,
                accessContextHeader = "X-Access-Context",
                executionContextTtlSeconds = context.TtlSeconds,
                topology = options.Topology,
                provider = options.Provider,
                runtimeProvider = options.RuntimeProvider,
                workerExecutionProvider = options.WorkerExecutionProvider,
                effectProbeStateEndpoint = options.EffectProbeStateEndpoint,
                effectEvidenceEndpoint = options.EffectEvidenceEndpoint,
                recoveryEndpoint = options.RecoveryEndpoint,
                journalResultAcceptanceEndpoint = options.JournalResultAcceptanceEndpoint,
                environmentRefs = new
                {
                    dotnet = EnvironmentReference(options.DotNetEnvironmentRef, _environments.DotNetReference),
                    typescript = EnvironmentReference(options.TypeScriptEnvironmentRef, _environments.TypeScriptReference),
                    python = EnvironmentReference(options.PythonEnvironmentRef, _environments.PythonReference)
                },
                containerEnvironmentRefs
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

        private static string EnvironmentReference(string? configured, string fallback) =>
            string.IsNullOrWhiteSpace(configured) ? fallback : configured;

        private static string Trn(AiMatrixHarnessOptions options, string resource, string feature, string action) =>
            $"trn:{options.Project}:{options.Namespace}:{resource}:{feature}:{action}";
    }
}
