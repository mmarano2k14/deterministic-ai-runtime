using Multiplexed.Abstractions.AI.Invocation.Durable;

namespace Multiplexed.AI.Tests.Runtime.Publication
{
    /// <summary>Existing TRN decisions, stable ownership checks and exact storage partitions.</summary>
    public sealed class AiPublicationAuthorizationTests
    {
        [Theory]
        [InlineData("publish")]
        [InlineData("read")]
        [InlineData("execute")]
        public async Task Missing_Existing_Rbac_Capability_Blocks_The_Operation(string operation)
        {
            using var fixture = new PublicationTestSupport.Fixture(); var publication = await fixture.PublishAsync();
            var identity = PublicationTestSupport.Identity(actions: Array.Empty<string>());
            var reads = fixture.MemoryPayloads.Reads; var writes = fixture.MemoryPayloads.Writes.Count;
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.AsAsync(async () =>
            {
                if (operation == "publish") await fixture.Publisher.PublishAsync(PublicationTestSupport.Scope, PublicationTestSupport.Upload());
                else if (operation == "read") await fixture.Publisher.ReadAsync(PublicationTestSupport.Scope, publication.PublicationRef);
                else await fixture.Runs.CreateAsync(PublicationTestSupport.Scope, "denied-run", publication.PublicationRef, "{}");
                return true;
            }, identity));
            Assert.Equal(reads, fixture.MemoryPayloads.Reads); Assert.Equal(writes, fixture.MemoryPayloads.Writes.Count);
        }

        [Theory]
        [InlineData("tenant")]
        [InlineData("group")]
        [InlineData("control-plane")]
        public async Task Spoofed_Trusted_Scope_Is_Rejected_Before_Storage(string field)
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var scope = field switch
            {
                "tenant" => PublicationTestSupport.Scope with { TenantId = "tenant-b" },
                "group" => PublicationTestSupport.Scope with { TenantGroupId = "group-b" },
                _ => PublicationTestSupport.Scope with { ControlPlaneId = "other-plane" }
            };
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.AsAsync(() => fixture.Publisher.PublishAsync(scope, PublicationTestSupport.Upload())));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Theory]
        [InlineData("tenant")]
        [InlineData("group")]
        [InlineData("project")]
        [InlineData("namespace")]
        public async Task Identical_Code_In_Different_Trusted_Partitions_Does_Not_Share_References(string partition)
        {
            using var fixture = new PublicationTestSupport.Fixture(); var original = await fixture.PublishAsync();
            var scope = PublicationTestSupport.Scope;
            if (partition == "tenant") scope = scope with { TenantId = "tenant-b" };
            if (partition == "group") scope = scope with { TenantGroupId = "group-b" };
            var identity = PublicationTestSupport.Identity(tenant: scope.TenantId, group: scope.TenantGroupId,
                project: partition == "project" ? "other" : "tests", ns: partition == "namespace" ? "other" : "default");
            var other = await fixture.AsAsync(() => fixture.Publisher.PublishAsync(scope, PublicationTestSupport.Upload()), identity);
            Assert.NotEqual(original.PublicationRef, other.PublicationRef);
            Assert.NotEqual(original.Manifest.Definition.Key, other.Manifest.Definition.Key);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.AsAsync(() => fixture.Publisher.ReadAsync(scope, original.PublicationRef), identity));
        }

        [Fact]
        public async Task An_Existing_Authorization_Cache_Cannot_Grant_A_Later_Request()
        {
            using var fixture = new PublicationTestSupport.Fixture(); await fixture.PublishAsync();
            fixture.Live.Namespaces[0].Trns.Clear();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.PublishAsync());
        }

        [Fact]
        public async Task Missing_Context_Is_Not_Reconstructed_From_The_Request()
        {
            using var fixture = new PublicationTestSupport.Fixture(); fixture.Accessor.Clear();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Publisher.PublishAsync(PublicationTestSupport.Scope, PublicationTestSupport.Upload()));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Context_Change_During_Persistence_Cannot_Publish_A_Manifest()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            fixture.MemoryPayloads.AfterWrite = (_, _) => { fixture.Live.TenantId = "tenant-b"; return Task.CompletedTask; };
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.PublishAsync());
            Assert.DoesNotContain(fixture.MemoryPayloads.Documents.Keys, k => k.Contains("/manifest/", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Cancellation_Does_Not_Produce_A_Published_Manifest(bool cancelBeforeCall)
        {
            using var fixture = new PublicationTestSupport.Fixture(); using var cancellation = new CancellationTokenSource();
            if (cancelBeforeCall) cancellation.Cancel();
            else fixture.MemoryPayloads.BeforeWrite = (_, _) => { cancellation.Cancel(); return Task.CompletedTask; };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.AsAsync(() =>
                fixture.Publisher.PublishAsync(PublicationTestSupport.Scope, PublicationTestSupport.Upload(), cancellation.Token)));
            Assert.DoesNotContain(fixture.MemoryPayloads.Documents.Keys, k => k.Contains("/manifest/", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Target_Resolution_Requires_The_Original_Execution_Owner()
        {
            using var fixture = new PublicationTestSupport.Fixture(); var publication = await fixture.PublishAsync(); var run = await fixture.CreateAsync(publication);
            var function = publication.Manifest.Functions.Single(f => f.Site.StepName == "second");
            var request = new AiDurableInvocationTargetRequest(PublicationTestSupport.Scope, run.ExecutionId,
                publication.Manifest.Definition.Sha256, publication.Manifest.PipelineName, publication.Manifest.PipelineVersion,
                "second", "code-second", function.ImplementationRef, function.ExecutionLanguage);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.AsAsync(() => fixture.Targets.ResolveAsync(request),
                PublicationTestSupport.Identity(user: "other-user")));
        }
    }
}
