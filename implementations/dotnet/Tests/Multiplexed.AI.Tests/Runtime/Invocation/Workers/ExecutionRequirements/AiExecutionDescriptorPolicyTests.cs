using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Invocation.Workers.Policies;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.ExecutionRequirements
{
    /// <summary>Real publication/pinning and policy materialization preserve server-only requirements at both declaration scopes.</summary>
    public sealed class AiExecutionDescriptorPolicyTests
    {
        [Theory]
        [InlineData("python", false)]
        [InlineData("python", true)]
        [InlineData("typescript", false)]
        [InlineData("typescript", true)]
        [InlineData("dotnet", false)]
        [InlineData("dotnet", true)]
        public async Task Pinned_Policy_Requirements_Survive_Materialization_Without_A_Durable_Step_Invocation(string language, bool stepScoped)
        {
            using var fixture = new ExecutionRequirementsTestSupport.Fixture();
            var publication = await fixture.PublishAsync(Upload(language, stepScoped));
            var run = await fixture.CreateAsync(publication);
            var function = Assert.Single(publication.Manifest.Functions);
            var request = Request(run.ExecutionId, function, language, stepScoped);
            var preparer = fixture.Services.GetRequiredService<AiConcurrencyPolicyPublicationPreparer>();
            var code = await fixture.Base.AsAsync(() => preparer.PrepareAsync(request));
            Assert.Equal(fixture.Catalog.Descriptors[language + "-fixed"], code.ExecutionDescriptor);
            Assert.Equal(function.Environment.Sha256, code.Target.EnvironmentSha256);
            Assert.Equal(publication.PublicationRef, code.Target.PublicationRef);
            Assert.Equal("[]", fixture.Base.JournalStore.Export());
            Assert.DoesNotContain("ExecutionDescriptor", JsonSerializer.Serialize(code));
        }

        [Fact]
        public async Task A_Policy_Cannot_Use_An_Environment_With_Replaced_Requirements()
        {
            using var fixture = new ExecutionRequirementsTestSupport.Fixture();
            var publication = await fixture.PublishAsync(Upload("python", false));
            var run = await fixture.CreateAsync(publication);
            var descriptor = fixture.Catalog.Descriptors["python-fixed"]!;
            fixture.Catalog.Descriptors["python-fixed"] = descriptor with
                { Requirements = descriptor.Requirements with { NetworkEgress = AiWorkerNetworkEgress.DenyAll } };
            var request = Request(run.ExecutionId, Assert.Single(publication.Manifest.Functions), "python", false);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Base.AsAsync(() =>
                fixture.Services.GetRequiredService<AiConcurrencyPolicyPublicationPreparer>().PrepareAsync(request)));
            Assert.Equal("[]", fixture.Base.JournalStore.Export());
        }

        private static AiConcurrencyPolicyRequest Request(string executionId, AiPublicationFunction function,
            string language, bool stepScoped) => new("policy-request-a", "capacity-guard",
                stepScoped ? "Step" : "Pipeline", stepScoped ? "native" : null, language, function.ImplementationRef,
                DateTimeOffset.UtcNow.AddMinutes(1),
                new AiConcurrencyPolicyInput(PublicationTestSupport.Scope.TenantId, PublicationTestSupport.Scope.TenantGroupId,
                    executionId, "policy-pipeline", "native", "native", "runtime-a", null, null, null),
                JsonSerializer.SerializeToElement(new { limit = 2 }));

        private static AiPipelinePublicationUpload Upload(string language, bool stepScoped)
        {
            var policies = new List<AiConfiguredPolicyDefinition>
            {
                new() { Name = "capacity-guard", Kind = "Concurrency", Invocation = new() { Kind = AiInvocationKind.Custom } }
            };
            var concurrency = new Dictionary<string, object?> { ["concurrency"] = new AiConcurrencyDefinition { Policies = policies } };
            var definition = new AiPipelineDefinition
            {
                Name = "policy-pipeline", Version = "1", ExecutionMode = AiExecutionMode.Dag, ExecutionLanguage = language,
                Config = stepScoped ? new Dictionary<string, object?>() : concurrency,
                Steps = new[] { new AiPipelineStepDefinition { Name = "native", StepKey = "native",
                    Config = stepScoped ? concurrency : new Dictionary<string, object?>() } }
            };
            var function = new AiPublicationFunctionUpload(
                new AiPublicationCallSite(AiPublicationFunctionKind.ConcurrencyPolicy, stepScoped ? "native" : null, 0),
                language + "-fixed", "policy.txt", "run",
                new[] { new AiPublicationFileUpload("policy.txt", Encoding.UTF8.GetBytes("policy-1")) },
                Array.Empty<AiPublicationDependencyUpload>());
            return new(definition, new[] { function });
        }
    }
}
