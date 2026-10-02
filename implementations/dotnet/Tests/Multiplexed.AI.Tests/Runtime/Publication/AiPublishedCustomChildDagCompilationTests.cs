using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Execution;

namespace Multiplexed.AI.Tests.Runtime.Publication
{
    /// <summary>
    /// Verifies that immutable publication can address custom declarations inside inline Child DAG definitions
    /// without changing the existing Child DAG scheduler, durable invocation journal, or worker authority.
    /// </summary>
    public sealed class AiPublishedCustomChildDagCompilationTests
    {
        [Fact]
        public async Task Custom_Child_Step_Is_Compiled_Into_The_Immutable_Publication()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var child = CustomDag("child", "typescript", PublicationTestSupport.Step("work"));
            var root = Root(ChildStep("invoke-child", child));
            var upload = new AiPipelinePublicationUpload(root, new[]
            {
                PublicationTestSupport.Function(Site(AiPublicationFunctionKind.Step, "work", "/invoke-child"), "typescript")
            });

            var publication = await fixture.PublishAsync(upload);

            Assert.Equal(2, publication.Manifest.SchemaVersion);
            var function = Assert.Single(publication.Manifest.Functions);
            Assert.Equal("/invoke-child", function.Site.DefinitionPath);
            Assert.Equal("work", function.Site.StepName);
            Assert.Equal("typescript", function.ExecutionLanguage);
            Assert.StartsWith("impl-", function.ImplementationRef);
            Assert.Contains("\"childDagDefinition\"", fixture.MemoryPayloads.Documents[publication.Manifest.Definition.Key], StringComparison.Ordinal);
            Assert.Contains(function.ImplementationRef, fixture.MemoryPayloads.Documents[publication.Manifest.Definition.Key], StringComparison.Ordinal);
            await fixture.ReadAsync(publication.PublicationRef);
        }

        [Fact]
        public async Task Child_Default_Language_Is_Resolved_Without_Inheriting_The_Parent_Default()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var child = CustomDag("child", "typescript", PublicationTestSupport.Step("work"));
            var root = Root(ChildStep("invoke-child", child));

            var publication = await fixture.PublishAsync(new AiPipelinePublicationUpload(root, new[]
            {
                PublicationTestSupport.Function(Site(AiPublicationFunctionKind.Step, "work", "/invoke-child"), "typescript")
            }));

            Assert.Equal("typescript", Assert.Single(publication.Manifest.Functions).ExecutionLanguage);
        }

        [Fact]
        public async Task Child_Local_Language_Override_Wins_Over_Child_And_Parent_Defaults()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var child = CustomDag("child", "typescript", PublicationTestSupport.Step("work", language: "dotnet"));
            var root = Root(ChildStep("invoke-child", child));

            var publication = await fixture.PublishAsync(new AiPipelinePublicationUpload(root, new[]
            {
                PublicationTestSupport.Function(Site(AiPublicationFunctionKind.Step, "work", "/invoke-child"), "dotnet")
            }));

            Assert.Equal("dotnet", Assert.Single(publication.Manifest.Functions).ExecutionLanguage);
        }

        [Fact]
        public async Task Parent_And_Child_Steps_With_The_Same_Name_Have_Distinct_Call_Sites()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var rootCustom = PublicationTestSupport.Step("work", order: 0, language: "python");
            var child = CustomDag("child", "typescript", PublicationTestSupport.Step("work"));
            var root = Root(
                rootCustom,
                ChildStep("invoke-child", child, order: 1, dependsOn: new[] { "work" }));

            var publication = await fixture.PublishAsync(new AiPipelinePublicationUpload(root, new[]
            {
                PublicationTestSupport.Function(new AiPublicationCallSite(AiPublicationFunctionKind.Step, "work"), "python"),
                PublicationTestSupport.Function(Site(AiPublicationFunctionKind.Step, "work", "/invoke-child"), "typescript")
            }));

            Assert.Equal(2, publication.Manifest.Functions.Count);
            Assert.Contains(publication.Manifest.Functions, function =>
                function.Site.StepName == "work" && function.Site.DefinitionPath is null && function.ExecutionLanguage == "python");
            Assert.Contains(publication.Manifest.Functions, function =>
                function.Site.StepName == "work" && function.Site.DefinitionPath == "/invoke-child" && function.ExecutionLanguage == "typescript");

            var run = await fixture.CreateAsync(publication);
            var rootTarget = await fixture.TargetAsync(run, "work");
            Assert.NotNull(rootTarget);
            Assert.Equal("python", rootTarget!.ExecutionLanguage);
            Assert.Equal(publication.Manifest.Functions.Single(function => function.Site.DefinitionPath is null).ImplementationRef,
                rootTarget.ImplementationRef);
        }

        [Fact]
        public async Task Recursive_Child_Declaration_Path_Identifies_The_Exact_Grandchild_Site()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var grandchild = CustomDag("grandchild", "dotnet", PublicationTestSupport.Step("leaf"));
            var child = CustomDag("child", "typescript", ChildStep("invoke-grandchild", grandchild));
            var root = Root(ChildStep("invoke-child", child));

            var publication = await fixture.PublishAsync(new AiPipelinePublicationUpload(root, new[]
            {
                PublicationTestSupport.Function(
                    Site(AiPublicationFunctionKind.Step, "leaf", "/invoke-child/invoke-grandchild"),
                    "dotnet")
            }));

            var function = Assert.Single(publication.Manifest.Functions);
            Assert.Equal("/invoke-child/invoke-grandchild", function.Site.DefinitionPath);
            Assert.Equal("dotnet", function.ExecutionLanguage);
            await fixture.ReadAsync(publication.PublicationRef);
        }

        [Fact]
        public async Task Definition_Path_Uses_Canonical_Json_Pointer_Escaping()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var child = CustomDag("child", "typescript", PublicationTestSupport.Step("work"));
            var root = Root(ChildStep("child/root~one", child));

            var publication = await fixture.PublishAsync(new AiPipelinePublicationUpload(root, new[]
            {
                PublicationTestSupport.Function(
                    Site(AiPublicationFunctionKind.Step, "work", "/child~1root~0one"),
                    "typescript")
            }));

            Assert.Equal("/child~1root~0one", Assert.Single(publication.Manifest.Functions).Site.DefinitionPath);
            await fixture.ReadAsync(publication.PublicationRef);
        }

        [Fact]
        public async Task Nested_Concurrency_Policy_Uses_The_Child_Path_And_Child_Default_Language()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var policy = new AiConfiguredPolicyDefinition
            {
                Name = "child-limit",
                Kind = "Concurrency",
                Invocation = new AiInvocationDefinition { Kind = AiInvocationKind.Custom }
            };
            var config = new Dictionary<string, object?>
            {
                ["concurrency"] = new AiConcurrencyDefinition { Policies = new List<AiConfiguredPolicyDefinition> { policy } }
            };
            var child = CustomDag("child", "typescript",
                new AiPipelineStepDefinition { Name = "native", StepKey = "native" }, config);
            var root = Root(ChildStep("invoke-child", child));

            var publication = await fixture.PublishAsync(new AiPipelinePublicationUpload(root, new[]
            {
                PublicationTestSupport.Function(
                    Site(AiPublicationFunctionKind.ConcurrencyPolicy, null, "/invoke-child", policyIndex: 0),
                    "typescript")
            }));

            var function = Assert.Single(publication.Manifest.Functions);
            Assert.Equal(AiPublicationFunctionKind.ConcurrencyPolicy, function.Site.Kind);
            Assert.Null(function.Site.StepName);
            Assert.Equal(0, function.Site.PolicyIndex);
            Assert.Equal("/invoke-child", function.Site.DefinitionPath);
            Assert.Equal("typescript", function.ExecutionLanguage);
        }

        [Fact]
        public async Task Missing_Nested_Code_Is_Rejected_Before_Any_Publication_Write()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var child = CustomDag("child", "typescript", PublicationTestSupport.Step("work"));
            var root = Root(ChildStep("invoke-child", child));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.PublishAsync(new AiPipelinePublicationUpload(root, Array.Empty<AiPublicationFunctionUpload>())));

            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Wrong_Nested_Path_Is_Rejected_Before_Any_Publication_Write()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var child = CustomDag("child", "typescript", PublicationTestSupport.Step("work"));
            var root = Root(ChildStep("invoke-child", child));

            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(new AiPipelinePublicationUpload(root, new[]
            {
                PublicationTestSupport.Function(Site(AiPublicationFunctionKind.Step, "work", "/other-child"), "typescript")
            })));

            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Theory]
        [InlineData("child")]
        [InlineData("/child~2")]
        [InlineData("/child/")]
        [InlineData("//child")]
        public async Task Noncanonical_Definition_Path_Is_Rejected_Before_Persistence(string path)
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var child = CustomDag("child", "typescript", PublicationTestSupport.Step("work"));
            var root = Root(ChildStep("invoke-child", child));

            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(new AiPipelinePublicationUpload(root, new[]
            {
                PublicationTestSupport.Function(Site(AiPublicationFunctionKind.Step, "work", path), "typescript")
            })));

            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Root_Call_Site_Retains_The_Historical_Manifest_Shape()
        {
            using var fixture = new PublicationTestSupport.Fixture();

            var publication = await fixture.PublishAsync();
            var manifestJson = fixture.MemoryPayloads.Documents.Single(document =>
                document.Key.EndsWith("/manifest/" + publication.PublicationSha256, StringComparison.Ordinal)).Value;

            Assert.Contains("\"Site\":{\"Kind\":0,\"PolicyIndex\":null,\"StepName\":\"first\"}", manifestJson, StringComparison.Ordinal);
            Assert.DoesNotContain("DefinitionPath", manifestJson, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Root_Only_Publication_Remains_Manifest_Schema_One()
        {
            using var fixture = new PublicationTestSupport.Fixture();

            var publication = await fixture.PublishAsync();

            Assert.Equal(1, publication.Manifest.SchemaVersion);
            Assert.All(publication.Manifest.Functions, function => Assert.Null(function.Site.DefinitionPath));
            await fixture.ReadAsync(publication.PublicationRef);
        }

        private static AiPublicationCallSite Site(
            AiPublicationFunctionKind kind,
            string? stepName,
            string definitionPath,
            int? policyIndex = null) =>
            new(kind, stepName, policyIndex) { DefinitionPath = definitionPath };

        private static AiPipelineDefinition Root(params AiPipelineStepDefinition[] steps) =>
            Root(steps, "python");

        private static AiPipelineDefinition Root(
            IReadOnlyCollection<AiPipelineStepDefinition> steps,
            string language) => new()
        {
            Name = "published-root",
            Version = "1",
            ExecutionLanguage = language,
            ExecutionMode = AiExecutionMode.Dag,
            Steps = steps
        };

        private static AiPipelineDefinition CustomDag(
            string name,
            string language,
            AiPipelineStepDefinition step,
            IReadOnlyDictionary<string, object?>? config = null) => new()
        {
            Name = name,
            Version = "1",
            ExecutionLanguage = language,
            ExecutionMode = AiExecutionMode.Dag,
            Steps = new[] { step },
            Config = config ?? new Dictionary<string, object?>()
        };

        private static AiPipelineStepDefinition ChildStep(
            string name,
            AiPipelineDefinition child,
            int order = 0,
            IReadOnlyCollection<string>? dependsOn = null) => new()
        {
            Name = name,
            StepKey = ExecuteChildDagStep.StepKey,
            Order = order,
            DependsOn = dependsOn ?? Array.Empty<string>(),
            Config = new Dictionary<string, object?>
            {
                [ExecuteChildDagStep.ChildDagIdConfigKey] = child.Name,
                [ExecuteChildDagStep.ChildDagVersionConfigKey] = child.Version,
                [ExecuteChildDagStep.LogicalInvocationKeyConfigKey] = name,
                [ExecuteChildDagStep.ChildDagDefinitionConfigKey] = child
            }
        };
    }
}
