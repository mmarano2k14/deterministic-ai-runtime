using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Payloads.Immutable;
using Multiplexed.AI.Runtime.Invocation.Durable.Dag;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.Policies;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.AI.Runtime.Publication.DI;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Tests.Runtime.Publication;
using Multiplexed.Rbac.Core.Authorization.Engine;
using Multiplexed.Rbac.Core.Authorization.Scope;
using Multiplexed.Rbac.Core.Authorization.Trn;
using Multiplexed.Rbac.Core.ExecutionContext;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.ExecutionRequirements
{
    /// <summary>Reuses the existing publication/RBAC/creation fixture; only the host catalog and execution descriptor are new.</summary>
    internal static class ExecutionRequirementsTestSupport
    {
        internal static AiPublicationExecutionRequirements TrustedRequirements => new()
        {
            IsolationTier = AiWorkerIsolationTier.TrustedProcess,
            NetworkEgress = AiWorkerNetworkEgress.HostNetwork,
            PathProtection = AiWorkerPathProtection.ValidatedPaths
        };

        internal static AiPublicationExecutionDescriptor Descriptor(AiPublicationEnvironment runtime,
            AiPublicationExecutionRequirements? requirements = null) => new()
        {
            OperatingSystem = AiWorkerExecutionAdmission.CurrentOperatingSystem,
            Architecture = AiWorkerExecutionAdmission.CurrentArchitecture,
            Artifact = new(AiPublicationEnvironmentArtifactKind.HostRuntime, "sha256:" + runtime.RuntimeSha256,
                AiPublicationExecutionDescriptors.HostRuntimeMediaType),
            Requirements = requirements ?? TrustedRequirements
        };

        internal sealed class Catalog : IAiPublicationExecutionEnvironmentCatalog
        {
            internal readonly Dictionary<string, AiPublicationEnvironment> Runtimes = new(StringComparer.Ordinal);
            internal readonly Dictionary<string, AiPublicationExecutionDescriptor?> Descriptors = new(StringComparer.Ordinal);
            internal Catalog(bool legacy = false)
            {
                foreach (var language in new[] { "python", "typescript", "dotnet" })
                {
                    var runtime = PublicationTestSupport.Environment(language);
                    Runtimes.Add(runtime.Reference, runtime);
                    Descriptors.Add(runtime.Reference, legacy ? null : Descriptor(runtime));
                }
            }
            public AiPublicationEnvironment? Find(string reference) => Runtimes.GetValueOrDefault(reference);
            public AiPublicationExecutionDescriptor? FindExecutionDescriptor(string reference) => Descriptors.GetValueOrDefault(reference);
        }

        internal sealed class Fixture : IDisposable
        {
            internal readonly PublicationTestSupport.Fixture Base = new();
            internal readonly Catalog Catalog;
            private readonly ServiceProvider _root;
            private readonly IServiceScope _scope;
            internal IServiceProvider Services => _scope.ServiceProvider;
            internal AiPipelinePublicationService Publisher => Services.GetRequiredService<AiPipelinePublicationService>();
            internal AiPublishedDagRunService Runs => Services.GetRequiredService<AiPublishedDagRunService>();
            internal Fixture(bool legacy = false)
            {
                Catalog = new(legacy);
                var services = new ServiceCollection();
                services.AddSingleton<IExecutionContextAccessor>(Base.Accessor);
                services.AddSingleton<IAiControlPlaneIdResolver>(Base.ControlPlane);
                services.AddSingleton<IAiPayloadStoreResolver>(Base.Payloads);
                services.AddSingleton<IAiPublicationEnvironmentCatalog>(Catalog);
                services.AddSingleton<IAiPipelineResolver>(Base.Resolver);
                services.AddSingleton<IAiDagExecutionEngineServices>(Base.EngineServices);
                services.AddSingleton<IAiExecutionStore>(Base.Store);
                services.AddSingleton<IAiExecutionPayloadResolver>(new McpStepTestSupport.PayloadResolver());
                services.AddSingleton(new TrnBuilder(Microsoft.Extensions.Options.Options.Create(new TrnBuilderOptions { Project = "tests" })));
                services.AddScoped<AuthorizationScope>(); services.AddScoped<IAuthorizationEngine, TrnAuthorizationEngine>();
                services.AddScoped<AiImmutableJsonPayloadReader>(); services.AddScoped<AiDurableInvocationDagBinding>();
                services.AddAiImmutablePublications(PublicationTestSupport.Options);
                services.AddScoped<AiWorkerPublicationPreparer>();
                services.AddScoped<AiConcurrencyPolicyPublicationPreparer>();
                _root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
                _scope = _root.CreateScope();
            }
            internal Task<AiPipelinePublication> PublishAsync(AiPipelinePublicationUpload? upload = null) =>
                Base.AsAsync(() => Publisher.PublishAsync(PublicationTestSupport.Scope, upload ?? PublicationTestSupport.Upload()));
            internal Task<AiPipelinePublication> ReadAsync(string reference) => Base.AsAsync(() => Publisher.ReadAsync(PublicationTestSupport.Scope, reference));
            internal Task<AiExecutionRecord> CreateAsync(AiPipelinePublication publication, string key = "requirements-run") =>
                Base.AsAsync(() => Runs.CreateAsync(PublicationTestSupport.Scope, key, publication.PublicationRef, "{\"amount\":1}"));
            internal Task<AiDurableInvocationRecord> PrepareAsync(AiExecutionRecord run) => Base.AsAsync(async () =>
            {
                var request = await Services.GetRequiredService<AiDurableInvocationDagBinding>().ReadAsync(run, PublicationTestSupport.Scope, "first");
                var target = (await Services.GetRequiredService<IAiDurableInvocationTargetResolver>().ResolveAsync(request))!;
                return await Base.Journal.PrepareAsync(new(new(PublicationTestSupport.Scope.TenantId, run.ExecutionId, "first"),
                    PublicationTestSupport.Scope, target, "{\"amount\":1}"));
            });
            internal Task<AiWorkerCodeBundle> CodeAsync(AiDurableInvocationRecord invocation) =>
                Base.AsAsync(() => Services.GetRequiredService<AiWorkerPublicationPreparer>().PrepareAsync(invocation));
            internal AiPublicationEnvironmentSnapshot Snapshot(AiPipelinePublication publication, string step = "first") =>
                JsonSerializer.Deserialize<AiPublicationEnvironmentSnapshot>(
                    Base.MemoryPayloads.Documents[publication.Manifest.Functions.Single(f => f.Site.StepName == step).Environment.Key])!;
            public void Dispose() { _scope.Dispose(); _root.Dispose(); Base.Dispose(); }
        }

        internal sealed class LaunchFixture : IDisposable
        {
            internal readonly string Root;
            internal readonly string Executable;
            internal readonly string Loader;
            internal readonly AiPublicationEnvironment Runtime;
            internal LaunchFixture()
            {
                Root = Path.Combine(Path.GetTempPath(), "execution-paths-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
                Executable = Path.Combine(Root, "host.bin"); Loader = Path.Combine(Root, "loader.bin");
                File.WriteAllText(Executable, "host-approved", Encoding.UTF8);
                File.WriteAllText(Loader, "loader-approved", Encoding.UTF8);
                Runtime = PublicationTestSupport.Environment("python") with { RuntimeSha256 = WorkerTestSupport.FileHash(Executable) };
            }
            internal AiWorkerProcessProfile Profile(AiPublicationExecutionDescriptor? descriptor = null, string? executable = null,
                string? directory = null, IEnumerable<string>? roots = null, IReadOnlyDictionary<string, string>? files = null) => new(
                Runtime, executable ?? Executable, WorkerTestSupport.FileHash(Executable), Array.Empty<string>(), directory ?? Root,
                verifiedHostFiles: files ?? new Dictionary<string, string> { [Loader] = WorkerTestSupport.FileHash(Loader) },
                executionDescriptor: descriptor ?? Descriptor(Runtime), approvedLaunchRoots: roots ?? new[] { Root });
            internal AiWorkerInvocationRequest Request(AiWorkerProcessProfile profile) => WorkerTestSupport.Request() with
            {
                Code = WorkerTestSupport.Bundle() with { Runtime = Runtime, ExecutionDescriptor = profile.ExecutionDescriptor }
            };
            public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        }
    }
}
