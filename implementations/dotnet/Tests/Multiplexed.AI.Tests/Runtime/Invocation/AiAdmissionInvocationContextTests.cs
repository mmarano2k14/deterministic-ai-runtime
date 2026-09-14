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
    /// <summary>
    /// Exercises the real admission policy/gate boundary with local doubles. Invoking
    /// the private boundary isolates it from Redis claim/recovery and is not a claim test.
    /// </summary>
    public sealed class AiAdmissionInvocationContextTests
    {
        [Fact]
        public async Task Admission_View_Reuses_The_Compiled_Binding_Without_Copying_An_Executable()
        {
            var source = Custom(language: "typescript");
            var definition = CreatePipeline(new[] { source }, config: Config(CustomPolicy()));
            var factory = new ProbeFactory(AiInvocationKind.Custom, "typescript");
            var plan = await new AiPipelineResolver(new ProbeRegistry(), new[] { factory }).ResolveAsync(definition);
            var resolved = Assert.Single(plan.Steps);
            var execution = CreateExecution();
            var effective = new DefaultAiConcurrencyDefinitionResolver().Resolve(definition, source);
            var context = AiStepAdmissionContextFactory.Create(execution, resolved, effective);
            Assert.NotSame(resolved, context.Step);
            Assert.Null(context.Step.Step);
            Assert.Same(resolved.InvocationBinding, context.InvocationBinding);
            Assert.Same(resolved.ConcurrencyPolicyBindings, context.ConcurrencyPolicyBindings);
            Assert.Same(effective, context.ConcurrencyAdmissionDefinition);
            Assert.Equal("typescript", context.InvocationBinding.ExecutionLanguage);
            Assert.Equal("python", Assert.Single(context.ConcurrencyPolicyBindings).Invocation.ExecutionLanguage);
            Assert.Single(factory.Created);
            Assert.Empty(factory.Created.Single().Calls);
        }

        [Fact]
        public void Declared_Custom_Step_With_No_Resolved_Binding_Is_Refused_At_Admission()
        {
            var source = Custom();
            var unbound = new ResolvedAiPipelineStep { Name = "work", StepKey = "same-key", Invocation = source.Invocation };
            Assert.Throws<InvalidOperationException>(() => AiStepAdmissionContextFactory.Create(CreateExecution(), unbound, new AiConcurrencyDefinition()));
        }

        [Fact]
        public void Legacy_Context_Still_Exposes_Native_Without_New_Metadata()
        {
            var context = new AiStepExecutionContext(CreateExecution(), new ResolvedAiPipelineStep { Name = "legacy", StepKey = "legacy" });
            Assert.Equal(AiInvocationBinding.Native, context.InvocationBinding);
            Assert.Empty(context.ConcurrencyPolicyBindings);
            Assert.Null(context.ConcurrencyAdmissionDefinition);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Pipeline_Native_Policy_Is_Evaluated_Before_Gate_Despite_Empty_Step_Config(bool block)
        {
            var source = Custom(language: "typescript");
            var definition = CreatePipeline(new[] { source }, config: Config(new AiConfiguredPolicyDefinition { Name = "guard" }));
            var adapter = new ProbeFactory(AiInvocationKind.Custom, "typescript");
            var plan = await new AiPipelineResolver(new ProbeRegistry(), new[] { adapter }).ResolveAsync(definition);
            var fixture = CreateFixture(plan, source, new ProbePolicy(block: block));
            var decision = await InvokeAdmissionAsync(fixture, plan, source);
            Assert.Equal(!block, decision.Allowed);
            Assert.Single(fixture.Policy.Calls);
            Assert.Equal(block ? 0 : 1, fixture.Gate.AcquireCalls);
            Assert.Equal(0, fixture.Gate.ReleaseCalls);
            Assert.Single(adapter.Created);
            Assert.Empty(adapter.Created.Single().Calls);
            var context = Assert.Single(fixture.PolicyFactory.Contexts);
            Assert.Null(context.Step.Step);
            Assert.Same(plan.Steps[0].InvocationBinding, context.InvocationBinding);
            Assert.Equal(AiPolicyBindingScope.Pipeline, Assert.Single(context.ConcurrencyPolicyBindings).Scope);
            Assert.Same(fixture.Admission.Definition, context.ConcurrencyAdmissionDefinition);
            Assert.Empty(fixture.Execution.State.Steps[source.Name].Config);
            if (!block) Assert.Same(fixture.Admission.Definition, fixture.Gate.Definition);
        }

        [Fact]
        public async Task Custom_Pipeline_Policy_Is_Still_Refused_And_Cannot_Be_Skipped_Or_Use_Homonymous_Native()
        {
            var source = Native();
            var definition = CreatePipeline(new[] { source }, config: Config(CustomPolicy()));
            var registry = new ProbeRegistry();
            var plan = await new AiPipelineResolver(registry).ResolveAsync(definition);
            var fixture = CreateFixture(plan, source, new ProbePolicy());
            await Assert.ThrowsAsync<NotSupportedException>(() => InvokeAdmissionAsync(fixture, plan, source));
            Assert.Empty(fixture.Policy.Calls);
            Assert.Equal(0, fixture.Gate.AcquireCalls);
            Assert.Empty(registry.Implementation.Calls);
            var context = Assert.Single(fixture.PolicyFactory.Contexts);
            Assert.Equal("python", Assert.Single(context.ConcurrencyPolicyBindings).Invocation.ExecutionLanguage);
        }

        [Fact]
        public async Task Local_Policy_List_Replaces_Pipeline_List_And_Receives_Its_Own_Config()
        {
            var localPolicy = new AiConfiguredPolicyDefinition
            {
                Name = "local", Config = new Dictionary<string, object?> { ["marker"] = "local-config" }
            };
            var source = Native(config: Config(localPolicy));
            var definition = CreatePipeline(new[] { source }, config: Config(new AiConfiguredPolicyDefinition { Name = "pipeline-only" }));
            var plan = await new AiPipelineResolver(new ProbeRegistry()).ResolveAsync(definition);
            var fixture = CreateFixture(plan, source, new ProbePolicy("local"));
            var decision = await InvokeAdmissionAsync(fixture, plan, source);
            Assert.True(decision.Allowed);
            var policyContext = Assert.Single(fixture.Policy.Calls);
            Assert.Equal("local-config", policyContext.Config["marker"]?.ToString());
            var binding = Assert.Single(Assert.Single(fixture.PolicyFactory.Contexts).ConcurrencyPolicyBindings);
            Assert.Equal("local", binding.PolicyName);
            Assert.Equal(AiPolicyBindingScope.Step, binding.Scope);
            Assert.Equal("work", binding.OwnerStepName);
        }

        [Fact]
        public async Task No_Policy_Fast_Path_Does_Not_Construct_A_Policy_Engine_Or_Run_An_Adapter()
        {
            var source = Custom();
            var plan = await new AiPipelineResolver(new ProbeRegistry(), Factories()).ResolveAsync(CreatePipeline(new[] { source }));
            var fixture = CreateFixture(plan, source, new ProbePolicy());
            Assert.True((await InvokeAdmissionAsync(fixture, plan, source)).Allowed);
            Assert.Empty(fixture.PolicyFactory.Contexts);
            Assert.Equal(1, fixture.Gate.AcquireCalls);
            Assert.Empty(((ProbeStep)plan.Steps[0].Step).Calls);
        }

        [Fact]
        public async Task Exact_Logical_Name_Wins_Over_Case_Insensitive_Fallback()
        {
            var desired = Custom("WORK", "typescript");
            var definition = CreatePipeline(new[] { Custom("work"), desired }, config: Config(new AiConfiguredPolicyDefinition { Name = "guard" }));
            var plan = await new AiPipelineResolver(new ProbeRegistry(), Factories()).ResolveAsync(definition);
            var fixture = CreateFixture(plan, desired, new ProbePolicy());
            await InvokeAdmissionAsync(fixture, plan, desired);
            var context = Assert.Single(fixture.PolicyFactory.Contexts);
            Assert.Equal("WORK", context.StepName);
            Assert.Equal("typescript", context.InvocationBinding.ExecutionLanguage);
            Assert.Same(plan.Steps.Single(x => x.Name == "WORK").InvocationBinding, context.InvocationBinding);
        }

        [Fact]
        public async Task Admission_Does_Not_Reparse_Step_Config_To_Change_Prepared_Decision_Input()
        {
            var source = Native();
            var definition = CreatePipeline(new[] { source }, config: Config(new AiConfiguredPolicyDefinition { Name = "guard" }));
            var plan = await new AiPipelineResolver(new ProbeRegistry()).ResolveAsync(definition);
            var fixture = CreateFixture(plan, source, new ProbePolicy(block: true));
            // Simulate a different hot-state section after admission preparation. The
            // decision must still use the already prepared definition, as the gate does.
            fixture.Execution.State.Steps[source.Name].Config["concurrency"] = new AiConcurrencyDefinition { Enabled = false };
            Assert.False((await InvokeAdmissionAsync(fixture, plan, source)).Allowed);
            Assert.Single(fixture.Policy.Calls);
            Assert.Equal(0, fixture.Gate.AcquireCalls);
        }

        private static AdmissionFixture CreateFixture(ResolvedAiPipeline plan, AiPipelineStepDefinition source, ProbePolicy policy)
        {
            var execution = CreateExecution();
            execution.EnsureStepInitialized(plan.Steps.Single(x => x.Name == source.Name));
            var obs = new TestObservability();
            var factory = new ProbePolicyFactory(obs, policy);
            var gate = new ProbeGate();
            var services = Ml1bPropertyProxy.For<IAiDagExecutionEngineServices>(new Dictionary<string, object?>
            {
                ["get_Services"] = execution.Services,
                ["get_StateReader"] = execution.StateReader,
                ["get_StateWriter"] = execution.StateWriter,
                ["get_ObservabilityService"] = obs,
                ["get_PolicyEngineFactory"] = factory,
                ["get_ConcurrencyGate"] = gate
            });
            var admission = AiDagExecutionHelpers.CreateConcurrencyAdmission(
                execution.ExecutionId, "test:v1", source.Name, "runtime-1",
                execution.State.Steps[source.Name], plan.Config, source, new DefaultAiConcurrencyDefinitionResolver());
            return new AdmissionFixture(new AiDagStepClaimService(services), execution, admission, policy, factory, gate);
        }

        private static Task<AiConcurrencyDecision> InvokeAdmissionAsync(AdmissionFixture fixture, ResolvedAiPipeline plan, AiPipelineStepDefinition source)
        {
            var method = typeof(AiDagStepClaimService).GetMethod("TryAcquireConcurrencyLeaseAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return (Task<AiConcurrencyDecision>)method!.Invoke(fixture.Service, new object[]
            {
                plan, fixture.Admission.Context, fixture.Admission.Definition,
                fixture.Execution.State, fixture.Execution.State.Steps[source.Name], source, source.Name, CancellationToken.None
            })!;
        }

        private sealed record AdmissionFixture(
            AiDagStepClaimService Service, AiExecutionContext Execution, AiDagConcurrencyAdmission Admission,
            ProbePolicy Policy, ProbePolicyFactory PolicyFactory, ProbeGate Gate);
    }
}
