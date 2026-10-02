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
using static Multiplexed.AI.Tests.Runtime.Invocation.Ml1bInvocationTestSupport;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    /// <summary>Tests the unchanged claimed-step executor using local adapter doubles only.</summary>
    public sealed class AiInvocationDagExecutionTests
    {
        [Theory]
        [InlineData(AiInvocationKind.Custom, "python", false)]
        [InlineData(AiInvocationKind.Custom, "typescript", false)]
        [InlineData(AiInvocationKind.Custom, "dotnet", false)]
        [InlineData(AiInvocationKind.Mcp, null, false)]
        [InlineData(AiInvocationKind.Custom, "python", true)]
        [InlineData(AiInvocationKind.Mcp, null, true)]
        public async Task Claimed_Dag_Executor_Calls_The_Selected_Adapter_And_Keeps_Result_Semantics(
            AiInvocationKind kind, string? language, bool park)
        {
            var source = kind == AiInvocationKind.Mcp ? Mcp() : Custom();
            var native = new ProbeRegistry();
            var factory = new ProbeFactory(kind, language);
            var plan = await new AiPipelineResolver(native, new[] { factory }).ResolveAsync(CreatePipeline(new[] { source }, language ?? "python"));
            var adapter = Assert.Single(factory.Created);
            adapter.Result = new AiStepResult
            {
                Success = true, Outcome = park ? AiStepExecutionOutcome.Park : AiStepExecutionOutcome.Complete,
                Value = "value", Output = "output", Data = new Dictionary<string, object?> { ["result"] = 42 }
            };
            var execution = CreateExecution();
            execution.EnsureStepInitialized(plan.Steps[0]);
            var compactor = new ProbeCompactor();
            var identity = Ml1bPropertyProxy.For<IAiRuntimeInstanceIdentityDescriptor>(new Dictionary<string, object?> { ["get_RuntimeInstanceId"] = "runtime-1" });
            var services = Ml1bPropertyProxy.For<IAiDagExecutionEngineServices>(new Dictionary<string, object?>
            {
                ["get_RuntimeInstanceIdentity"] = identity,
                ["get_ObservabilityService"] = new TestObservability(),
                ["get_PayloadCompactor"] = compactor
            });
            var executor = new AiDagClaimedStepExecutor(services);
            var result = await executor.ExecuteAsync(execution.Record, execution.State, plan,
                new AiClaimedStep { ExecutionId = execution.ExecutionId, StepName = source.Name, ClaimToken = "claim-1" },
                (_, _, _) => execution);
            Assert.Same(adapter.Result, result);
            Assert.Equal(park ? 0 : 1, compactor.Calls);
            var context = Assert.Single(adapter.Calls);
            Assert.Same(plan.Steps[0], context.Step);
            Assert.Same(adapter.Metadata!.Binding, context.InvocationBinding);
            Assert.Equal(source.Name, context.StepName);
            Assert.Equal("same-key", context.StepKey);
            Assert.Equal(AiStepExecutionStatus.Running, context.StepState.Status);
            Assert.Equal("runtime-1", context.StepState.ClaimedBy);
            Assert.Equal("claim-1", context.StepState.ClaimToken);
            Assert.Equal("value", result.Value);
            Assert.Equal("output", result.Output);
            Assert.Equal(42, result.Data["result"]);
            Assert.Equal(0, native.Calls);
            Assert.Single(factory.Created);
        }
    }
}
