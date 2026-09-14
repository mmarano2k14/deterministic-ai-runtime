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
    public sealed class AiInvocationBindingSnapshotTests
    {
        [Fact]
        public async Task Frozen_Definition_Recompiles_The_Same_Bindings_Without_Persisting_Adapters()
        {
            var policy = CustomPolicy();
            var source = Custom("analyze", "typescript");
            var definition = CreatePipeline(new[] { source, Mcp(config: Config(CustomPolicy("tool-guard"))) }, config: Config(policy));
            var snapshots = ChildDagCompositionTestData.CreateSnapshotService();
            var snapshot = await snapshots.FreezeDefinitionAsync(definition, "parent");
            var before = await new AiPipelineResolver(new ProbeRegistry(), Factories()).ResolveAsync(definition);
            policy.Name = "new-policy";
            policy.ExecutionLanguage = "dotnet";
            var restored = await snapshots.LoadDefinitionAsync(snapshot);
            var after = await new AiPipelineResolver(new ProbeRegistry(), Factories()).ResolveAsync(restored);
            Assert.Equal(before.ExecutionLanguage, after.ExecutionLanguage);
            for (var i = 0; i < before.Steps.Count; i++)
            {
                Assert.Equal(before.Steps[i].InvocationBinding, after.Steps[i].InvocationBinding);
                Assert.Equal(before.Steps[i].ConcurrencyPolicyBindings.ToArray(), after.Steps[i].ConcurrencyPolicyBindings.ToArray());
                Assert.NotSame(before.Steps[i].Step, after.Steps[i].Step);
                Assert.Empty(((ProbeStep)after.Steps[i].Step).Calls);
            }
            Assert.Equal("guard", Assert.Single(after.Steps.Single(x => x.Name == "analyze").ConcurrencyPolicyBindings).PolicyName);
            Assert.Equal("python", Assert.Single(after.Steps.Single(x => x.Name == "analyze").ConcurrencyPolicyBindings).Invocation.ExecutionLanguage);
            var json = await snapshots.LoadDefinitionJsonAsync(snapshot);
            Assert.DoesNotContain("ConcurrencyPolicyBindings", json);
            Assert.DoesNotContain("ProbeStep", json);
            Assert.DoesNotContain("InvocationBinding", json);
        }

        [Fact]
        public async Task Reloading_A_Frozen_Custom_Definition_Still_Requires_An_Installed_Capability()
        {
            var snapshots = ChildDagCompositionTestData.CreateSnapshotService();
            var snapshot = await snapshots.FreezeDefinitionAsync(CreatePipeline(new[] { Custom() }), "parent");
            var restored = await snapshots.LoadDefinitionAsync(snapshot);
            var native = new ProbeRegistry();
            await Assert.ThrowsAsync<NotSupportedException>(() => new AiPipelineResolver(native).ResolveAsync(restored));
            Assert.Equal(0, native.Calls);
        }
    }
}
