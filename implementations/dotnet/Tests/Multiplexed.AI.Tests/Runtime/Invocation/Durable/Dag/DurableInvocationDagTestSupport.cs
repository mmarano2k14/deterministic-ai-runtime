using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
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
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.Abstractions.Runtime;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Retry;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.Runtime.AI.Policies;
using Multiplexed.AI.Runtime.AI.Retry;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Engine.Helpers;
using Multiplexed.AI.Runtime.Execution.Engine.Local;
using Multiplexed.AI.Runtime.Execution.Payloads;
using Multiplexed.AI.Runtime.Execution.Payloads.Immutable;
using Multiplexed.AI.Runtime.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Durable.Dag;
using Multiplexed.AI.Runtime.Observability;
using Multiplexed.AI.Runtime.Observability.Tracing;
using Multiplexed.AI.Runtime.Pipeline;
using Multiplexed.AI.Stores.Memory;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime;
using RbacContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;
using Multiplexed.Abstractions.AI.Steps;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag
{
    /// <summary>Real journal, adapter, resolver, local runner and external-wait engine; test-only storage and dispatch.</summary>
    internal static class DurableInvocationDagTestSupport
    {
        internal static AiDurableInvocationScope Scope => DurableInvocationTestSupport.Scope;
        internal static AiDurableInvocationIdentity Identity => DurableInvocationTestSupport.Identity;
        internal static AiPipelineDefinition Definition(string language = "python") => new()
        {
            Name = "analysis", Version = "1", ExecutionMode = AiExecutionMode.Dag, ExecutionLanguage = language,
            Steps = new[] { new AiPipelineStepDefinition
            {
                Name = Identity.StepName, StepKey = "analysis-custom",
                Invocation = new AiInvocationDefinition { Kind = AiInvocationKind.Custom, ImplementationRef = "analyze-1" },
                Input = new Dictionary<string, object?> { ["amount"] = 1 },
                Config = new Dictionary<string, object?> { ["retry"] = new AiRetryPolicyDefinition
                    { MaxRetries = 0, Policies = new List<AiConfiguredPolicyDefinition>() } }
            } }
        };

        internal static async Task<Fixture> CreateAsync(string language = "python")
        {
            var fixture = new Fixture(Definition(language));
            await fixture.InitializeAsync();
            return fixture;
        }

        // The production immutable reader verifies this descriptor independently.
        internal static AiStoredPayload Snapshot(object value)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) Write(document.RootElement, writer);
            var json = Encoding.UTF8.GetString(stream.ToArray());
            return AiStoredPayload.Inline(json, contentHash: Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant());
        }
        private static void Write(JsonElement element, Utf8JsonWriter writer)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); Write(property.Value, writer); }
                writer.WriteEndObject();
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Write(item, writer); writer.WriteEndArray();
            }
            else element.WriteTo(writer);
        }

        internal sealed class Fixture : IDisposable
        {
            internal readonly DurableInvocationTestSupport.MemoryStore JournalStore;
            internal readonly DurableInvocationTestSupport.Clock Clock;
            internal AiDurableInvocationJournal Journal;
            internal readonly MemoryAiExecutionStore Store = new();
            internal readonly ExecutionContextAccessor Accessor = new();
            internal readonly InMemoryAiSharedQueue Queue = new();
            internal readonly StaticAiControlPlaneIdResolver ControlPlane = new(Scope.ControlPlaneId);
            internal readonly Controller Controller = new();
            internal readonly TargetResolver Targets = new();
            internal readonly FullSteps Steps = new();
            internal readonly McpStepTestSupport.PayloadResolver Inputs = new();
            internal readonly Payloads Payloads = new();
            internal readonly AiPipelineDefinition Definition;
            internal readonly AiPipelineResolver Resolver;
            internal ServiceProvider Provider = null!;
            internal AiDurableInvocationDagBinding Binding = null!;
            internal AiDurableInvocationDagContinuationScheduler Scheduler = null!;
            internal AiDurableInvocationDagContinuationCoordinator Coordinator = null!;
            internal AiDurableInvocationDagReconciler Reconciler = null!;
            internal AiStepExecutionContext Context = null!;
            internal ResolvedAiPipeline Plan = null!;
            internal IAiDagExecutionEngineServices EngineServices = null!;
            internal AiDagExecutionEngine Engine = null!;
            internal AiDagLocalExecutionRunner Runner = null!;
            internal bool FailPersistAfterExecution;

            internal Fixture(AiPipelineDefinition definition)
            {
                Definition = definition;
                (Journal, JournalStore, Clock) = DurableInvocationTestSupport.Create();
                Resolver = new AiPipelineResolver(new Registry(), new[]
                    { new AiDurableInvocationStepAdapterFactory(definition.ExecutionLanguage!) });
            }
            internal async Task InitializeAsync()
            {
                Plan = await Resolver.ResolveAsync(Definition);
                var services = new ServiceCollection();
                services.AddSingleton<IExecutionContextAccessor>(Accessor);
                services.AddSingleton<IAiControlPlaneIdResolver>(ControlPlane);
                services.AddSingleton<IAiExecutionPayloadResolver>(Inputs);
                services.AddSingleton<IAiDurableInvocationTargetResolver>(Targets);
                services.AddSingleton(Journal);
                Binding = new AiDurableInvocationDagBinding(new AiImmutableJsonPayloadReader(Payloads));
                services.AddSingleton(Binding);
                Provider = services.BuildServiceProvider();
                var snapshot = new ExecutionContextSnapshot
                {
                    ContextKey = "context-a", Project = "tests", UserId = "user-a", TenantId = Scope.TenantId,
                    TenantGroupId = Scope.TenantGroupId, CurrentNamespace = "default",
                    Namespaces = new() { new NamespaceEntry { Name = "default", Trns = new HashSet<string>(StringComparer.Ordinal) } }
                };
                var record = new AiExecutionRecord
                {
                    ExecutionId = Identity.ExecutionId, PipelineName = Definition.Name, ExecutionMode = AiExecutionMode.Dag,
                    ContextKey = snapshot.ContextKey, ExecutionContextSnapshot = snapshot, Status = AiExecutionStatus.Running,
                    PipelineDefinitionSnapshot = Snapshot(Definition), Steps = new() { Identity.StepName }
                };
                record.RenewExecutionStepKey();
                var state = new AiExecutionState { ExecutionId = record.ExecutionId, PipelineName = record.PipelineName! };
                Context = new AiStepExecutionContext(Build(record, state, default), Assert.Single(Plan.Steps));
                Context.StepState.MarkRunning("runtime-a", "claim-a");
                await Store.CreateAsync(record, state);
                RebuildCoordinator();
                BuildEngine();
            }
            internal void RebuildCoordinator()
            {
                Scheduler = new(Controller, Queue, Accessor, ControlPlane);
                Coordinator = new(Journal, Store, Steps, Binding, ControlPlane, Accessor, Scheduler);
                Reconciler = new(JournalStore, Journal, Coordinator, ControlPlane, NullLogger<AiDurableInvocationDagReconciler>.Instance);
            }
            internal AiExecutionContext Build(AiExecutionRecord record, AiExecutionState state, CancellationToken ct)
            {
                var baseline = ChildDagCompositionTestData.CreateExecutionContext(record, state);
                return new AiExecutionContext(record, state, Provider, baseline.StateReader, baseline.StateWriter, ct);
            }
            internal async Task<T> WithIdentityAsync<T>(Func<Task<T>> action, RbacContext? identity = null)
            {
                var previous = Accessor.Current;
                Accessor.Set(identity ?? ExecutionContextSnapshotMapper.ToExecutionContext(Context.Record.ExecutionContextSnapshot!));
                try { return await action(); }
                finally { if (previous is null) Accessor.Clear(); else Accessor.Set(previous); }
            }
            internal Task<AiStepResult> InvokeAsync(CancellationToken ct = default) =>
                WithIdentityAsync(() => Assert.Single(Plan.Steps).Step.ExecuteAsync(Context, ct));
            internal async Task<AiDurableInvocationRecord> TerminalAsync(bool success = true, string json = "{\"value\":42}")
            {
                if (await Journal.GetAsync(Scope, Identity) is null) await InvokeAsync();
                var lease = (await Journal.TryAcquireLeaseAsync(Scope, Identity, "language-worker-a", TimeSpan.FromSeconds(30)))!;
                Assert.Equal(AiDurableInvocationCompletionStatus.Accepted,
                    await Journal.CompleteAsync(Scope, Identity, lease.Lease!, new AiDurableInvocationResult(success, json)));
                return (await Journal.GetAsync(Scope, Identity))!;
            }
            internal async Task SetStateAsync(AiStepExecutionStatus stepStatus, AiExecutionStatus parentStatus = AiExecutionStatus.Running,
                AiStepResult? result = null)
            {
                var record = (await Store.GetRecordAsync(Identity.ExecutionId))!;
                var state = (await Store.GetStateAsync(Identity.ExecutionId))!;
                record.Status = parentStatus;
                state.Steps[Identity.StepName].Status = stepStatus;
                state.Steps[Identity.StepName].Result = result;
                await Store.CreateAsync(record, state);
            }
            internal Task<AiDurableInvocationRecord?> ReconcileAsync() => Coordinator.ReconcileAsync(Scope, Identity);
            internal async Task<AiExecutionRecord> RunNextAsync()
            {
                // Establish caller identity before the awaited context-loader delegate.
                return await WithIdentityAsync(() => Runner.ExecuteNextAsync(Identity.ExecutionId,
                    async (id, ct) => ((await Store.GetRecordAsync(id, ct))!, (await Store.GetStateAsync(id, ct))!),
                    _ => Task.CompletedTask, Build,
                    async (record, expected, state, ct) =>
                    {
                        if (FailPersistAfterExecution) { FailPersistAfterExecution = false; throw new IOException("Injected crash before DAG persistence."); }
                        Assert.True(await Store.TryUpdateAsync(record.ExecutionId, expected, record, state, ct));
                    }, _ => { }, _ => { }));
            }
            private void BuildEngine()
            {
                var logger = new NoopLogger();
                var observability = new AiRuntimeObservability(
                    DagTestProxy.Noop<IAiRuntimeMetrics>(), new NoOpAiRuntimeTracer(), logger,
                    new NoOpAiDecisionLedgerRecorder(), DagTestProxy.Noop<IAiRuntimeCorrelationAccessor>());
                var options = new AiEngineOptions();
                options.Snapshots.Enabled = false;
                options.Cleanup.AutoCleanupOnCompleted = false;
                options.Cleanup.AutoCleanupOnFailed = false;
                var policyFactory = DagTestProxy.Create<IAiPolicyEngineFactory>((method, args) =>
                    new DefaultAiRetryEngine(new DefaultAiPolicyRegistry(Array.Empty<IAiPolicy>()),
                        (AiStepExecutionContext)args![1]!, observability));
                var pipeline = DagTestProxy.Create<IAiSequentialPipelineExecutor>((method, args) =>
                {
                    Assert.Equal("PrepareAsync", method.Name);
                    return Resolver.ResolveAsync(Assert.IsType<AiPipelineDefinition>(args![0]), (CancellationToken)args[1]!);
                });
                var values = new Dictionary<string, object?>
                {
                    ["Store"] = Store, ["DagStore"] = null, ["Accessor"] = Accessor,
                    ["ContextStore"] = DagTestProxy.Noop<IContextStore>(), ["ContextFactory"] = new ExecutionContextFactory(),
                    ["Services"] = Provider, ["PipelineExecutor"] = pipeline, ["Logger"] = logger,
                    ["StateReader"] = Context.Execution.StateReader, ["StateWriter"] = Context.Execution.StateWriter,
                    ["PayloadStoreResolver"] = Payloads, ["StepResolver"] = Steps, ["AiOptions"] = Options.Create(options),
                    ["SnapshotService"] = null, ["PolicyEngineFactory"] = policyFactory, ["ObservabilityService"] = observability,
                    ["PayloadCompactor"] = DagTestProxy.Noop<IAiStepResultPayloadCompactor>(),
                    ["RuntimeInstanceIdentity"] = DagTestProxy.Create<IAiRuntimeInstanceIdentityDescriptor>((_, _) => "runtime-a")
                };
                EngineServices = DagTestProxy.Create<IAiDagExecutionEngineServices>((method, _) =>
                    values.TryGetValue(method.Name[4..], out var value) ? value : throw new NotSupportedException(method.Name));
                var publisherType = typeof(AiDagLocalExecutionRunner).GetConstructors().Single().GetParameters()[2].ParameterType;
                var publisher = DagTestProxy.Noop(publisherType);
                Runner = (AiDagLocalExecutionRunner)Activator.CreateInstance(typeof(AiDagLocalExecutionRunner),
                    EngineServices, new AiDagExecutionLifecycleHelper(EngineServices), publisher, ControlPlane)!;
                Engine = new AiDagExecutionEngine(EngineServices, DagTestProxy.Noop<IAiDagExecutionEngineRuntimeServices>());
            }
            public void Dispose() => Provider.Dispose();
        }

        internal sealed class TargetResolver : IAiDurableInvocationTargetResolver
        {
            internal int Calls;
            internal Func<AiDurableInvocationTargetRequest, AiDurableInvocationTarget?>? Handler;
            internal static AiDurableInvocationTarget Target(AiDurableInvocationTargetRequest request) => new(
                request.PipelineName, request.PipelineVersion, request.DefinitionSha256, "publication-1", new string('b', 64),
                request.ImplementationRef, new string('c', 64), request.ExecutionLanguage, "environment-locked", new string('d', 64));
            public Task<AiDurableInvocationTarget?> ResolveAsync(AiDurableInvocationTargetRequest request, CancellationToken cancellationToken = default)
            { Calls++; cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Handler is null ? Target(request) : Handler(request)); }
        }
        internal sealed class Payloads : IAiPayloadStoreResolver
        {
            internal readonly InMemoryAiPayloadStore Store = new();
            public IAiPayloadStore Resolve() => Store;
        }
        private sealed class Registry : IAiStepRegistry
        { public IAiStep Resolve(string stepKey) => throw new InvalidOperationException("Custom capability must never fall back to a native step."); }
        internal sealed class FullSteps : IAiExecutionStepResolver
        {
            internal AiStepState? Archived;
            internal int FullReads;
            public Task WarmAsync(string executionId, AiExecutionState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task WarmStepsAsync(string executionId, AiExecutionState state, IReadOnlyCollection<string> stepNames, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task<AiStepState?> GetStepAsync(string executionId, string stepName, AiExecutionState state, CancellationToken cancellationToken = default)
            { FullReads++; return Task.FromResult(state.Steps.TryGetValue(stepName, out var value) ? value : Archived); }
            public Task<AiStepState?> GetStepStatusAsync(string executionId, string stepName, AiExecutionState state, CancellationToken cancellationToken = default) =>
                Task.FromResult(state.Steps.TryGetValue(stepName, out var value) ? value : Archived);
        }
        internal sealed class Controller : IAiSharedRuntimeController
        {
            internal readonly List<AiSharedRuntimeControllerRequest> Requests = new();
            internal Func<AiSharedRuntimeControllerRequest, Task<AiSharedRuntimeControllerResult>>? Handler;
            internal static AiSharedRuntimeControllerResult Accepted(AiSharedRuntimeControllerRequest request) => new()
            {
                Operation = request.Operation, Success = true, SharedRunId = request.RequestedSharedRunId,
                Run = new AiSharedRunRecord
                {
                    SharedRunId = request.RequestedSharedRunId!, Status = AiSharedRunStatus.QueuedGlobally,
                    RunRequest = request.RunRequest!, ExecutionContextSnapshot = request.RunRequest!.ExecutionContextSnapshot!
                }
            };
            public Task<AiSharedRuntimeControllerResult> SubmitRunAsync(AiSharedRuntimeControllerRequest request, CancellationToken cancellationToken = default)
            { cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request); return Handler is null ? Task.FromResult(Accepted(request)) : Handler(request); }
            public Task<AiSharedRuntimeControllerResult> ExecuteAsync(AiSharedRuntimeControllerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<AiSharedRuntimeControllerResult> GetRunAsync(AiSharedRuntimeControllerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<AiSharedRuntimeControllerResult> ListRunsAsync(AiSharedRuntimeControllerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<AiSharedRuntimeControllerResult> CancelRunAsync(AiSharedRuntimeControllerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }

    /// <summary>Dependency-only proxy. It never substitutes DAG transitions or journal decisions.</summary>
    public class DagTestProxy : DispatchProxy
    {
        private Func<MethodInfo, object?[]?, object?> _handler = null!;
        internal static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
        { var result = DispatchProxy.Create<T, DagTestProxy>(); ((DagTestProxy)(object)result)._handler = handler; return result; }
        internal static T Noop<T>() where T : class => (T)Noop(typeof(T));
        internal static object Noop(Type type)
        {
            var result = DispatchProxy.Create(type, typeof(DagTestProxy));
            ((DagTestProxy)result)._handler = (method, _) => method.ReturnType == typeof(void) ? null :
                method.ReturnType == typeof(Task) ? Task.CompletedTask :
                method.ReturnType.IsInterface ? Noop(method.ReturnType) :
                throw new NotSupportedException("Unexpected noop dependency: " + method.Name);
            return result;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _handler(targetMethod!, args);
    }
}
