using System.Text.Json;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.ExecutionRequirements
{
    /// <summary>Schema compatibility, immutable identity, catalog fencing and server-side materialization.</summary>
    public sealed class AiExecutionDescriptorPublicationTests
    {
        [Theory]
        [InlineData("python")]
        [InlineData("typescript")]
        [InlineData("dotnet")]
        public async Task Versioned_Requirements_Are_Pinned_And_Materialized_Without_Worker_Wire_Changes(string language)
        {
            using var fixture = new ExecutionRequirementsTestSupport.Fixture();
            var publication = await fixture.PublishAsync(PublicationTestSupport.Upload(language: language, secondLanguage: language));
            var run = await fixture.CreateAsync(publication);
            var invocation = await fixture.PrepareAsync(run);
            var code = await fixture.CodeAsync(invocation);
            var snapshot = fixture.Snapshot(publication);
            Assert.Equal(2, snapshot.SchemaVersion);
            Assert.Equal(fixture.Catalog.FindExecutionDescriptor(language + "-fixed"), snapshot.ExecutionDescriptor);
            Assert.Equal(snapshot.ExecutionDescriptor, code.ExecutionDescriptor);
            Assert.Equal("env-" + publication.Manifest.Functions.Single(f => f.Site.StepName == "first").Environment.Sha256,
                invocation.Definition.Target.EnvironmentRef);
            Assert.Equal(invocation.Definition.Target, code.Target);
            Assert.DoesNotContain("ExecutionDescriptor", JsonSerializer.Serialize(code));
            Assert.NotEqual("sha256:" + code.Target.EnvironmentSha256, code.ExecutionDescriptor!.Artifact.Digest);
        }

        [Fact]
        public async Task Legacy_Environment_Documents_Preserve_Their_Preexisting_Bytes_And_References()
        {
            using var historical = new PublicationTestSupport.Fixture();
            var before = await historical.PublishAsync();
            using var compatible = new ExecutionRequirementsTestSupport.Fixture(legacy: true);
            var after = await compatible.PublishAsync();
            Assert.Equal(before.PublicationRef, after.PublicationRef);
            foreach (var document in historical.MemoryPayloads.Documents)
                Assert.Equal(document.Value, compatible.Base.MemoryPayloads.Documents[document.Key]);
            Assert.Null(compatible.Snapshot(after).ExecutionDescriptor);
            Assert.Equal(1, compatible.Snapshot(after).SchemaVersion);
            var code = await compatible.CodeAsync(await compatible.PrepareAsync(await compatible.CreateAsync(after)));
            Assert.Null(code.ExecutionDescriptor);
        }

        [Theory]
        [InlineData("isolation")]
        [InlineData("egress")]
        [InlineData("paths")]
        [InlineData("os")]
        [InlineData("architecture")]
        [InlineData("artifact")]
        public async Task Every_Execution_Requirement_Participates_In_The_Immutable_Environment_Hash(string field)
        {
            using var first = new ExecutionRequirementsTestSupport.Fixture();
            using var changed = new ExecutionRequirementsTestSupport.Fixture();
            var descriptor = changed.Catalog.Descriptors["python-fixed"]!;
            changed.Catalog.Descriptors["python-fixed"] = field switch
            {
                "isolation" => descriptor with { Requirements = descriptor.Requirements with { IsolationTier = AiWorkerIsolationTier.RestrictedProcess } },
                "egress" => descriptor with { Requirements = descriptor.Requirements with { NetworkEgress = AiWorkerNetworkEgress.DenyAll } },
                "paths" => descriptor with { Requirements = descriptor.Requirements with { PathProtection = AiWorkerPathProtection.SealedClosure } },
                "os" => descriptor with { OperatingSystem = descriptor.OperatingSystem == "linux" ? "windows" : "linux" },
                "architecture" => descriptor with { Architecture = descriptor.Architecture == "amd64" ? "arm64" : "amd64" },
                _ => descriptor with { Artifact = new(AiPublicationEnvironmentArtifactKind.OciImage, "sha256:" + new string('e', 64),
                    AiPublicationExecutionDescriptors.OciImageMediaType) }
            };
            var old = await first.PublishAsync(); var current = await changed.PublishAsync();
            Assert.NotEqual(old.PublicationRef, current.PublicationRef);
            Assert.NotEqual(old.Manifest.Functions.Single(f => f.Site.StepName == "first").Environment.Sha256,
                current.Manifest.Functions.Single(f => f.Site.StepName == "first").Environment.Sha256);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Catalog_Downgrade_Or_Mutation_Cannot_Replace_A_Pinned_Descriptor(bool remove)
        {
            using var fixture = new ExecutionRequirementsTestSupport.Fixture(); var published = await fixture.PublishAsync();
            var previous = fixture.Catalog.Descriptors["python-fixed"]!;
            fixture.Catalog.Descriptors["python-fixed"] = remove ? null : previous with
                { Requirements = previous.Requirements with { NetworkEgress = AiWorkerNetworkEgress.DenyAll } };
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync(published.PublicationRef));
        }

        [Fact]
        public async Task Legacy_Run_Is_Not_Retroactively_Upgraded_When_The_Catalog_Changes()
        {
            using var fixture = new ExecutionRequirementsTestSupport.Fixture(legacy: true);
            var published = await fixture.PublishAsync();
            fixture.Catalog.Descriptors["python-fixed"] = ExecutionRequirementsTestSupport.Descriptor(fixture.Catalog.Runtimes["python-fixed"]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync(published.PublicationRef));
        }

        [Fact]
        public async Task Invalid_Descriptor_Does_Not_Write_A_Partial_Publication()
        {
            using var fixture = new ExecutionRequirementsTestSupport.Fixture();
            fixture.Catalog.Descriptors["python-fixed"] = fixture.Catalog.Descriptors["python-fixed"]! with { SchemaVersion = 9 };
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync());
            Assert.Empty(fixture.Base.MemoryPayloads.Documents);
        }

        [Fact]
        public async Task New_Publication_Cannot_Rebind_The_Environment_Of_An_Existing_Run()
        {
            using var fixture = new ExecutionRequirementsTestSupport.Fixture();
            var first = await fixture.PublishAsync(); var run = await fixture.CreateAsync(first);
            var runtime = fixture.Catalog.Runtimes["python-fixed"] with { Reference = "python-revision-two" };
            fixture.Catalog.Runtimes.Add(runtime.Reference, runtime);
            fixture.Catalog.Descriptors.Add(runtime.Reference, ExecutionRequirementsTestSupport.Descriptor(runtime,
                ExecutionRequirementsTestSupport.TrustedRequirements with { NetworkEgress = AiWorkerNetworkEgress.DenyAll }));
            var upload = PublicationTestSupport.Upload("2");
            upload = upload with { Functions = upload.Functions.Select(f => f.Site.StepName == "first"
                ? f with { EnvironmentRef = runtime.Reference } : f).ToArray() };
            var second = await fixture.PublishAsync(upload);
            Assert.NotEqual(first.PublicationRef, second.PublicationRef);
            var invocation = await fixture.PrepareAsync(run); var code = await fixture.CodeAsync(invocation);
            Assert.Equal(first.PublicationRef, code.Target.PublicationRef);
            Assert.Equal(AiWorkerNetworkEgress.HostNetwork, code.ExecutionDescriptor!.Requirements.NetworkEgress);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateAsync(second));
        }
    }
}
