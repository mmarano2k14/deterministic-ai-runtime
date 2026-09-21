using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Execution;
using Multiplexed.AI.Runtime.Invocation.Workers.Policies;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.Policies
{
    public sealed class AiDelegationPolicyPublicationPreparerTests
    {
        [Fact]
        public async Task Exact_Pinned_Delegation_Implementation_Is_Materialized()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(Upload("python"));
            var run = await fixture.CreateAsync(publication);
            var function = Assert.Single(publication.Manifest.Functions);

            var bundle = await fixture.AsAsync(() =>
                CreatePreparer(fixture).PrepareAsync(Request(run.ExecutionId, function, "python")));

            Assert.Equal(function.ImplementationRef, bundle.Target.ImplementationRef);
            Assert.Equal("python", bundle.Target.ExecutionLanguage);
            Assert.Equal(publication.PublicationRef, bundle.Target.PublicationRef);
            Assert.Contains(bundle.Sources, source => source.Path == "policy.txt");
        }

        [Fact]
        public async Task Changed_Implementation_Reference_Is_Refused()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(Upload("python"));
            var run = await fixture.CreateAsync(publication);
            var function = Assert.Single(publication.Manifest.Functions);
            var request = Request(run.ExecutionId, function, "python") with { ImplementationRef = "impl-missing" };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.AsAsync(() => CreatePreparer(fixture).PrepareAsync(request)));
        }

        [Fact]
        public async Task Different_Tenant_Cannot_Read_The_Pinned_Delegation_Policy()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(Upload("python"));
            var run = await fixture.CreateAsync(publication);
            var function = Assert.Single(publication.Manifest.Functions);
            var request = Request(run.ExecutionId, function, "python") with
            {
                Context = Request(run.ExecutionId, function, "python").Context with { TenantId = "tenant-b" }
            };

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                fixture.AsAsync(() => CreatePreparer(fixture).PrepareAsync(request)));
        }

        [Fact]
        public async Task Pipeline_Scope_Cannot_Claim_A_Child_Step_Owner()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(Upload("python", stepScoped: false));
            var run = await fixture.CreateAsync(publication);
            var function = Assert.Single(publication.Manifest.Functions);
            var request = Request(run.ExecutionId, function, "python", stepScoped: false) with
            {
                OwnerStepName = "invoke-child"
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.AsAsync(() => CreatePreparer(fixture).PrepareAsync(request)));
        }

        [Fact]
        public async Task Step_Scope_Must_Match_The_Parent_Call_Site()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(Upload("python", stepScoped: true));
            var run = await fixture.CreateAsync(publication);
            var function = Assert.Single(publication.Manifest.Functions);
            var request = Request(run.ExecutionId, function, "python", stepScoped: true) with
            {
                OwnerStepName = "other"
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.AsAsync(() => CreatePreparer(fixture).PrepareAsync(request)));
        }

        [Fact]
        public async Task Project_Drift_After_Publication_Is_Rejected()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(Upload("python"));
            var run = await fixture.CreateAsync(publication);
            var function = Assert.Single(publication.Manifest.Functions);
            var store = new PolicyOwnershipDriftExecutionStore(fixture.Store, snapshot =>
            {
                snapshot.Project = "other-project";
                return snapshot;
            });

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                fixture.AsAsync(() => CreatePreparer(fixture, store).PrepareAsync(Request(run.ExecutionId, function, "python"))));
        }

        [Fact]
        public async Task Namespace_Drift_After_Publication_Is_Rejected()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(Upload("python"));
            var run = await fixture.CreateAsync(publication);
            var function = Assert.Single(publication.Manifest.Functions);
            var store = new PolicyOwnershipDriftExecutionStore(fixture.Store, snapshot =>
            {
                snapshot.CurrentNamespace = "other-namespace";
                return snapshot;
            });

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                fixture.AsAsync(() => CreatePreparer(fixture, store).PrepareAsync(Request(run.ExecutionId, function, "python"))));
        }

        private static AiDelegationPolicyPublicationPreparer CreatePreparer(
            PublicationTestSupport.Fixture fixture) => CreatePreparer(fixture, fixture.Store);

        private static AiDelegationPolicyPublicationPreparer CreatePreparer(
            PublicationTestSupport.Fixture fixture,
            IAiExecutionStore store) => new(
                store,
                fixture.Accessor,
                fixture.ControlPlane,
                fixture.Services.GetRequiredService<AiPublicationIdentity>(),
                fixture.Services.GetRequiredService<AiPublicationOptions>(),
                fixture.Services.GetRequiredService<AiImmutablePublicationStore>());

        private static AiDelegationPolicyRequest Request(
            string executionId,
            AiPublicationFunction function,
            string language,
            bool stepScoped = true) => new(
                "delegation-request-a",
                "delegation-guard",
                stepScoped ? "Step" : "Pipeline",
                stepScoped ? "invoke-child" : null,
                language,
                function.ImplementationRef,
                DateTimeOffset.UtcNow.AddSeconds(5),
                new AiDelegationPolicyInput(
                    PublicationTestSupport.Scope.TenantId,
                    PublicationTestSupport.Scope.TenantGroupId,
                    executionId,
                    "invoke-child",
                    "child-analysis",
                    "1",
                    "child-key",
                    0),
                JsonSerializer.SerializeToElement(new { region = "eu" }));

        private static AiPipelinePublicationUpload Upload(string language, bool stepScoped = true)
        {
            var policy = new AiConfiguredPolicyDefinition
            {
                Name = "delegation-guard",
                Kind = "Delegation",
                Invocation = new AiInvocationDefinition { Kind = AiInvocationKind.Custom }
            };
            var delegation = new AiChildDelegationPolicyDefinition
            {
                Policies = new List<AiConfiguredPolicyDefinition> { policy }
            };
            var child = new AiPipelineDefinition
            {
                Name = "child-analysis",
                Version = "1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps = new[] { new AiPipelineStepDefinition { Name = "native", StepKey = "native" } }
            };
            var stepConfig = new Dictionary<string, object?>
            {
                [ExecuteChildDagStep.ChildDagIdConfigKey] = child.Name,
                [ExecuteChildDagStep.ChildDagVersionConfigKey] = child.Version,
                [ExecuteChildDagStep.LogicalInvocationKeyConfigKey] = "child-key",
                [ExecuteChildDagStep.ChildDagDefinitionConfigKey] = child
            };
            if (stepScoped)
            {
                stepConfig[AiChildDelegationPolicyDefinition.ConfigKey] = delegation;
            }

            var step = new AiPipelineStepDefinition
            {
                Name = "invoke-child",
                StepKey = ExecuteChildDagStep.StepKey,
                Config = stepConfig
            };
            var definition = new AiPipelineDefinition
            {
                Name = "published-delegation",
                Version = "1",
                ExecutionMode = AiExecutionMode.Dag,
                ExecutionLanguage = language,
                Config = stepScoped
                    ? new Dictionary<string, object?>()
                    : new Dictionary<string, object?>
                    {
                        [AiChildDelegationPolicyDefinition.ConfigKey] = delegation
                    },
                Steps = new[] { step }
            };
            var function = new AiPublicationFunctionUpload(
                new AiPublicationCallSite(AiPublicationFunctionKind.DelegationPolicy, stepScoped ? "invoke-child" : null, 0),
                language + "-fixed",
                "policy.txt",
                "run",
                new[] { new AiPublicationFileUpload("policy.txt", Encoding.UTF8.GetBytes("delegation-policy")) },
                Array.Empty<AiPublicationDependencyUpload>());
            return new AiPipelinePublicationUpload(definition, new[] { function });
        }
    }
}
