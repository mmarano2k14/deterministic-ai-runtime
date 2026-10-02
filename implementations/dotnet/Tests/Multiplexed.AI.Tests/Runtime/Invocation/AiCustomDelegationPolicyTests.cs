using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Execution;
using Multiplexed.AI.Runtime.Invocation;
using Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    /// <summary>Freezes Delegation binding semantics and adapter authority before child allocation.</summary>
    public sealed class AiCustomDelegationPolicyTests
    {
        [Fact]
        public void Pipeline_Delegation_Custom_Binding_Uses_Pipeline_Scope()
        {
            var step = ChildStep();
            var pipeline = Pipeline("python", DelegationConfig(Custom("delegation.remote")), step);

            var binding = Assert.Single(new AiDelegationPolicyBindingResolver().Resolve(pipeline, step));

            Assert.Equal(AiPolicyBindingScope.Pipeline, binding.Scope);
            Assert.Null(binding.OwnerStepName);
            Assert.Equal("python", binding.Invocation.ExecutionLanguage);
        }

        [Fact]
        public void Step_Delegation_Replaces_Pipeline_Delegation_For_That_Child_Checkpoint()
        {
            var step = ChildStep(DelegationConfig(Custom("delegation.step", "typescript")));
            var pipeline = Pipeline("python", DelegationConfig(Custom("delegation.pipeline")), step);

            var binding = Assert.Single(new AiDelegationPolicyBindingResolver().Resolve(pipeline, step));

            Assert.Equal("delegation.step", binding.PolicyName);
            Assert.Equal(AiPolicyBindingScope.Step, binding.Scope);
            Assert.Equal(step.Name, binding.OwnerStepName);
            Assert.Equal("typescript", binding.Invocation.ExecutionLanguage);
        }

        [Fact]
        public void Non_Child_Step_Does_Not_Expose_A_Delegation_Checkpoint()
        {
            var step = new AiPipelineStepDefinition { Name = "work", StepKey = "native" };
            var pipeline = Pipeline("python", DelegationConfig(Custom("delegation.pipeline")), step);

            Assert.Empty(new AiDelegationPolicyBindingResolver().Resolve(pipeline, step));
        }

        [Fact]
        public void Policy_Local_Language_Overrides_Pipeline_Default()
        {
            var step = ChildStep();
            var pipeline = Pipeline("python", DelegationConfig(Custom("delegation.remote", "dotnet")), step);

            var binding = Assert.Single(new AiDelegationPolicyBindingResolver().Resolve(pipeline, step));

            Assert.Equal("dotnet", binding.Invocation.ExecutionLanguage);
            Assert.Equal(AiExecutionLanguageSource.Policy, binding.Invocation.LanguageSource);
        }

        [Fact]
        public void Custom_Delegation_Without_Effective_Language_Is_Refused()
        {
            var step = ChildStep();
            var pipeline = Pipeline(null, DelegationConfig(Custom("delegation.remote")), step);

            Assert.Throws<InvalidOperationException>(() =>
                new AiDelegationPolicyBindingResolver().Resolve(pipeline, step));
        }

        [Fact]
        public void Native_And_Custom_Delegation_Bindings_Preserve_Declaration_Order()
        {
            var step = ChildStep();
            var pipeline = Pipeline("python", DelegationConfig(
                new AiConfiguredPolicyDefinition { Name = "delegation.native" },
                Custom("delegation.remote")), step);

            var bindings = new AiDelegationPolicyBindingResolver().Resolve(pipeline, step);

            Assert.Equal(2, bindings.Count);
            Assert.Equal(AiInvocationKind.Native, bindings[0].Invocation.Kind);
            Assert.Equal(AiInvocationKind.Custom, bindings[1].Invocation.Kind);
        }

        [Fact]
        public void Delegation_Capability_Is_Hosted()
        {
            var capability = AiCustomPolicyFamilyCapabilities.Get(AiPolicyKind.Delegation);
            Assert.True(capability.SupportsHostedExecution);
            Assert.Equal(AiCustomPolicyFamilyContracts.DelegationV1, capability.ContractId);
        }

        [Theory]
        [InlineData("approve", AiPolicyResultKind.Success)]
        [InlineData("deny", AiPolicyResultKind.Block)]
        public async Task Hosted_Adapter_Maps_Only_Approve_Or_Deny_Without_Mutating_The_Relation(
            string decision,
            AiPolicyResultKind expectedKind)
        {
            var declaration = Custom("delegation.remote");
            var pipelineStep = ChildStep(DelegationConfig(declaration));
            var pipeline = Pipeline("python", null, pipelineStep);
            var binding = Assert.Single(new AiDelegationPolicyBindingResolver().Resolve(pipeline, pipelineStep));
            var transport = new Transport(decision);
            var factory = new AiDelegationPolicyAdapterFactory(new[] { transport });
            var relation = ChildDagCompositionTestData.CreateRelation(AiChildExecutionRelationStatus.DelegationPolicyPending);
            var context = StepContext(binding, relation.ParentCallSiteId);
            var adapter = factory.Bind(context, declaration, binding);

            var result = await adapter.ExecuteAsync(new AiChildDelegationPolicyContext
            {
                Relation = relation,
                Config = declaration.Config
            });

            Assert.Equal(expectedKind, result.Kind);
            Assert.Equal(AiChildExecutionRelationStatus.DelegationPolicyPending, relation.Status);
            Assert.Null(relation.ChildExecutionId);
            Assert.Single(transport.Requests);
            Assert.Equal(relation.ChildInvocationKey, transport.Requests[0].Context.ChildInvocationKey);
        }

        [Fact]
        public async Task Technical_Transport_Failure_Is_Not_Converted_Into_Approval_Or_Denial()
        {
            var declaration = Custom("delegation.remote");
            var pipelineStep = ChildStep(DelegationConfig(declaration));
            var pipeline = Pipeline("python", null, pipelineStep);
            var binding = Assert.Single(new AiDelegationPolicyBindingResolver().Resolve(pipeline, pipelineStep));
            var transport = new Transport("approve") { Error = new IOException("transport failed") };
            var factory = new AiDelegationPolicyAdapterFactory(new[] { transport });
            var relation = ChildDagCompositionTestData.CreateRelation(AiChildExecutionRelationStatus.DelegationPolicyPending);
            var adapter = factory.Bind(StepContext(binding, relation.ParentCallSiteId), declaration, binding);

            await Assert.ThrowsAsync<IOException>(() => adapter.ExecuteAsync(new AiChildDelegationPolicyContext
            {
                Relation = relation,
                Config = declaration.Config
            }));

            Assert.Equal(AiChildExecutionRelationStatus.DelegationPolicyPending, relation.Status);
            Assert.Null(relation.ChildExecutionId);
        }

        private static AiStepExecutionContext StepContext(AiPolicyInvocationBinding binding, string stepName)
        {
            var record = ChildDagCompositionTestData.CreateParentRecord(AiExecutionStatus.Running);
            var state = ChildDagCompositionTestData.CreateParentState(AiStepExecutionStatus.Running);
            var resolved = new ResolvedAiPipelineStep
            {
                Name = stepName,
                StepKey = ExecuteChildDagStep.StepKey,
                DelegationPolicyBindings = new[] { binding },
                Step = new NoOpStep()
            };
            return new AiStepExecutionContext(
                ChildDagCompositionTestData.CreateExecutionContext(record, state),
                resolved);
        }

        private static AiConfiguredPolicyDefinition Custom(string name, string? language = null) => new()
        {
            Name = name,
            Kind = "Delegation",
            ExecutionLanguage = language,
            Invocation = new AiInvocationDefinition
            {
                Kind = AiInvocationKind.Custom,
                ImplementationRef = "impl-" + name
            },
            Config = new Dictionary<string, object?> { ["region"] = "eu" }
        };

        private static Dictionary<string, object?> DelegationConfig(params AiConfiguredPolicyDefinition[] policies) => new()
        {
            [AiChildDelegationPolicyDefinition.ConfigKey] = new AiChildDelegationPolicyDefinition
            {
                Policies = policies.ToList()
            }
        };

        private static AiPipelineStepDefinition ChildStep(Dictionary<string, object?>? config = null) => new()
        {
            Name = ChildDagCompositionTestData.ParentCallSiteId,
            StepKey = ExecuteChildDagStep.StepKey,
            Config = config ?? new Dictionary<string, object?>()
        };

        private static AiPipelineDefinition Pipeline(
            string? language,
            Dictionary<string, object?>? config,
            AiPipelineStepDefinition step) => new()
        {
            Name = "parent-pipeline",
            Version = "1",
            ExecutionMode = AiExecutionMode.Dag,
            ExecutionLanguage = language,
            Config = config ?? new Dictionary<string, object?>(),
            Steps = new[] { step }
        };

        private sealed class Transport : IAiDelegationPolicyTransport
        {
            private readonly string _decision;
            internal readonly List<AiDelegationPolicyRequest> Requests = new();
            internal Exception? Error;

            internal Transport(string decision) => _decision = decision;
            public string ExecutionLanguage => "python";

            public Task<JsonElement> EvaluateAsync(
                AiDelegationPolicyRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Requests.Add(request);
                if (Error is not null) throw Error;
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    schemaVersion = 1,
                    requestId = request.RequestId,
                    policyKind = "delegation",
                    decision = _decision,
                    reason = _decision == "deny" ? "not permitted" : null
                }));
            }
        }

        private sealed class NoOpStep : Multiplexed.Abstractions.AI.Steps.IAiStep
        {
            public string Name => ExecuteChildDagStep.StepKey;
            public Task<AiStepResult> ExecuteAsync(AiStepExecutionContext context, CancellationToken cancellationToken = default) =>
                Task.FromResult(AiStepResult.Ok());

            Task<AiStepResult> IAiStep.ExecuteAsync(AiStepExecutionContext context, CancellationToken cancellationToken)
            {
                throw new NotImplementedException();
            }
        }
    }
}
