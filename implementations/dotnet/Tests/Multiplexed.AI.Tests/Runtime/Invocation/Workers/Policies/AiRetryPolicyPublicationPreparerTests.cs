using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.AI.Abstractions.AI.Retry;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Invocation.Workers.Policies;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.Policies
{
    /// <summary>Policy code is selected only from the publication pinned to the owning execution.</summary>
    public sealed class AiRetryPolicyPublicationPreparerTests
    {
        [Theory]
        [InlineData("python")]
        [InlineData("typescript")]
        [InlineData("dotnet")]
        public async Task Exact_Pinned_Policy_Code_Is_Materialized(string language)
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(Upload(language, revision: "1"));
            var run = await fixture.CreateAsync(publication);
            var function = Assert.Single(publication.Manifest.Functions);
            var preparer = CreatePreparer(fixture);

            var bundle = await fixture.AsAsync(() => preparer.PrepareAsync(Request(run.ExecutionId, function, language)));

            Assert.Equal(function.ImplementationRef, bundle.Target.ImplementationRef);
            Assert.Equal(language, bundle.Target.ExecutionLanguage);
            Assert.Equal(language, bundle.Runtime.ExecutionLanguage);
            Assert.Equal("policy.txt", bundle.EntryPointPath);
            Assert.Equal("run", bundle.EntryPointSymbol);
            Assert.Equal("policy-1", Encoding.UTF8.GetString(Decode(Assert.Single(bundle.Sources).Base64Url)));
        }

        [Fact]
        public async Task Later_Publication_Does_Not_Change_An_Existing_Run_Policy()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var first = await fixture.PublishAsync(Upload("python", revision: "1"));
            var run = await fixture.CreateAsync(first, key: "pinned-policy-run");
            var firstFunction = Assert.Single(first.Manifest.Functions);
            var second = await fixture.PublishAsync(Upload("python", revision: "2"));
            Assert.NotEqual(first.PublicationRef, second.PublicationRef);
            var preparer = CreatePreparer(fixture);

            var bundle = await fixture.AsAsync(() => preparer.PrepareAsync(Request(run.ExecutionId, firstFunction, "python")));

            Assert.Equal(firstFunction.ImplementationRef, bundle.Target.ImplementationRef);
            Assert.Equal("policy-1", Encoding.UTF8.GetString(Decode(Assert.Single(bundle.Sources).Base64Url)));
        }


        [Fact]
        public async Task Equivalent_Duplicate_Policy_Sites_Can_Share_The_Same_Immutable_Implementation()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(Upload("python", copies: 2));
            var run = await fixture.CreateAsync(publication);
            var functions = publication.Manifest.Functions.ToArray();
            Assert.Equal(2, functions.Length);
            Assert.Equal(functions[0].ImplementationRef, functions[1].ImplementationRef);
            var preparer = CreatePreparer(fixture);

            var bundle = await fixture.AsAsync(() => preparer.PrepareAsync(Request(run.ExecutionId, functions[0], "python")));

            Assert.Equal(functions[0].ImplementationRef, bundle.Target.ImplementationRef);
            Assert.Equal("policy-1", Encoding.UTF8.GetString(Decode(Assert.Single(bundle.Sources).Base64Url)));
        }

        [Fact]
        public async Task Different_Implementation_Reference_Cannot_Be_Substituted()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(Upload("python"));
            var run = await fixture.CreateAsync(publication);
            var function = Assert.Single(publication.Manifest.Functions);
            var request = Request(run.ExecutionId, function, "python") with { ImplementationRef = "impl-missing" };

            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.AsAsync(() => CreatePreparer(fixture).PrepareAsync(request)));
        }

        [Fact]
        public async Task Different_Tenant_Cannot_Read_The_Pinned_Policy()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(Upload("python"));
            var run = await fixture.CreateAsync(publication);
            var function = Assert.Single(publication.Manifest.Functions);
            var request = Request(run.ExecutionId, function, "python");
            request = request with { Context = request.Context with { TenantId = "tenant-b" } };

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.AsAsync(() => CreatePreparer(fixture).PrepareAsync(request)));
        }

        [Fact]
        public async Task Pipeline_Scope_Cannot_Claim_A_Step_Owner()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(Upload("python"));
            var run = await fixture.CreateAsync(publication);
            var function = Assert.Single(publication.Manifest.Functions);
            var request = Request(run.ExecutionId, function, "python") with { OwnerStepName = "native" };

            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.AsAsync(() => CreatePreparer(fixture).PrepareAsync(request)));
        }

        [Fact]
        public async Task Step_Scope_Must_Match_The_Admission_Step()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(Upload("python", stepScoped: true));
            var run = await fixture.CreateAsync(publication);
            var function = Assert.Single(publication.Manifest.Functions);
            var request = Request(run.ExecutionId, function, "python", stepScoped: true) with { OwnerStepName = "other" };

            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.AsAsync(() => CreatePreparer(fixture).PrepareAsync(request)));
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

        private static AiRetryPolicyPublicationPreparer CreatePreparer(PublicationTestSupport.Fixture fixture) =>
            CreatePreparer(fixture, fixture.Store);

        private static AiRetryPolicyPublicationPreparer CreatePreparer(PublicationTestSupport.Fixture fixture, IAiExecutionStore store) => new(
            store,
            fixture.Accessor,
            fixture.ControlPlane,
            fixture.Services.GetRequiredService<AiPublicationIdentity>(),
            fixture.Services.GetRequiredService<AiPublicationOptions>(),
            fixture.Services.GetRequiredService<AiImmutablePublicationStore>());

        private static AiRetryPolicyRequest Request(
            string executionId,
            AiPublicationFunction function,
            string language,
            bool stepScoped = false) => new(
            "policy-request-a",
            "retry-guard",
            stepScoped ? "Step" : "Pipeline",
            stepScoped ? "native" : null,
            language,
            function.ImplementationRef,
            DateTimeOffset.UtcNow.AddSeconds(5),
            new AiRetryPolicyInput(
                PublicationTestSupport.Scope.TenantId,
                PublicationTestSupport.Scope.TenantGroupId,
                executionId,
                "policy-pipeline",
                "native",
                "native",
                1,
                3,
                "transient",
                "TimeoutException",
                DateTimeOffset.UtcNow),
            JsonSerializer.SerializeToElement(new { mode = "transient" }));

        private static AiPipelinePublicationUpload Upload(
            string language,
            string revision = "1",
            bool stepScoped = false,
            int copies = 1)
        {
            if (copies < 1 || copies > 4) throw new ArgumentOutOfRangeException(nameof(copies));
            var policies = Enumerable.Range(0, copies).Select(_ => new AiConfiguredPolicyDefinition
            {
                Name = "retry-guard",
                Kind = "Retry",
                Invocation = new AiInvocationDefinition { Kind = AiInvocationKind.Custom }
            }).ToList();
            var concurrency = new Dictionary<string, object?>
            {
                ["retry"] = new AiRetryPolicyDefinition { Policies = policies, MaxRetries = 3 }
            };
            var step = new AiPipelineStepDefinition
            {
                Name = "native",
                StepKey = "native",
                Config = stepScoped ? concurrency : new Dictionary<string, object?>()
            };
            var definition = new AiPipelineDefinition
            {
                Name = "policy-pipeline",
                Version = revision,
                ExecutionMode = AiExecutionMode.Dag,
                ExecutionLanguage = language,
                Config = stepScoped ? new Dictionary<string, object?>() : concurrency,
                Steps = new[] { step }
            };
            var functions = Enumerable.Range(0, copies).Select(index => new AiPublicationFunctionUpload(
                new AiPublicationCallSite(AiPublicationFunctionKind.RetryPolicy, stepScoped ? "native" : null, index),
                language + "-fixed",
                "policy.txt",
                "run",
                new[] { new AiPublicationFileUpload("policy.txt", Encoding.UTF8.GetBytes("policy-" + revision)) },
                Array.Empty<AiPublicationDependencyUpload>())).ToArray();
            return new AiPipelinePublicationUpload(definition, functions);
        }

        private static byte[] Decode(string encoded) => Convert.FromBase64String(
            encoded.Replace('-', '+').Replace('_', '/') + new string('=', (4 - encoded.Length % 4) % 4));
    }
}
