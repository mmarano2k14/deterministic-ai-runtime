using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.Invocation.Mcp;
using Multiplexed.AI.Runtime.Pipeline;
using Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support;
using Multiplexed.Rbac.Core.Authorization.Engine;
using Multiplexed.Rbac.Core.Authorization.Scope;
using Multiplexed.Rbac.Core.Authorization.Trn;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime;
using RbacContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    /// <summary>Local routing/transport/payload doubles; the RBAC engine and step helper are real.</summary>
    internal static class McpStepTestSupport
    {
        public const string Grant = "trn:tests:default:reports:publish:invoke";

        public static AiPipelineStepDefinition Step(
            string name = "publish", string connection = "reports", string tool = "publish",
            IReadOnlyDictionary<string, object?>? input = null,
            IReadOnlyDictionary<string, object?>? config = null) => new()
        {
            Name = name, StepKey = "same-key",
            Invocation = new AiInvocationDefinition { Kind = AiInvocationKind.Mcp, ConnectionRef = connection, Tool = tool },
            Input = input ?? new Dictionary<string, object?>(), Config = config ?? new Dictionary<string, object?>()
        };

        public static AiPipelineDefinition Pipeline(AiPipelineStepDefinition? step = null, string? language = "python",
            AiExecutionMode mode = AiExecutionMode.Dag) => new()
        {
            Name = "test", Version = "v1", ExecutionMode = mode, ExecutionLanguage = language,
            Steps = new[] { step ?? Step() }
        };

        public static AiMcpToolBinding Target(AiMcpToolResolutionRequest request) => new(
            request.TenantId, request.TenantGroupId, request.ConnectionRef, request.Tool,
            "connection/v1", "reports", "publish", "invoke");

        public static JsonElement Json(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        public static JsonElement Response(AiMcpToolRequest request, bool error = false, bool structured = true)
        {
            var response = new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1, ["requestId"] = request.RequestId, ["isError"] = error,
                ["content"] = new object[] { new { type = "text", text = error ? "tool refused" : "published" } }
            };
            if (structured) response["structuredContent"] = new { id = 42 };
            return JsonSerializer.SerializeToElement(response);
        }

        public static async Task<Fixture> CreateAsync(
            AiPipelineDefinition? definition = null, Resolver? resolver = null, Transport? transport = null,
            string tenant = "tenant-1", string id = "execution-1", string grant = Grant,
            bool registerAuthorization = true, AiMcpStepInvocationOptions? options = null,
            CancellationToken runtimeCancellation = default)
        {
            resolver ??= new Resolver();
            transport ??= new Transport();
            var factory = new AiMcpStepAdapterFactory(resolver, transport, options);
            var registry = new NativeRegistry();
            var plan = await new AiPipelineResolver(registry, new[] { factory }).ResolveAsync(definition ?? Pipeline());
            var accessor = new ExecutionContextAccessor();
            var payloads = new PayloadResolver();
            var services = new ServiceCollection();
            services.AddSingleton<IExecutionContextAccessor>(accessor);
            services.AddSingleton<IAiExecutionPayloadResolver>(payloads);
            services.AddSingleton(new TrnBuilder(Options.Create(new TrnBuilderOptions { Project = "tests" })));
            services.AddScoped<AuthorizationScope>();
            if (registerAuthorization) services.AddScoped<IAuthorizationEngine, TrnAuthorizationEngine>();
            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            var context = CreateContext(plan, provider, tenant, id, grant, runtimeCancellation);
            return new Fixture(factory, resolver, transport, plan, registry, provider, accessor, payloads, context);
        }

        public static AiStepExecutionContext CreateContext(ResolvedAiPipeline plan, IServiceProvider services,
            string tenant = "tenant-1", string id = "execution-1", string grant = Grant,
            CancellationToken cancellationToken = default)
        {
            var snapshot = new ExecutionContextSnapshot
            {
                ContextKey = id, Project = "tests", UserId = "user-1", TenantId = tenant,
                TenantGroupId = "group-1", CurrentNamespace = "default",
                Namespaces = new() { new NamespaceEntry { Name = "default", Trns = new HashSet<string>(StringComparer.Ordinal) } }
            };
            if (!string.IsNullOrEmpty(grant)) snapshot.Namespaces[0].Trns.Add(grant);
            var record = new AiExecutionRecord
            {
                ExecutionId = id, PipelineName = plan.Name, ExecutionMode = AiExecutionMode.Dag,
                ExecutionContextSnapshot = snapshot
            };
            var state = new AiExecutionState { ExecutionId = id, PipelineName = plan.Name };
            var baseline = ChildDagCompositionTestData.CreateExecutionContext(record, state);
            var execution = new AiExecutionContext(record, state, services, baseline.StateReader, baseline.StateWriter, cancellationToken);
            return new AiStepExecutionContext(execution, Assert.Single(plan.Steps));
        }

        internal sealed class Fixture : IDisposable
        {
            public Fixture(AiMcpStepAdapterFactory factory, Resolver resolver, Transport transport, ResolvedAiPipeline plan,
                NativeRegistry registry, ServiceProvider provider, ExecutionContextAccessor accessor,
                PayloadResolver payloads, AiStepExecutionContext context)
            {
                Factory = factory; Resolver = resolver; Transport = transport; Plan = plan; Registry = registry;
                Provider = provider; Accessor = accessor; Payloads = payloads; Context = context;
                Live = ExecutionContextSnapshotMapper.ToExecutionContext(context.Record.ExecutionContextSnapshot!);
            }
            public AiMcpStepAdapterFactory Factory { get; }
            public Resolver Resolver { get; }
            public Transport Transport { get; }
            public ResolvedAiPipeline Plan { get; }
            public NativeRegistry Registry { get; }
            public ServiceProvider Provider { get; }
            public ExecutionContextAccessor Accessor { get; }
            public PayloadResolver Payloads { get; }
            public AiStepExecutionContext Context { get; }
            public RbacContext Live { get; }
            public AiMcpStepAdapter Adapter => Assert.IsType<AiMcpStepAdapter>(Context.Step.Step);

            public Task<AiStepResult> InvokeAsync(CancellationToken cancellationToken = default) =>
                WithLiveAsync(Live, () => Adapter.ExecuteAsync(Context, cancellationToken));

            public async Task<T> WithLiveAsync<T>(RbacContext? live, Func<Task<T>> action)
            {
                var previous = Accessor.Current;
                if (live is null) Accessor.Clear(); else Accessor.Set(live);
                try { return await action(); }
                finally { if (previous is null) Accessor.Clear(); else Accessor.Set(previous); }
            }
            public void Dispose() => Provider.Dispose();
        }

        internal sealed class Resolver : IAiMcpToolResolver
        {
            public ConcurrentQueue<AiMcpToolResolutionRequest> Calls { get; } = new();
            public Func<AiMcpToolResolutionRequest, CancellationToken, Task<AiMcpToolBinding?>>? Handler { get; set; }
            public Task<AiMcpToolBinding?> ResolveAsync(AiMcpToolResolutionRequest request, CancellationToken cancellationToken = default)
            {
                Calls.Enqueue(request);
                return Handler is null ? Task.FromResult<AiMcpToolBinding?>(Target(request)) : Handler(request, cancellationToken);
            }
        }

        internal sealed class Transport : IAiMcpToolTransport
        {
            public ConcurrentQueue<AiMcpToolRequest> Calls { get; } = new();
            public Func<AiMcpToolRequest, CancellationToken, Task<JsonElement>>? Handler { get; set; }
            public CancellationToken LastToken { get; private set; }
            public Task<JsonElement> InvokeAsync(AiMcpToolRequest request, CancellationToken cancellationToken = default)
            {
                LastToken = cancellationToken;
                Calls.Enqueue(request);
                return Handler is null ? Task.FromResult(Response(request)) : Handler(request, cancellationToken);
            }
        }

        internal sealed class PayloadResolver : IAiExecutionPayloadResolver
        {
            public int Calls;
            public Dictionary<string, object?> Artifacts { get; } = new(StringComparer.Ordinal);
            public Task<object?> ResolveAsync(AiStoredPayload payload, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref Calls);
                if (payload.IsInline) return Task.FromResult(payload.InlineValue);
                if (payload.ArtifactId is not null && Artifacts.TryGetValue(payload.ArtifactId, out var value)) return Task.FromResult(value);
                throw new InvalidOperationException("Missing test artifact.");
            }
        }

        internal sealed class NativeRegistry : IAiStepRegistry
        {
            public int Calls;
            public IAiStep Resolve(string stepKey) { Interlocked.Increment(ref Calls); return new NativeStep(); }
        }
        internal sealed class NativeStep : IAiStep
        {
            public string Name => "native";
            public Task<AiStepResult> ExecuteAsync(AiStepExecutionContext context, CancellationToken cancellationToken = default) =>
                Task.FromResult(AiStepResult.Ok("native"));
        }
    }
}
