using System.Text.Json;
using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Execution;

namespace Multiplexed.AI.Tests.Runtime.Publication
{
    /// <summary>Preserves native execution and policy declaration scopes without moving policy checkpoints.</summary>
    public sealed class AiPublicationPolicyAndCompatibilityTests
    {
        private static AiConfiguredPolicyDefinition Policy(string name, string? language = null) => new()
        {
            Name = name, Kind = "Concurrency", ExecutionLanguage = language,
            Invocation = new AiInvocationDefinition { Kind = AiInvocationKind.Custom }
        };
        private static IReadOnlyDictionary<string, object?> Config(params AiConfiguredPolicyDefinition[] policies) =>
            new Dictionary<string, object?> { ["concurrency"] = new AiConcurrencyDefinition { Policies = policies.ToList() } };

        [Theory]
        [InlineData(null, "typescript")]
        [InlineData("dotnet", "dotnet")]
        public async Task Pipeline_And_Local_Policies_Retain_Their_Original_Language_Scope(string? localOverride, string expected)
        {
            using var fixture = new PublicationTestSupport.Fixture(); var upload = PublicationTestSupport.Upload();
            var definition = PublicationTestSupport.Copy(upload.Definition, new[] { upload.Definition.Steps.First(),
                PublicationTestSupport.Step("second", 1, "typescript", Config(Policy("local-limit", localOverride))) }, Config(Policy("global-limit")));
            var functions = upload.Functions.Concat(new[] {
                PublicationTestSupport.Function(new(AiPublicationFunctionKind.ConcurrencyPolicy, null, 0), "python"),
                PublicationTestSupport.Function(new(AiPublicationFunctionKind.ConcurrencyPolicy, "second", 0), expected) }).ToArray();
            var publication = await fixture.PublishAsync(new(definition, functions));
            Assert.Equal("python", publication.Manifest.Functions.Single(f => f.Site.Kind == AiPublicationFunctionKind.ConcurrencyPolicy && f.Site.StepName is null).ExecutionLanguage);
            Assert.Equal(expected, publication.Manifest.Functions.Single(f => f.Site.Kind == AiPublicationFunctionKind.ConcurrencyPolicy && f.Site.StepName == "second").ExecutionLanguage);
            await fixture.ReadAsync(publication.PublicationRef);
        }

        [Fact]
        public async Task Policy_Index_Distinguishes_Repeated_Names_Without_Reordering_Them()
        {
            using var fixture = new PublicationTestSupport.Fixture(); var upload = PublicationTestSupport.Upload();
            var config = Config(Policy("same-name"), Policy("same-name", "dotnet"));
            var functions = upload.Functions.Concat(new[] {
                PublicationTestSupport.Function(new(AiPublicationFunctionKind.ConcurrencyPolicy, null, 0)),
                PublicationTestSupport.Function(new(AiPublicationFunctionKind.ConcurrencyPolicy, null, 1), "dotnet") }).ToArray();
            var publication = await fixture.PublishAsync(upload with { Definition = PublicationTestSupport.Copy(upload.Definition, config: config), Functions = functions });
            var policies = publication.Manifest.Functions.Where(f => f.Site.Kind == AiPublicationFunctionKind.ConcurrencyPolicy).OrderBy(f => f.Site.PolicyIndex).ToArray();
            Assert.Equal(new[] { "python", "dotnet" }, policies.Select(f => f.ExecutionLanguage));
        }

        [Theory]
        [InlineData("validation")]
        [InlineData("retry")]
        [InlineData("retention")]
        public async Task Unsupported_Custom_Policy_Checkpoints_Are_Not_Silently_Published(string family)
        {
            using var fixture = new PublicationTestSupport.Fixture(); var upload = PublicationTestSupport.Upload();
            var config = new Dictionary<string, object?> { [family] = new { policies = new[] { Policy("custom") } } };
            await Assert.ThrowsAsync<NotSupportedException>(() => fixture.PublishAsync(upload with { Definition = PublicationTestSupport.Copy(upload.Definition, config: config) }));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Native_Step_And_Absent_Retry_Block_Remain_Native_And_Absent()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var definition = PublicationTestSupport.Copy(PublicationTestSupport.Definition(), new[] { new AiPipelineStepDefinition { Name = "native", StepKey = "native" } });
            var publication = await fixture.PublishAsync(new(definition, Array.Empty<AiPublicationFunctionUpload>()));
            Assert.Empty(publication.Manifest.Functions);
            var json = fixture.MemoryPayloads.Documents[publication.Manifest.Definition.Key];
            var frozen = JsonSerializer.Deserialize<AiPipelineDefinition>(json)!;
            var plan = await fixture.Resolver.ResolveAsync(frozen);
            Assert.Same(fixture.Registry.Native, Assert.Single(plan.Steps).Step);
            Assert.Null(Assert.Single(frozen.Steps).Execution);
            Assert.Equal(0, fixture.Registry.Native.Calls);
        }

        [Fact]
        public async Task Native_Concurrency_Policies_Do_Not_Require_Code_Uploads()
        {
            using var fixture = new PublicationTestSupport.Fixture(); var upload = PublicationTestSupport.Upload();
            var publication = await fixture.PublishAsync(upload with { Definition = PublicationTestSupport.Copy(upload.Definition,
                config: Config(new AiConfiguredPolicyDefinition { Name = "native-guard" })) });
            Assert.Equal(2, publication.Manifest.Functions.Count);
            Assert.All(publication.Manifest.Functions, f => Assert.Equal(AiPublicationFunctionKind.Step, f.Site.Kind));
        }

        [Fact]
        public async Task Name_Only_Child_Definitions_Are_Not_Misrepresented_As_A_Pinned_Closure()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var step = new AiPipelineStepDefinition { Name = "child", StepKey = ExecuteChildDagStep.StepKey,
                Config = new Dictionary<string, object?> { ["childDagId"] = "child", ["childDagVersion"] = "1", ["logicalInvocationKey"] = "child" } };
            var definition = PublicationTestSupport.Copy(PublicationTestSupport.Definition(), new[] { step });
            await Assert.ThrowsAsync<NotSupportedException>(() => fixture.PublishAsync(new(definition, Array.Empty<AiPublicationFunctionUpload>())));
        }

        [Fact]
        public async Task Exact_Inline_Native_Child_Definition_Is_Retained()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var child = new AiPipelineDefinition { Name = "child", Version = "1", ExecutionMode = AiExecutionMode.Dag,
                Steps = new[] { new AiPipelineStepDefinition { Name = "native", StepKey = "native" } } };
            var step = new AiPipelineStepDefinition { Name = "child", StepKey = ExecuteChildDagStep.StepKey,
                Config = new Dictionary<string, object?> { ["childDagId"] = "child", ["childDagVersion"] = "1",
                    ["logicalInvocationKey"] = "child", ["childDagDefinition"] = child } };
            var definition = PublicationTestSupport.Copy(PublicationTestSupport.Definition(), new[] { step });
            var publication = await fixture.PublishAsync(new(definition, Array.Empty<AiPublicationFunctionUpload>()));
            Assert.Contains("childDagDefinition", fixture.MemoryPayloads.Documents[publication.Manifest.Definition.Key]);
            await fixture.ReadAsync(publication.PublicationRef);
        }

        [Fact]
        public async Task Custom_Child_Code_Without_Its_Own_Run_Pin_Is_Explicitly_Unsupported()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var child = PublicationTestSupport.Definition();
            var step = new AiPipelineStepDefinition { Name = "child", StepKey = ExecuteChildDagStep.StepKey,
                Config = new Dictionary<string, object?> { ["childDagId"] = child.Name, ["childDagVersion"] = child.Version,
                    ["logicalInvocationKey"] = "child", ["childDagDefinition"] = child } };
            var definition = PublicationTestSupport.Copy(PublicationTestSupport.Definition(), new[] { step });
            await Assert.ThrowsAsync<NotSupportedException>(() => fixture.PublishAsync(new(definition, Array.Empty<AiPublicationFunctionUpload>())));
        }

        [Fact]
        public async Task Existing_Graph_Validation_Runs_Before_Any_Publication_Write()
        {
            using var fixture = new PublicationTestSupport.Fixture(); var upload = PublicationTestSupport.Upload();
            var first = new AiPipelineStepDefinition { Name = "first", StepKey = "code-first", DependsOn = new[] { "second" },
                Invocation = new AiInvocationDefinition { Kind = AiInvocationKind.Custom } };
            var definition = PublicationTestSupport.Copy(upload.Definition, new[] { first, upload.Definition.Steps.Last() });
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(upload with { Definition = definition }));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }
    }
}
