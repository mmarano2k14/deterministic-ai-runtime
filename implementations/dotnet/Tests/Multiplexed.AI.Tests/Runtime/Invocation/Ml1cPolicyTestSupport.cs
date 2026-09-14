using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.AI.Concurrency;
using Multiplexed.AI.Runtime.AI.Policies;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Engine.Helpers;
using Multiplexed.AI.Runtime.Execution.Engine.Steps;
using Multiplexed.AI.Runtime.Invocation;
using Multiplexed.AI.Runtime.Pipeline;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime;
using Xunit;
using static Multiplexed.AI.Tests.Runtime.Invocation.Ml1bInvocationTestSupport;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    /// <summary>Only local transports. No language process, external effect or Redis claim.</summary>
    internal static class Ml1cPolicyTestSupport
    {
        public static AiConfiguredPolicyDefinition Policy(
            string name = "guard", string? language = null,
            Dictionary<string, object?>? config = null, string? reference = null) => new()
        {
            Name = name, ExecutionLanguage = language,
            Invocation = new AiInvocationDefinition { Kind = AiInvocationKind.Custom, ImplementationRef = reference ?? $"publication/{name}/v1" },
            Config = config ?? new Dictionary<string, object?>()
        };

        public static Dictionary<string, object?> TypedConfig(params AiConfiguredPolicyDefinition[] policies) => new()
        {
            ["concurrency"] = new AiConcurrencyDefinition { Enabled = true, Policies = policies.ToList() }
        };

        public static JsonElement Json(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        public static JsonElement Response(AiConcurrencyPolicyRequest request, string decision = "allow", int? retryMs = null)
        {
            var data = new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1, ["requestId"] = request.RequestId,
                ["policyKind"] = "concurrency", ["decision"] = decision, ["reason"] = decision + "-reason"
            };
            if (retryMs.HasValue) data["retryAfterMs"] = retryMs.Value;
            return JsonSerializer.SerializeToElement(data);
        }

        public static async Task<Fixture> CreateAsync(
            AiPipelineDefinition? definition = null,
            IEnumerable<IAiConcurrencyPolicyTransport>? transports = null,
            bool installFactory = true, IEnumerable<IAiPolicy>? natives = null,
            AiConcurrencyPolicyInvocationOptions? options = null,
            string id = "execution-1", string tenant = "tenant-1",
            AiConcurrencyPolicyAdapterFactory? sharedFactory = null)
        {
            definition ??= CreatePipeline(new[] { Native() }, config: Config(Policy()));
            var source = Assert.Single(definition.Steps);
            var registry = new ProbeRegistry();
            var plan = await new AiPipelineResolver(registry, Factories()).ResolveAsync(definition);
            var baseline = CreateExecution(id, tenant);
            var observer = new RecordingObserver();
            var accessor = new ExecutionContextAccessor();
            var services = new ServiceCollection();
            services.AddSingleton<IAiControlPlaneObserver>(observer);
            services.AddSingleton<IExecutionContextAccessor>(accessor);
            if (installFactory)
            {
                services.AddSingleton(sharedFactory ?? new AiConcurrencyPolicyAdapterFactory(
                    transports ?? new[] { new Transport() }, options));
            }
            var provider = services.BuildServiceProvider();
            var execution = new AiExecutionContext(
                baseline.Record, baseline.State, provider, baseline.StateReader, baseline.StateWriter);
            var effective = new DefaultAiConcurrencyDefinitionResolver().Resolve(definition, source);
            var stepContext = AiStepAdmissionContextFactory.Create(execution, Assert.Single(plan.Steps), effective);
            var obs = new TestObservability();
            var policyFactory = new DefaultAiPolicyEngineFactory(
                new DefaultAiPolicyRegistry(natives ?? Array.Empty<IAiPolicy>()),
                new DefaultAiPolicyEngineRegistry(new[] { typeof(DefaultAiConcurrencyEngine) }), obs);
            var engine = (IAiConcurrencyEngine)policyFactory.Create(AiPolicyKind.Concurrency, stepContext);
            var admission = AiDagExecutionHelpers.CreateConcurrencyAdmission(
                id, "test:v1", source.Name, "runtime-1", execution.State.Steps[source.Name],
                plan.Config, source, new DefaultAiConcurrencyDefinitionResolver());
            return new Fixture(definition, source, plan, execution, stepContext, effective, engine,
                policyFactory, observer, provider, accessor, admission, registry);
        }

        internal sealed record Fixture(
            AiPipelineDefinition Definition, AiPipelineStepDefinition Source,
            ResolvedAiPipeline Plan, AiExecutionContext Execution, AiStepExecutionContext StepContext,
            AiConcurrencyDefinition Effective, IAiConcurrencyEngine Engine,
            DefaultAiPolicyEngineFactory PolicyFactory, RecordingObserver Observer,
            ServiceProvider Provider, IExecutionContextAccessor Accessor,
            AiDagConcurrencyAdmission Admission, ProbeRegistry StepRegistry) : IDisposable
        {
            public ProbeGate Gate { get; } = new();
            public Task<AiConcurrencyDecision> DecideAsync(CancellationToken cancellationToken = default) =>
                Engine.DecideAsync(Admission.Context, cancellationToken);

            // Invoke the real pre-claim policy/gate boundary. The runner's restoration of
            // the trusted AsyncLocal tenant scope is modelled explicitly, not a store read.
            public async Task<AiConcurrencyDecision> AdmitAsync(CancellationToken cancellationToken = default)
            {
                var previous = Accessor.Current;
                var snapshot = Execution.Record.ExecutionContextSnapshot!;
                Accessor.Set(new Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext
                {
                    ContextKey = snapshot.ContextKey, Project = snapshot.Project, UserId = snapshot.UserId,
                    TenantId = snapshot.TenantId, TenantGroupId = snapshot.TenantGroupId,
                    CurrentNamespace = snapshot.CurrentNamespace, Namespaces = new()
                });
                try
                {
                    var services = Ml1bPropertyProxy.For<IAiDagExecutionEngineServices>(new Dictionary<string, object?>
                    {
                        ["get_Services"] = Provider,
                        ["get_StateReader"] = Execution.StateReader,
                        ["get_StateWriter"] = Execution.StateWriter,
                        ["get_ObservabilityService"] = new TestObservability(),
                        ["get_PolicyEngineFactory"] = PolicyFactory,
                        ["get_ConcurrencyGate"] = Gate
                    });
                    var method = typeof(AiDagStepClaimService).GetMethod("TryAcquireConcurrencyLeaseAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.NotNull(method);
                    var task = (Task<AiConcurrencyDecision>)method!.Invoke(new AiDagStepClaimService(services), new object[]
                    {
                        Plan, Admission.Context, Admission.Definition, Execution.State,
                        Execution.State.Steps[Source.Name], Source, Source.Name, cancellationToken
                    })!;
                    return await task;
                }
                finally
                {
                    if (previous is null) Accessor.Clear(); else Accessor.Set(previous);
                }
            }

            public void Dispose() => Provider.Dispose();
        }

        internal sealed class Transport : IAiConcurrencyPolicyTransport
        {
            public Transport(string language = "python") { ExecutionLanguage = language; }
            public string ExecutionLanguage { get; }
            public ConcurrentQueue<AiConcurrencyPolicyRequest> Calls { get; } = new();
            public TaskCompletionSource<AiConcurrencyPolicyRequest> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationToken LastToken { get; private set; }
            public Func<AiConcurrencyPolicyRequest, CancellationToken, Task<JsonElement>> Handler { get; set; } =
                (request, _) => Task.FromResult(Response(request));
            public Task<JsonElement> EvaluateAsync(AiConcurrencyPolicyRequest request, CancellationToken cancellationToken = default)
            {
                Calls.Enqueue(request); LastToken = cancellationToken; Started.TrySetResult(request);
                return Handler(request, cancellationToken);
            }
        }

        internal sealed class RecordingObserver : IAiControlPlaneObserver
        {
            public ConcurrentQueue<AiControlPlaneEvent> Events { get; } = new();
            public Task RecordAsync(AiControlPlaneEvent controlPlaneEvent, CancellationToken cancellationToken = default)
            {
                Events.Enqueue(controlPlaneEvent); return Task.CompletedTask;
            }
        }
    }
}
