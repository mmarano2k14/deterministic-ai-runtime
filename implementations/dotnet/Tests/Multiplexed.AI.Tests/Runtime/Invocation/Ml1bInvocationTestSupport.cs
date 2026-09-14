using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.AI.Concurrency;
using Multiplexed.AI.Runtime.AI.Policies;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Engine.Helpers;
using Multiplexed.AI.Runtime.Execution.Engine.Steps;
using Multiplexed.AI.Runtime.Invocation;
using Multiplexed.AI.Runtime.Observability.Metrics.Policy;
using Multiplexed.AI.Runtime.Observability.Tracing;
using Multiplexed.AI.Runtime.Pipeline;
using Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support;
using Xunit;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    /// <summary>Local doubles only: no language process, MCP client, Redis or hosted worker.</summary>
    internal static class Ml1bInvocationTestSupport
    {
        public static AiPipelineStepDefinition Custom(
            string name = "work", string? language = null, int order = 0,
            IReadOnlyDictionary<string, object?>? config = null, string? implementationRef = null) => new()
        {
            Name = name, StepKey = "same-key", Order = order, ExecutionLanguage = language,
            Invocation = new AiInvocationDefinition
            {
                Kind = AiInvocationKind.Custom, ImplementationRef = implementationRef ?? $"publication/{name}/v1"
            },
            Config = config ?? new Dictionary<string, object?>()
        };

        public static AiPipelineStepDefinition Native(string name = "work", IReadOnlyDictionary<string, object?>? config = null) => new()
        {
            Name = name, StepKey = "same-key", Config = config ?? new Dictionary<string, object?>()
        };

        public static AiPipelineStepDefinition Mcp(string name = "tool", IReadOnlyDictionary<string, object?>? config = null) => new()
        {
            Name = name, StepKey = "same-key", Config = config ?? new Dictionary<string, object?>(),
            Invocation = new AiInvocationDefinition { Kind = AiInvocationKind.Mcp, ConnectionRef = "reports", Tool = "publish" }
        };

        public static AiConfiguredPolicyDefinition CustomPolicy(string name = "guard", string? language = null) => new()
        {
            Name = name, ExecutionLanguage = language,
            Invocation = new AiInvocationDefinition { Kind = AiInvocationKind.Custom, ImplementationRef = $"publication/{name}/v1" }
        };

        public static Dictionary<string, object?> Config(params AiConfiguredPolicyDefinition[] policies) => new()
        {
            ["concurrency"] = new Dictionary<string, object?> { ["enabled"] = true, ["policies"] = policies.ToList() }
        };

        public static AiPipelineDefinition CreatePipeline(
            IReadOnlyList<AiPipelineStepDefinition> steps, string? language = "python",
            IReadOnlyDictionary<string, object?>? config = null) => new()
        {
            Name = "test", Version = "v1", ExecutionMode = AiExecutionMode.Dag,
            ExecutionLanguage = language, Steps = steps, Config = config ?? new Dictionary<string, object?>()
        };

        public static IAiStepInvocationAdapterFactory[] Factories() => new IAiStepInvocationAdapterFactory[]
        {
            new ProbeFactory(AiInvocationKind.Custom, "python"),
            new ProbeFactory(AiInvocationKind.Custom, "typescript"),
            new ProbeFactory(AiInvocationKind.Custom, "dotnet"),
            new ProbeFactory(AiInvocationKind.Mcp, null)
        };

        public static AiExecutionContext CreateExecution(string id = "execution-1", string tenant = "tenant-1")
        {
            var record = new AiExecutionRecord
            {
                ExecutionId = id, PipelineName = "test", ExecutionMode = AiExecutionMode.Dag,
                ExecutionContextSnapshot = new ExecutionContextSnapshot
                {
                    ContextKey = id, Project = "tests", UserId = "user-1", TenantId = tenant,
                    TenantGroupId = "group-1", CurrentNamespace = "default", Namespaces = new()
                }
            };
            var state = new AiExecutionState { ExecutionId = id, PipelineName = "test" };
            return ChildDagCompositionTestData.CreateExecutionContext(record, state);
        }

        internal sealed class ProbeRegistry : IAiStepRegistry
        {
            public int Calls;
            public ProbeStep Implementation { get; } = new(null);
            public IAiStep Resolve(string stepKey)
            {
                Interlocked.Increment(ref Calls);
                return Implementation;
            }
        }

        internal sealed class ProbeFactory : IAiStepInvocationAdapterFactory
        {
            public ProbeFactory(AiInvocationKind kind, string? language) { Kind = kind; ExecutionLanguage = language; }
            public AiInvocationKind Kind { get; }
            public string? ExecutionLanguage { get; }
            public ConcurrentQueue<ProbeStep> Created { get; } = new();
            public Func<AiStepInvocationAdapterContext, IAiStep>? OnCreate { get; set; }
            public IAiStep Create(AiStepInvocationAdapterContext context)
            {
                if (OnCreate is not null) return OnCreate(context);
                var step = new ProbeStep(context);
                Created.Enqueue(step);
                return step;
            }
        }

        internal sealed class ProbeStep : IAiStep
        {
            public ProbeStep(AiStepInvocationAdapterContext? metadata) { Metadata = metadata; }
            public AiStepInvocationAdapterContext? Metadata { get; }
            public string Name => Metadata?.StepName ?? "native";
            public ConcurrentQueue<AiStepExecutionContext> Calls { get; } = new();
            public AiStepResult Result { get; set; } = AiStepResult.Ok("adapter");
            public Task<AiStepResult> ExecuteAsync(AiStepExecutionContext context, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Calls.Enqueue(context);
                return Task.FromResult(Result);
            }
        }

        internal sealed class ProbePolicy : IAiPolicy
        {
            public ProbePolicy(string key = "guard", bool block = false) { Key = key; Block = block; }
            public string Key { get; }
            public AiPolicyKind Kind => AiPolicyKind.Concurrency;
            public bool Block { get; }
            public ConcurrentQueue<AiConcurrencyPolicyContext> Calls { get; } = new();
            public Task<AiPolicyResult> ExecuteAsync(object context, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Calls.Enqueue(Assert.IsType<AiConcurrencyPolicyContext>(context));
                return Task.FromResult(Block ? AiPolicyResult.Block("native denied") : AiPolicyResult.Success("native allowed"));
            }
        }

        internal sealed class ProbePolicyFactory : IAiPolicyEngineFactory
        {
            private readonly IAiPolicyRegistry _registry;
            private readonly IAiRuntimeObservability _obs;
            public ProbePolicyFactory(IAiRuntimeObservability obs, params IAiPolicy[] policies)
            {
                _obs = obs; _registry = new DefaultAiPolicyRegistry(policies);
            }
            public ConcurrentQueue<AiStepExecutionContext> Contexts { get; } = new();
            public IAiPolicyEngine Create(AiPolicyKind kind, AiStepExecutionContext stepContext)
            {
                Assert.Equal(AiPolicyKind.Concurrency, kind);
                Contexts.Enqueue(stepContext);
                return new DefaultAiConcurrencyEngine(_registry, stepContext, _obs);
            }
            public TPolicyEngine Create<TPolicyEngine>(AiPolicyKind kind, AiStepExecutionContext stepContext)
                where TPolicyEngine : class, IAiPolicyEngine
            {
                return (TPolicyEngine)Create(kind, stepContext);
            }
        }

        internal sealed class ProbeGate : IAiConcurrencyGate
        {
            public int AcquireCalls;
            public int ReleaseCalls;
            public AiConcurrencyDefinition? Definition;
            public Task<AiConcurrencyDecision> TryAcquireAsync(AiConcurrencyContext context, AiConcurrencyDefinition definition, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref AcquireCalls); Definition = definition;
                return Task.FromResult(AiConcurrencyDecision.Allow());
            }
            public Task ReleaseAsync(AiConcurrencyContext context, AiConcurrencyDefinition definition, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref ReleaseCalls); return Task.CompletedTask;
            }
        }

        internal sealed class ProbeCompactor : IAiStepResultPayloadCompactor
        {
            public int Calls;
            public Task CompactAsync(AiStepResult result, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref Calls); return Task.CompletedTask;
            }
        }

        internal sealed class TestObservability : IAiRuntimeObservability
        {
            public IAiRuntimeMetrics Metrics { get; } = new TestMetrics();
            public IAiRuntimeTracer Tracer { get; } = new NoOpAiRuntimeTracer();
            public IAiDecisionLedgerRecorder Ledger => null!;
            public IAiRuntimeCorrelationAccessor Correlation => null!;
        }

        private sealed class TestMetrics : IAiRuntimeMetrics
        {
            public Multiplexed.Abstractions.AI.Observability.Metrics.Execution.IAiExecutionMetrics Execution => null!;
            public Multiplexed.Abstractions.AI.Observability.Metrics.Retention.IAiRetentionMetrics Retention => null!;
            public Multiplexed.Abstractions.AI.Observability.Metrics.Storage.IAiStorageMetrics Storage => null!;
            public Multiplexed.Abstractions.AI.Observability.Metrics.HotState.IAiHotStateMetrics HotState => null!;
            public Multiplexed.Abstractions.AI.Observability.Metrics.Resolvers.IAiResolverMetrics Resolver => null!;
            public Multiplexed.Abstractions.AI.Observability.Metrics.Policy.IAiPolicyMetrics Policy { get; } =
                new AiPolicyMetrics(NoOpAiRuntimeMetricWriter.Instance);
            public Multiplexed.Abstractions.AI.Observability.Metrics.Workers.IAiRuntimeInstanceWorkerMetrics Worker => null!;
        }
    }

    /// <summary>Strict property-only service proxy; any unexpected dependency fails the test.</summary>
    public class Ml1bPropertyProxy : DispatchProxy
    {
        private IReadOnlyDictionary<string, object?> _values = new Dictionary<string, object?>();
        public static T For<T>(IReadOnlyDictionary<string, object?> values) where T : class
        {
            var result = DispatchProxy.Create<T, Ml1bPropertyProxy>();
            ((Ml1bPropertyProxy)(object)result)._values = values;
            return result;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is not null && _values.TryGetValue(targetMethod.Name, out var value)) return value;
            throw new NotSupportedException($"Unexpected test dependency: {targetMethod?.Name}");
        }
    }
}
