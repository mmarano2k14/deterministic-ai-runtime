using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Execution.Payloads.Stores;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.Abstractions.Runtime;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Retry;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.Runtime.AI.Policies;
using Multiplexed.AI.Runtime.AI.Retry;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Engine.Helpers;
using Multiplexed.AI.Runtime.Execution.Engine.Local;
using Multiplexed.AI.Runtime.Execution.Payloads.Immutable;
using Multiplexed.AI.Runtime.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Durable.Dag;
using Multiplexed.AI.Runtime.Observability;
using Multiplexed.AI.Runtime.Observability.Context;
using Multiplexed.AI.Runtime.Observability.Tracing;
using Multiplexed.AI.Runtime.Pipeline;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.AI.Runtime.Publication.DI;
using Multiplexed.AI.Stores.Memory;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.AI.Tests.Runtime.Invocation;
using Multiplexed.AI.Tests.Runtime.Invocation.Durable;
using Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag;
using Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support;
using Multiplexed.Rbac.Core.Authorization.Engine;
using Multiplexed.Rbac.Core.Authorization.Scope;
using Multiplexed.Rbac.Core.Authorization.Trn;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime;
using RbacContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.AI.Tests.Runtime.Publication
{
    /// <summary>Real publication, RBAC, pinned creation and DAG logic; test-only stores and platform dependencies.</summary>
    internal static class PublicationTestSupport
    {
        internal static AiDurableInvocationScope Scope => new("tenant-a", "group-a", "control-a");
        internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        internal static AiPublicationOptions Options => new(new("code", "publication", "publish"),
            new("code", "publication", "read"), new("code", "publication", "execute"));
        internal static AiPublicationEnvironment Environment(string language) => new(language + "-fixed", language, "1.0.0", Hash("runtime-" + language));
        internal static RbacContext Identity(string[]? actions = null, string tenant = "tenant-a", string group = "group-a",
            string project = "tests", string ns = "default", string user = "user-a") => new()
        {
            ContextKey = "publication-context", Project = project, UserId = user, TenantId = tenant, TenantGroupId = group,
            CurrentNamespace = ns, Namespaces = new() { new NamespaceEntry { Name = ns,
                Trns = new HashSet<string>((actions ?? new[] { "publish", "read", "execute" })
                    .Select(a => $"trn:tests:{ns}:code:publication:{a}"), StringComparer.Ordinal) } }
        };
        internal static AiPipelineDefinition Definition(string version = "1", string language = "python", string? secondLanguage = "typescript",
            IReadOnlyDictionary<string, object?>? pipelineConfig = null) => new()
        {
            Name = "published-analysis", Version = version, ExecutionLanguage = language, ExecutionMode = AiExecutionMode.Dag,
            Config = pipelineConfig ?? new Dictionary<string, object?>(),
            Steps = new[] { Step("first", 0), Step("second", 1, secondLanguage) }
        };
        internal static AiPipelineStepDefinition Step(string name, int order = 0, string? language = null,
            IReadOnlyDictionary<string, object?>? config = null) => new()
        {
            Name = name, StepKey = "code-" + name, Order = order, ExecutionLanguage = language,
            DependsOn = order == 0 ? Array.Empty<string>() : new[] { "first" },
            Invocation = new AiInvocationDefinition { Kind = AiInvocationKind.Custom },
            Input = new Dictionary<string, object?> { ["amount"] = 1 },
            Config = config ?? new Dictionary<string, object?> { ["retry"] = new AiRetryPolicyDefinition
                { MaxRetries = 0, Policies = new List<AiConfiguredPolicyDefinition>() } }
        };
        internal static AiPipelineDefinition Copy(AiPipelineDefinition source, IReadOnlyCollection<AiPipelineStepDefinition>? steps = null,
            IReadOnlyDictionary<string, object?>? config = null, AiExecutionMode? mode = null) => new()
        {
            Name = source.Name, Version = source.Version, ExecutionLanguage = source.ExecutionLanguage,
            ExecutionMode = mode ?? source.ExecutionMode, Steps = steps ?? source.Steps, Config = config ?? source.Config
        };
        internal static AiPublicationFunctionUpload Function(AiPublicationCallSite site, string language = "python", string revision = "1") => new(
            site, language + "-fixed", "main.txt", "run", new[] { new AiPublicationFileUpload("main.txt", Encoding.UTF8.GetBytes("code-" + revision)) },
            new[] { new AiPublicationDependencyUpload("example", "1.0.0", new[] {
                new AiPublicationFileUpload("dependency.dat", Encoding.UTF8.GetBytes("dependency-" + revision)) }) });
        internal static AiPipelinePublicationUpload Upload(string revision = "1", string language = "python", string? secondLanguage = "typescript") =>
            new(Definition(revision, language, secondLanguage), new[] {
                Function(new(AiPublicationFunctionKind.Step, "first"), language, revision),
                Function(new(AiPublicationFunctionKind.Step, "second"), secondLanguage ?? language, revision) });

        internal sealed class PayloadResolver : IAiPayloadStoreResolver
        {
            internal IAiPayloadStore Store;
            internal PayloadResolver(IAiPayloadStore store) => Store = store;
            public IAiPayloadStore Resolve() => Store;
        }
        internal sealed class PayloadStore : IAiImmutablePayloadStore
        {
            internal readonly ConcurrentDictionary<string, string> Documents = new(StringComparer.Ordinal);
            internal readonly ConcurrentQueue<string> Writes = new();
            internal int Reads;
            internal Func<string, string, Task>? BeforeWrite;
            internal Func<string, string, Task>? AfterWrite;
            internal Func<string, string?, string?>? OnRead;
            internal string Export() => JsonSerializer.Serialize(Documents);
            internal void Restore(string json)
            { Documents.Clear(); foreach (var item in JsonSerializer.Deserialize<Dictionary<string, string>>(json)!) Documents[item.Key] = item.Value; }
            public Task<string> SaveAsync(string content, CancellationToken cancellationToken = default) => throw new NotSupportedException("Use the immutable capability.");
            public async Task<string> SaveImmutableAsync(string key, string content, AiPayloadMetadata metadata, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (BeforeWrite is not null) await BeforeWrite(key, content);
                cancellationToken.ThrowIfCancellationRequested();
                if (Documents.GetOrAdd(key, content) != content) throw new InvalidOperationException("Conflicting immutable write.");
                Writes.Enqueue(key);
                if (AfterWrite is not null) await AfterWrite(key, content);
                cancellationToken.ThrowIfCancellationRequested(); return key;
            }
            public Task<string?> LoadAsync(string key, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref Reads);
                Documents.TryGetValue(key, out var value); return Task.FromResult(OnRead is null ? value : OnRead(key, value));
            }
            public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            { cancellationToken.ThrowIfCancellationRequested(); Documents.TryRemove(key, out _); return Task.CompletedTask; }
        }
        internal sealed class Catalog : IAiPublicationEnvironmentCatalog
        {
            internal readonly Dictionary<string, AiPublicationEnvironment> Entries = new(StringComparer.Ordinal);
            internal Catalog() { foreach (var language in new[] { "python", "typescript", "dotnet" }) Entries.Add(language + "-fixed", Environment(language)); }
            public AiPublicationEnvironment? Find(string reference) => Entries.GetValueOrDefault(reference);
        }
        internal sealed class NativeStep : IAiStep
        {
            internal int Calls; public string Name => "native";
            public Task<AiStepResult> ExecuteAsync(AiStepExecutionContext context, CancellationToken cancellationToken = default)
            { Calls++; return Task.FromResult(AiStepResult.Ok(value: 7)); }
        }
        internal sealed class Registry : IAiStepRegistry
        {
            internal readonly NativeStep Native = new();
            public IAiStep Resolve(string key) => key == "native" || key == "execution.child-dag" ? Native
                : throw new InvalidOperationException("No native fallback is permitted.");
        }

        internal sealed class Fixture : IDisposable
        {
            internal readonly PayloadStore MemoryPayloads;
            internal readonly PayloadResolver Payloads;
            internal readonly Catalog Environments = new();
            internal readonly ExecutionContextAccessor Accessor = new();
            internal readonly Registry Registry = new();
            internal readonly MemoryAiExecutionStore Store = new();
            internal readonly DurableInvocationTestSupport.MemoryStore JournalStore;
            internal readonly DurableInvocationTestSupport.Clock Clock;
            internal readonly AiDurableInvocationJournal Journal;
            internal readonly RbacContext Live = Identity();
            internal readonly ServiceProvider Root;
            internal readonly IServiceScope ServiceScope;
            internal IServiceProvider Services => ServiceScope.ServiceProvider;
            internal AiPipelinePublicationService Publisher => Services.GetRequiredService<AiPipelinePublicationService>();
            internal AiPublishedDagRunService Runs => Services.GetRequiredService<AiPublishedDagRunService>();
            internal IAiDurableInvocationTargetResolver Targets => Services.GetRequiredService<IAiDurableInvocationTargetResolver>();
            internal readonly AiPipelineResolver Resolver;
            internal readonly StaticAiControlPlaneIdResolver ControlPlane = new(Scope.ControlPlaneId);
            internal int ContextSeeds;
            internal int LatestLookups;
            internal bool FailContextSeed;
            internal Action? BeforeDefinitionResolution;
            internal IAiDagExecutionEngineServices EngineServices => Services.GetRequiredService<IAiDagExecutionEngineServices>();

            internal Fixture(AiPublicationOptions? options = null, IAiPayloadStore? payloadStore = null)
            {
                MemoryPayloads = payloadStore as PayloadStore ?? new PayloadStore();
                Payloads = new PayloadResolver(payloadStore ?? MemoryPayloads);
                (Journal, JournalStore, Clock) = DurableInvocationTestSupport.Create();
                Resolver = new AiPipelineResolver(Registry, new[] { new AiDurableInvocationStepAdapterFactory("python"),
                    new AiDurableInvocationStepAdapterFactory("typescript"), new AiDurableInvocationStepAdapterFactory("dotnet") });
                var services = new ServiceCollection();
                services.AddSingleton<IExecutionContextAccessor>(Accessor);
                services.AddSingleton<IAiControlPlaneIdResolver>(ControlPlane);
                services.AddSingleton<IAiPayloadStoreResolver>(Payloads);
                services.AddSingleton<IAiPublicationEnvironmentCatalog>(Environments);
                services.AddSingleton<IAiPipelineResolver>(Resolver);
                services.AddSingleton(new TrnBuilder(Microsoft.Extensions.Options.Options.Create(new TrnBuilderOptions { Project = "tests" })));
                services.AddScoped<AuthorizationScope>(); services.AddScoped<IAuthorizationEngine, TrnAuthorizationEngine>();
                services.AddSingleton(Journal);
                services.AddSingleton<IAiExecutionPayloadResolver>(new McpStepTestSupport.PayloadResolver());
                services.AddScoped<AiImmutableJsonPayloadReader>(); services.AddScoped<AiDurableInvocationDagBinding>();
                services.AddScoped<IAiDagExecutionEngineServices>(BuildEngineServices);
                services.AddAiImmutablePublications(options ?? PublicationTestSupport.Options);
                Root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
                ServiceScope = Root.CreateScope();
            }
            internal async Task<T> AsAsync<T>(Func<Task<T>> action, RbacContext? live = null)
            {
                var previous = Accessor.Current; Accessor.Set(live ?? Live);
                try { return await action(); }
                finally { if (previous is null) Accessor.Clear(); else Accessor.Set(previous); }
            }
            internal Task<AiPipelinePublication> PublishAsync(AiPipelinePublicationUpload? upload = null) =>
                AsAsync(() => Publisher.PublishAsync(Scope, upload ?? Upload()));
            internal Task<AiPipelinePublication> ReadAsync(string publicationRef) => AsAsync(() => Publisher.ReadAsync(Scope, publicationRef));
            internal Task<AiExecutionRecord> CreateAsync(AiPipelinePublication publication, string key = "run-a", string inputs = "{\"amount\":1}") =>
                AsAsync(() => Runs.CreateAsync(Scope, key, publication.PublicationRef, inputs));
            internal Task<AiDurableInvocationTarget?> TargetAsync(AiExecutionRecord record, string name = "second") => AsAsync(async () =>
            {
                var request = await Services.GetRequiredService<AiDurableInvocationDagBinding>().ReadAsync(record, Scope, name);
                return await Targets.ResolveAsync(request);
            });
            internal AiExecutionContext Build(AiExecutionRecord record, AiExecutionState state, CancellationToken ct)
            {
                var baseline = ChildDagCompositionTestData.CreateExecutionContext(record, state);
                return new AiExecutionContext(record, state, Services, baseline.StateReader, baseline.StateWriter, ct);
            }
            internal Task<AiExecutionRecord> RunNextAsync(string executionId) => AsAsync(async () =>
            {
                var engine = EngineServices;
                var publisherType = typeof(AiDagLocalExecutionRunner).GetConstructors().Single().GetParameters()[2].ParameterType;
                var runner = (AiDagLocalExecutionRunner)Activator.CreateInstance(typeof(AiDagLocalExecutionRunner), engine,
                    new AiDagExecutionLifecycleHelper(engine), DagTestProxy.Noop(publisherType), ControlPlane)!;
                return await runner.ExecuteNextAsync(executionId,
                    async (id, ct) => ((await Store.GetRecordAsync(id, ct))!, (await Store.GetStateAsync(id, ct))!),
                    _ => Task.CompletedTask, Build,
                    async (record, expected, state, ct) => Assert.True(await Store.TryUpdateAsync(record.ExecutionId, expected, record, state, ct)),
                    _ => { }, _ => { });
            });
            internal async Task CompleteAsync(string executionId, string stepName)
            {
                var identity = new AiDurableInvocationIdentity(Scope.TenantId, executionId, stepName);
                var lease = (await Journal.TryAcquireLeaseAsync(Scope, identity, "test-worker", TimeSpan.FromSeconds(30)))!;
                Assert.Equal(AiDurableInvocationCompletionStatus.Accepted,
                    await Journal.CompleteAsync(Scope, identity, lease.Lease!, new AiDurableInvocationResult(true, "{\"value\":42}")));
                await new AiDagExecutionEngine(EngineServices, DagTestProxy.Noop<IAiDagExecutionEngineRuntimeServices>())
                    .ResumeExternalWaitingStepAsync(executionId, stepName);
            }
            private IAiDagExecutionEngineServices BuildEngineServices(IServiceProvider services)
            {
                var baseline = ChildDagCompositionTestData.CreateExecutionContext(new AiExecutionRecord(), new AiExecutionState());
                var logger = new NoopLogger();
                var runtimeInstanceIdentity = DagTestProxy.Create<IAiRuntimeInstanceIdentityDescriptor>((_, _) => "runtime-a");
                // Exact creation reads Correlation.Current while resolving each step's retry definition.
                var observability = new AiRuntimeObservability(DagTestProxy.Noop<IAiRuntimeMetrics>(), new NoOpAiRuntimeTracer(), logger,
                    new NoOpAiDecisionLedgerRecorder(), new AsyncLocalAiRuntimeCorrelationAccessor(runtimeInstanceIdentity));
                var options = new AiEngineOptions(); options.Snapshots.Enabled = false;
                options.Cleanup.AutoCleanupOnCompleted = false; options.Cleanup.AutoCleanupOnFailed = false;
                var values = new Dictionary<string, object?>
                {
                    ["Store"] = Store, ["DagStore"] = null, ["Accessor"] = Accessor, ["Services"] = services,
                    ["ContextFactory"] = new ExecutionContextFactory(),
                    ["ContextStore"] = DagTestProxy.Create<IContextStore>((method, args) =>
                    {
                        if (method.Name != "SeedAsync") throw new NotSupportedException(method.Name);
                        ContextSeeds++; if (FailContextSeed) { FailContextSeed = false; throw new IOException("Injected interruption before DAG creation."); }
                        return Task.FromResult(((RbacContext)args![0]!).ContextKey);
                    }),
                    ["PipelineExecutor"] = DagTestProxy.Create<IAiSequentialPipelineExecutor>((method, args) =>
                    {
                        if (method.Name != "PrepareAsync" || args![0] is not AiPipelineDefinition definition)
                        { LatestLookups++; throw new InvalidOperationException("Published runs cannot consult the mutable provider."); }
                        BeforeDefinitionResolution?.Invoke(); return Resolver.ResolveAsync(definition, (CancellationToken)args[1]!);
                    }),
                    ["Logger"] = logger, ["StateReader"] = baseline.StateReader, ["StateWriter"] = baseline.StateWriter,
                    ["PayloadStoreResolver"] = Payloads, ["StepResolver"] = new DurableInvocationDagTestSupport.FullSteps(),
                    ["AiOptions"] = Microsoft.Extensions.Options.Options.Create(options), ["SnapshotService"] = null,
                    ["PolicyEngineFactory"] = DagTestProxy.Create<IAiPolicyEngineFactory>((_, args) =>
                        new DefaultAiRetryEngine(new DefaultAiPolicyRegistry(Array.Empty<IAiPolicy>()), (AiStepExecutionContext)args![1]!, observability)),
                    ["ObservabilityService"] = observability, ["PayloadCompactor"] = DagTestProxy.Noop<IAiStepResultPayloadCompactor>(),
                    ["RuntimeInstanceIdentity"] = runtimeInstanceIdentity
                };
                return DagTestProxy.Create<IAiDagExecutionEngineServices>((method, _) =>
                    values.TryGetValue(method.Name[4..], out var value) ? value : throw new NotSupportedException(method.Name));
            }
            public void Dispose() { ServiceScope.Dispose(); Root.Dispose(); }
        }
    }
}
