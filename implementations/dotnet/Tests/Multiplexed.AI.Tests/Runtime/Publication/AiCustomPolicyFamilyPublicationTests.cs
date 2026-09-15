using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Abstractions.AI.Retry;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Execution;

namespace Multiplexed.AI.Tests.Runtime.Publication
{
    /// <summary>Freezes immutable publication support for the selected custom policy families without enabling their hosted adapters.</summary>
    public sealed class AiCustomPolicyFamilyPublicationTests
    {
        private static AiConfiguredPolicyDefinition CustomPolicy(string name, string family, string? language = null) => new()
        {
            Name = name,
            Kind = family,
            ExecutionLanguage = language,
            Invocation = new AiInvocationDefinition { Kind = AiInvocationKind.Custom }
        };

        private static AiConfiguredPolicyDefinition BoundCustomPolicy(string name, string family, string? language = null) => new()
        {
            Name = name,
            Kind = family,
            ExecutionLanguage = language,
            Invocation = new AiInvocationDefinition
            {
                Kind = AiInvocationKind.Custom,
                ImplementationRef = $"implementation:{name}"
            }
        };

        [Fact]
        public async Task Retry_Custom_Code_Is_Published_As_A_Family_Specific_Call_Site()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var upload = PublicationTestSupport.Upload();
            var config = new Dictionary<string, object?>
            {
                ["retry"] = new AiRetryPolicyDefinition
                {
                    Policies = new List<AiConfiguredPolicyDefinition> { CustomPolicy("retry.remote", "Retry") },
                    MaxRetries = 3
                }
            };
            var definition = PublicationTestSupport.Copy(upload.Definition, config: config);
            var functions = upload.Functions.Append(
                PublicationTestSupport.Function(new AiPublicationCallSite(AiPublicationFunctionKind.RetryPolicy, null, 0))).ToArray();

            var publication = await fixture.PublishAsync(new(definition, functions));
            var policy = Assert.Single(publication.Manifest.Functions.Where(value => value.Site.Kind == AiPublicationFunctionKind.RetryPolicy));
            Assert.Equal("retry.remote", policy.LogicalName);
            Assert.Equal("python", policy.ExecutionLanguage);
            Assert.Null(policy.Site.DefinitionPath);
            await fixture.ReadAsync(publication.PublicationRef);
        }

        [Fact]
        public async Task Delegation_Custom_Code_Is_Published_At_The_Existing_Child_Dag_Checkpoint()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var child = new AiPipelineDefinition
            {
                Name = "child",
                Version = "1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps = new[] { new AiPipelineStepDefinition { Name = "native", StepKey = "native" } }
            };
            var delegation = new AiChildDelegationPolicyDefinition
            {
                Policies = new List<AiConfiguredPolicyDefinition> { CustomPolicy("delegation.remote", "Delegation", "dotnet") }
            };
            var childStep = new AiPipelineStepDefinition
            {
                Name = "invoke-child",
                StepKey = ExecuteChildDagStep.StepKey,
                Config = new Dictionary<string, object?>
                {
                    [ExecuteChildDagStep.ChildDagIdConfigKey] = child.Name,
                    [ExecuteChildDagStep.ChildDagVersionConfigKey] = child.Version,
                    [ExecuteChildDagStep.LogicalInvocationKeyConfigKey] = "child",
                    [ExecuteChildDagStep.ChildDagDefinitionConfigKey] = child,
                    ["delegation"] = delegation
                }
            };
            var definition = PublicationTestSupport.Copy(
                PublicationTestSupport.Definition(language: "python", secondLanguage: null),
                steps: new[] { childStep });
            var functions = new[]
            {
                PublicationTestSupport.Function(
                    new AiPublicationCallSite(AiPublicationFunctionKind.DelegationPolicy, "invoke-child", 0),
                    "dotnet")
            };

            var publication = await fixture.PublishAsync(new(definition, functions));
            var policy = Assert.Single(publication.Manifest.Functions);
            Assert.Equal(AiPublicationFunctionKind.DelegationPolicy, policy.Site.Kind);
            Assert.Equal("invoke-child", policy.Site.StepName);
            Assert.Equal("delegation.remote", policy.LogicalName);
            Assert.Equal("dotnet", policy.ExecutionLanguage);
            await fixture.ReadAsync(publication.PublicationRef);
        }

        [Fact]
        public async Task Retry_Custom_Publication_Advertises_Hosted_Execution_Without_Native_Fallback()
        {
            var capability = AiCustomPolicyFamilyCapabilities.Get(Multiplexed.AI.Abstractions.AI.Policies.AiPolicyKind.Retry);
            Assert.True(capability.SupportsCustomPublication);
            Assert.True(capability.SupportsHostedExecution);

            var declaration = BoundCustomPolicy("retry.remote", "Retry");
            Assert.Throws<NotSupportedException>(() => Multiplexed.AI.Runtime.Invocation.AiInvocationBindingResolver.EnsureNativePolicy(declaration));
            await Task.CompletedTask;
        }

        [Fact]
        public async Task Delegation_Custom_Publication_Advertises_Hosted_Execution_Without_Native_Fallback()
        {
            var capability = AiCustomPolicyFamilyCapabilities.Get(Multiplexed.AI.Abstractions.AI.Policies.AiPolicyKind.Delegation);
            Assert.True(capability.SupportsCustomPublication);
            Assert.True(capability.SupportsHostedExecution);

            var declaration = BoundCustomPolicy("delegation.remote", "Delegation");
            Assert.Throws<NotSupportedException>(() => Multiplexed.AI.Runtime.Invocation.AiInvocationBindingResolver.EnsureNativePolicy(declaration));
            await Task.CompletedTask;
        }
    }
}
