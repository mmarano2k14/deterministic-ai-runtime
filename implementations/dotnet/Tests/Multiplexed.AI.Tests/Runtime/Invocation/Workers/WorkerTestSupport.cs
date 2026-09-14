using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.DI;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.AI.Runtime.Publication.DI;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.AI.Tests.Runtime.Invocation.Durable;
using Multiplexed.AI.Tests.Runtime.Publication;
using Multiplexed.Rbac.Core.Authorization.Engine;
using Multiplexed.Rbac.Core.Authorization.Scope;
using Multiplexed.Rbac.Core.Authorization.Trn;
using Multiplexed.Rbac.Core.ExecutionContext;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers
{
    /// <summary>Small transport doubles and the already-characterized journal/publication fixtures.</summary>
    internal static class WorkerTestSupport
    {
        internal static AiWorkerCodeBundle Bundle(AiDurableInvocationTarget? target = null)
        {
            target ??= DurableInvocationTestSupport.Definition().Target;
            return new(target, PublicationTestSupport.Environment(target.ExecutionLanguage), "main.txt", "run",
                new[] { new AiWorkerFile("main.txt", PublicationTestSupport.Hash("code-1"), 6, "Y29kZS0x") },
                Array.Empty<AiWorkerDependency>());
        }
        internal static AiWorkerInvocationRequest Request() => new(1, "invoke", "request-a", "operation-a",
            "effect-a", "worker-a", 1, "tenant-a", "execution-a", "analyze", 0, DateTimeOffset.UtcNow.AddMinutes(1), null,
            JsonSerializer.SerializeToElement(new { amount = 1 }), Bundle());
        internal static string Frame(AiWorkerInvocationRequest request, string type = "result", bool success = true, object? payload = null)
        {
            var fields = new Dictionary<string, object?> { ["protocolVersion"] = 1, ["type"] = type,
                ["requestId"] = request.RequestId, ["operationId"] = request.OperationId,
                ["workerId"] = request.WorkerId, ["epoch"] = request.Epoch };
            if (type == "result") { fields["success"] = success; fields["payload"] = payload ?? new { value = 42 }; }
            return JsonSerializer.Serialize(fields);
        }
        internal sealed class Preparer : IAiWorkerInvocationPreparer
        {
            internal int Calls;
            internal Func<AiDurableInvocationRecord, CancellationToken, Task<AiWorkerCodeBundle>> Body =
                (record, token) => Task.FromResult(Bundle(record.Definition.Target));
            public Task<AiWorkerCodeBundle> PrepareAsync(AiDurableInvocationRecord record, CancellationToken cancellationToken = default)
            { Interlocked.Increment(ref Calls); return Body(record, cancellationToken); }
        }
        internal sealed class Transport : IAiWorkerInvocationTransport
        {
            internal readonly ConcurrentQueue<AiWorkerInvocationRequest> Requests = new();
            internal Func<AiWorkerInvocationRequest, Func<CancellationToken, Task>, CancellationToken, Task<AiDurableInvocationResult>> Body =
                (_, _, _) => Task.FromResult(new AiDurableInvocationResult(true, "{\"value\":42}"));
            public Task<AiDurableInvocationResult> InvokeAsync(AiWorkerInvocationRequest request,
                Func<CancellationToken, Task> heartbeat, CancellationToken cancellationToken = default)
            { Requests.Enqueue(request); return Body(request, heartbeat, cancellationToken); }
        }
        internal sealed class Fixture : IDisposable
        {
            internal readonly DurableInvocationTestSupport.Clock Clock;
            internal readonly DurableInvocationTestSupport.MemoryStore Store;
            internal readonly AiDurableInvocationJournal Journal;
            internal readonly Preparer Preparer = new();
            internal readonly Transport Transport = new();
            internal readonly AiWorkerProcessCapacity Capacity;
            internal readonly AiWorkerInvocationSupervisor Supervisor;
            internal readonly AiWorkerSupervisionOptions Options;
            internal Fixture(AiWorkerSupervisionOptions? options = null)
            {
                Options = options ?? new(); (Journal, Store, Clock) = DurableInvocationTestSupport.Create();
                Capacity = new(Options);
                Supervisor = new(Journal, Preparer, Transport, new StaticAiControlPlaneIdResolver("control-a"),
                    Capacity, Options, NullLogger<AiWorkerInvocationSupervisor>.Instance, Clock);
            }
            internal Task<AiDurableInvocationRecord> PrepareAsync(AiDurableInvocationDefinition? definition = null) => Journal.PrepareAsync(definition ?? DurableInvocationTestSupport.Definition());
            internal Task<AiWorkerDispatchResult> DispatchAsync(CancellationToken token = default) =>
                Supervisor.DispatchAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity, token);
            internal async Task<AiDurableInvocationRecord> ReadAsync() => (await Journal.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity))!;
            public void Dispose() => Capacity.Dispose();
        }
        internal sealed class PagedStore : IAiDurableInvocationStore, IAiDurableInvocationDispatchPageStore
        {
            internal readonly DurableInvocationTestSupport.MemoryStore Inner;
            internal Func<IReadOnlyList<AiDurableInvocationRecord>, IReadOnlyList<AiDurableInvocationRecord>>? Mutate;
            internal PagedStore(DurableInvocationTestSupport.MemoryStore inner) => Inner = inner;
            public Task<AiDurableInvocationRecord?> GetAsync(AiDurableInvocationScope s, AiDurableInvocationIdentity i, CancellationToken c = default) => Inner.GetAsync(s, i, c);
            public Task<AiDurableInvocationRecord> GetOrCreateAsync(AiDurableInvocationRecord r, CancellationToken c = default) => Inner.GetOrCreateAsync(r, c);
            public Task<bool> TryReplaceAsync(AiDurableInvocationRecord a, AiDurableInvocationRecord b, CancellationToken c = default) => Inner.TryReplaceAsync(a, b, c);
            public Task<IReadOnlyList<AiDurableInvocationRecord>> ListDispatchCandidatesAsync(AiDurableInvocationScope s, string l, DateTimeOffset n, int m, CancellationToken c = default) => Inner.ListDispatchCandidatesAsync(s, l, n, m, c);
            public Task<IReadOnlyList<AiDurableInvocationRecord>> ListContinuationCandidatesAsync(AiDurableInvocationScope s, int m, CancellationToken c = default) => Inner.ListContinuationCandidatesAsync(s, m, c);
            public Task<IReadOnlyList<AiDurableInvocationRecord>> ListDispatchPageAsync(AiDurableInvocationScope scope, string language,
                DateTimeOffset now, int max, AiWorkerDispatchCursor? after = null, CancellationToken token = default)
            {
                token.ThrowIfCancellationRequested();
                IReadOnlyList<AiDurableInvocationRecord> page = JsonSerializer.Deserialize<AiDurableInvocationRecord[]>(Inner.Export())!
                    .Where(r => r.Definition.Scope == scope && r.Definition.Target.ExecutionLanguage == language &&
                        (r.Status == AiDurableInvocationStatus.Prepared || r.Status == AiDurableInvocationStatus.Leased && r.Lease!.ExpiresAtUtc <= now) &&
                        (after is null || r.UpdatedAtUtc > after.UpdatedAtUtc || r.UpdatedAtUtc == after.UpdatedAtUtc && string.CompareOrdinal(r.OperationId, after.OperationId) > 0))
                    .OrderBy(r => r.UpdatedAtUtc).ThenBy(r => r.OperationId, StringComparer.Ordinal).Take(max).ToArray();
                return Task.FromResult(Mutate is null ? page : Mutate(page));
            }
        }
        internal sealed class PublishedFixture : IDisposable
        {
            internal readonly PublicationTestSupport.Fixture Publication = new();
            internal readonly ServiceProvider Root;
            internal readonly IServiceScope Scope;
            internal IAiWorkerInvocationPreparer Preparer => Scope.ServiceProvider.GetRequiredService<IAiWorkerInvocationPreparer>();
            internal PublishedFixture()
            {
                var p = Publication; var services = new ServiceCollection(); services.AddLogging();
                services.AddSingleton<IExecutionContextAccessor>(p.Accessor);
                services.AddSingleton<IAiControlPlaneIdResolver>(p.ControlPlane);
                services.AddSingleton<IAiExecutionStore>(p.Store);
                services.AddSingleton<IAiPayloadStoreResolver>(p.Payloads);
                services.AddSingleton<IAiPublicationEnvironmentCatalog>(p.Environments);
                services.AddSingleton<IAiPipelineResolver>(p.Resolver);
                services.AddSingleton<IAiDagExecutionEngineServices>(p.EngineServices);
                services.AddSingleton<IAiDurableInvocationStore>(new PagedStore(p.JournalStore));
                services.AddSingleton(p.Journal); services.AddSingleton<TimeProvider>(p.Clock);
                services.AddSingleton(new TrnBuilder(Microsoft.Extensions.Options.Options.Create(new TrnBuilderOptions { Project = "tests" })));
                services.AddScoped<AuthorizationScope>(); services.AddScoped<IAuthorizationEngine, TrnAuthorizationEngine>();
                services.AddAiImmutablePublications(PublicationTestSupport.Options);
                services.AddAiHostedInvocationWorkers(new AiConfiguredWorkerProcessCatalog(Array.Empty<AiWorkerProcessProfile>()), new());
                Root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
                Scope = Root.CreateScope();
            }
            internal async Task<(AiExecutionRecord Parent, AiDurableInvocationRecord Invocation)> PrepareAsync(string language = "python")
            {
                var published = await Publication.PublishAsync(PublicationTestSupport.Upload(language: language));
                var run = await Publication.CreateAsync(published);
                await Publication.RunNextAsync(run.ExecutionId);
                var invocation = (await Publication.Journal.GetAsync(PublicationTestSupport.Scope,
                    new AiDurableInvocationIdentity(PublicationTestSupport.Scope.TenantId, run.ExecutionId, "first")))!;
                Assert.NotNull(invocation);
                return ((await Publication.Store.GetRecordAsync(run.ExecutionId))!, invocation);
            }
            public void Dispose() { Scope.Dispose(); Root.Dispose(); Publication.Dispose(); }
        }
        internal static string ProbePath => Path.Combine(AppContext.BaseDirectory, "worker-probe", "Multiplexed.AI.WorkerProtocol.Probe.dll");
        internal static string DotnetPath()
        {
            var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
            var root = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()).Parent!.Parent!.Parent!.FullName;
            var path = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (!File.Exists(path)) throw new FileNotFoundException("Set DOTNET_HOST_PATH to the installed .NET host for real worker-process tests.", path);
            return path;
        }
        internal static string FileHash(string path)
        { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
        internal static AiWorkerProcessProfile ProbeProfile(string mode = "success", string language = "python")
        {
            if (!File.Exists(ProbePath)) throw new FileNotFoundException("Build the test project to produce the independent worker protocol probe.", ProbePath);
            var dotnet = DotnetPath();
            var environment = new Dictionary<string, string> { ["WORKER_PROBE_VALUE"] = "explicit-only", ["DOTNET_EnableDiagnostics"] = "0" };
            if (OperatingSystem.IsWindows()) environment["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")
                ?? throw new InvalidOperationException("Windows worker tests require the explicit SystemRoot environment variable.");
            return new(PublicationTestSupport.Environment(language), dotnet, FileHash(dotnet), new[] { ProbePath, mode },
                Path.GetDirectoryName(ProbePath)!, environment, new Dictionary<string, string> { [ProbePath] = FileHash(ProbePath) });
        }
        internal static AiWorkerProcessTransport ProbeTransport(string mode = "success", AiWorkerProcessTransportOptions? options = null) =>
            new(new AiConfiguredWorkerProcessCatalog(new[] { ProbeProfile(mode) }), options ?? new AiWorkerProcessTransportOptions());
    }
}
