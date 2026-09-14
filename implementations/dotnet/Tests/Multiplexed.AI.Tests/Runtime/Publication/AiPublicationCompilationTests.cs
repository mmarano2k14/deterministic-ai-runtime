using System.Text;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Publication
{
    /// <summary>Content identity, generated bindings, caller isolation, and bounded portable files.</summary>
    public sealed class AiPublicationCompilationTests
    {
        [Theory]
        [InlineData("python")]
        [InlineData("typescript")]
        [InlineData("dotnet")]
        public async Task Generates_Immutable_Code_And_Environment_References(string language)
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var upload = PublicationTestSupport.Upload(language: language, secondLanguage: null);
            var publication = await fixture.PublishAsync(upload);
            Assert.StartsWith("pub-", publication.PublicationRef);
            Assert.Equal(publication.PublicationRef[4..], publication.PublicationSha256);
            Assert.All(publication.Manifest.Functions, function =>
            {
                Assert.Equal(language, function.ExecutionLanguage);
                Assert.Equal("impl-" + function.Implementation.Sha256, function.ImplementationRef);
                Assert.Contains("/environment/", function.Environment.Key);
            });
            Assert.All(upload.Definition.Steps, s => Assert.Null(s.Invocation!.ImplementationRef));
            Assert.Equal(0, fixture.Registry.Native.Calls);
            Assert.Empty(await fixture.JournalStore.ListDispatchCandidatesAsync(PublicationTestSupport.Scope, language, fixture.Clock.GetUtcNow(), 100));
            Assert.Equal(publication.PublicationRef, (await fixture.ReadAsync(publication.PublicationRef)).PublicationRef);
        }

        [Fact]
        public async Task Identical_Publications_And_Reordered_Code_Attachments_Converge()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var upload = PublicationTestSupport.Upload(); var first = await fixture.PublishAsync(upload);
            var count = fixture.MemoryPayloads.Documents.Count;
            var second = await fixture.PublishAsync(upload with { Functions = upload.Functions.Reverse().ToArray() });
            Assert.Equal(first.PublicationRef, second.PublicationRef); Assert.Equal(count, fixture.MemoryPayloads.Documents.Count);
        }

        [Theory]
        [InlineData("source")]
        [InlineData("dependency")]
        [InlineData("version")]
        [InlineData("symbol")]
        public async Task Changed_Code_Or_Dependency_Content_Changes_The_Publication(string change)
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var upload = PublicationTestSupport.Upload(); var before = await fixture.PublishAsync(upload);
            var function = upload.Functions[0];
            function = change switch
            {
                "source" => function with { Sources = new[] { new AiPublicationFileUpload("main.txt", Encoding.UTF8.GetBytes("changed")) } },
                "dependency" => function with { Dependencies = new[] { function.Dependencies[0] with { Files = new[] {
                    new AiPublicationFileUpload("dependency.dat", Encoding.UTF8.GetBytes("changed")) } } } },
                "version" => function with { Dependencies = new[] { function.Dependencies[0] with { Version = "2.0.0" } } },
                _ => function with { EntryPointSymbol = "other" }
            };
            var after = await fixture.PublishAsync(upload with { Functions = new[] { function, upload.Functions[1] } });
            Assert.NotEqual(before.PublicationRef, after.PublicationRef);
            Assert.NotEqual(before.Manifest.Definition.Sha256, after.Manifest.Definition.Sha256);
            Assert.Equal(before.PublicationRef, (await fixture.ReadAsync(before.PublicationRef)).PublicationRef);
        }

        [Theory]
        [InlineData("Example.Functions::Run")]
        [InlineData("Example_1.Functions2::Run_2")]
        public async Task DotNet_Qualified_Entry_Point_Symbol_Is_Preserved(string symbol)
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var upload = PublicationTestSupport.Upload(language: "dotnet", secondLanguage: null);
            var functions = upload.Functions.Select(function => function with { EntryPointSymbol = symbol }).ToArray();

            var publication = await fixture.PublishAsync(upload with { Functions = functions });

            Assert.StartsWith("pub-", publication.PublicationRef);
            Assert.Contains(fixture.MemoryPayloads.Documents.Values, json => json.Contains(symbol, StringComparison.Ordinal));
            await fixture.ReadAsync(publication.PublicationRef);
        }

        [Theory]
        [InlineData("Example.Functions:Run")]
        [InlineData("Example.Functions:::Run")]
        [InlineData("::Run")]
        [InlineData("Example.Functions::")]
        [InlineData("Example..Functions::Run")]
        [InlineData("Example.Functions::Run::Again")]
        public async Task Malformed_Clr_Style_Entry_Point_Symbol_Is_Rejected_Before_Persistence(string symbol)
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var upload = PublicationTestSupport.Upload(language: "dotnet", secondLanguage: null);
            var functions = upload.Functions.Select(function => function with { EntryPointSymbol = symbol }).ToArray();

            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(upload with { Functions = functions }));

            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Caller_Mutations_After_The_First_Storage_Write_Cannot_Change_Frozen_Bytes()
        {
            using var fixture = new PublicationTestSupport.Fixture(); var upload = PublicationTestSupport.Upload();
            var expected = PublicationTestSupport.Hash(Encoding.UTF8.GetString(upload.Functions[0].Sources[0].Content));
            fixture.MemoryPayloads.BeforeWrite = (_, _) =>
            {
                Array.Fill(upload.Functions[0].Sources[0].Content, (byte)'x'); return Task.CompletedTask;
            };
            var publication = await fixture.PublishAsync(upload);
            Assert.Contains(fixture.MemoryPayloads.Documents.Values, value => value.Contains(expected, StringComparison.Ordinal));
            await fixture.ReadAsync(publication.PublicationRef);
        }

        [Theory]
        [InlineData("../main.py")]
        [InlineData("/main.py")]
        [InlineData("a/../main.py")]
        [InlineData("a//main.py")]
        [InlineData("a\\main.py")]
        [InlineData("C:/main.py")]
        [InlineData("CON.txt")]
        [InlineData("a/LPT1.py")]
        [InlineData("a./main.py")]
        public async Task Unsafe_File_Paths_Are_Rejected_Before_Persistence(string path)
        {
            using var fixture = new PublicationTestSupport.Fixture(); var upload = PublicationTestSupport.Upload();
            var function = upload.Functions[0] with { Sources = new[] { new AiPublicationFileUpload(path, new byte[] { 1 }) } };
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(upload with { Functions = new[] { function, upload.Functions[1] } }));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Theory]
        [InlineData("latest")]
        [InlineData("*")]
        [InlineData("^1.0")]
        [InlineData("~1.0")]
        [InlineData(">=1.0")]
        [InlineData("[1.0,2.0)")]
        [InlineData("https://registry/package")]
        public async Task Dependency_Ranges_And_Latest_Are_Not_Resolved(string version)
        {
            using var fixture = new PublicationTestSupport.Fixture(); var upload = PublicationTestSupport.Upload();
            var function = upload.Functions[0] with { Dependencies = new[] { upload.Functions[0].Dependencies[0] with { Version = version } } };
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(upload with { Functions = new[] { function, upload.Functions[1] } }));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Case_Colliding_Files_Are_Rejected()
        {
            using var fixture = new PublicationTestSupport.Fixture(); var upload = PublicationTestSupport.Upload();
            var function = upload.Functions[0] with { Sources = new[] {
                new AiPublicationFileUpload("main.txt", new byte[] { 1 }), new AiPublicationFileUpload("MAIN.txt", new byte[] { 2 }) } };
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(upload with { Functions = new[] { function, upload.Functions[1] } }));
        }

        [Theory]
        [InlineData("file")]
        [InlineData("total")]
        [InlineData("count")]
        [InlineData("functions")]
        public async Task Host_Size_And_Count_Bounds_Are_Enforced(string bound)
        {
            var defaults = PublicationTestSupport.Options;
            var options = new AiPublicationOptions(defaults.Publish, defaults.Read, defaults.Execute,
                maxFileBytes: bound == "file" ? 1 : 1024, maxTotalBytes: bound == "total" ? 1024 : 8192,
                maxFiles: bound == "count" ? 1 : 512, maxFunctions: bound == "functions" ? 1 : 128);
            using var fixture = new PublicationTestSupport.Fixture(options); var upload = PublicationTestSupport.Upload();
            if (bound == "total") upload = upload with { Functions = upload.Functions.Select(f => f with {
                Sources = new[] { new AiPublicationFileUpload("main.txt", new byte[900]) } }).ToArray() };
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(upload));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Theory]
        [InlineData("missing")]
        [InlineData("extra")]
        [InlineData("duplicate")]
        [InlineData("missing-entry")]
        public async Task Declaration_Coverage_Must_Be_Exact(string failure)
        {
            using var fixture = new PublicationTestSupport.Fixture(); var upload = PublicationTestSupport.Upload();
            var functions = failure switch
            {
                "missing" => upload.Functions.Take(1).ToArray(),
                "extra" => upload.Functions.Append(PublicationTestSupport.Function(new(AiPublicationFunctionKind.Step, "unknown"))).ToArray(),
                "duplicate" => upload.Functions.Append(upload.Functions[0]).ToArray(),
                _ => new[] { upload.Functions[0] with { EntryPointPath = "missing.py" }, upload.Functions[1] }
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(upload with { Functions = functions }));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Binary_Artifacts_Use_Bounded_Url_Safe_Encoding_Without_Losing_Bytes()
        {
            using var fixture = new PublicationTestSupport.Fixture(); var upload = PublicationTestSupport.Upload();
            var bytes = Enumerable.Range(0, 262143).Select(i => new byte[] { 0xfb, 0xef, 0xbe }[i % 3]).ToArray();
            var function = upload.Functions[0] with { Sources = new[] { new AiPublicationFileUpload("main.txt", bytes) } };
            var publication = await fixture.PublishAsync(upload with { Functions = new[] { function, upload.Functions[1] } });
            await fixture.ReadAsync(publication.PublicationRef);
            Assert.Contains(fixture.MemoryPayloads.Documents.Values, json => json.Contains("Base64Url", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Preassigned_Handler_References_Are_Refused()
        {
            using var fixture = new PublicationTestSupport.Fixture(); var upload = PublicationTestSupport.Upload();
            var step = upload.Definition.Steps.First();
            var replacement = new Multiplexed.Abstractions.AI.Pipeline.AiPipelineStepDefinition {
                Name = step.Name, StepKey = step.StepKey, Invocation = new AiInvocationDefinition { Kind = AiInvocationKind.Custom, ImplementationRef = "manual-handler" } };
            upload = upload with { Definition = PublicationTestSupport.Copy(upload.Definition, new[] { replacement, upload.Definition.Steps.Last() }) };
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(upload));
        }

        [Fact]
        public async Task Sequential_Publication_Does_Not_Receive_An_Implicit_Dag_Mode()
        {
            using var fixture = new PublicationTestSupport.Fixture(); var upload = PublicationTestSupport.Upload();
            await Assert.ThrowsAsync<NotSupportedException>(() => fixture.PublishAsync(upload with {
                Definition = PublicationTestSupport.Copy(upload.Definition, mode: AiExecutionMode.Sequential) }));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }
    }
}
