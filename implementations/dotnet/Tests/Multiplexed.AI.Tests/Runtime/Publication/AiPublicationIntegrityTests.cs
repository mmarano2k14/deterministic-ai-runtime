using System.Text.Json;
using Multiplexed.Abstractions.AI.Publication;

namespace Multiplexed.AI.Tests.Runtime.Publication
{
    /// <summary>Immutable payload failures, exact runtime availability and publication write ordering.</summary>
    public sealed class AiPublicationIntegrityTests
    {
        [Theory]
        [InlineData("file")]
        [InlineData("environment")]
        [InlineData("implementation")]
        [InlineData("definition")]
        [InlineData("manifest")]
        public async Task Missing_Required_Document_Is_An_Error_Not_A_Latest_Fallback(string kind)
        {
            using var fixture = new PublicationTestSupport.Fixture(); var publication = await fixture.PublishAsync();
            var key = fixture.MemoryPayloads.Documents.Keys.First(k => k.Contains("/" + kind + "/", StringComparison.Ordinal));
            fixture.MemoryPayloads.Documents.TryRemove(key, out _);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync(publication.PublicationRef));
            Assert.Equal(0, fixture.LatestLookups);
        }

        [Theory]
        [InlineData("file")]
        [InlineData("environment")]
        [InlineData("implementation")]
        [InlineData("definition")]
        [InlineData("manifest")]
        public async Task Corrupted_Content_Cannot_Be_Used(string kind)
        {
            using var fixture = new PublicationTestSupport.Fixture(); var publication = await fixture.PublishAsync();
            var key = fixture.MemoryPayloads.Documents.Keys.First(k => k.Contains("/" + kind + "/", StringComparison.Ordinal));
            fixture.MemoryPayloads.Documents[key] += " ";
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync(publication.PublicationRef));
        }

        [Theory]
        [InlineData("missing")]
        [InlineData("runtime-digest")]
        [InlineData("runtime-version")]
        [InlineData("language")]
        public async Task A_Pinned_Environment_Must_Remain_Exactly_Available(string change)
        {
            using var fixture = new PublicationTestSupport.Fixture(); var publication = await fixture.PublishAsync();
            var environment = fixture.Environments.Entries["python-fixed"];
            if (change == "missing") fixture.Environments.Entries.Remove("python-fixed");
            else fixture.Environments.Entries["python-fixed"] = change switch
            {
                "runtime-digest" => environment with { RuntimeSha256 = PublicationTestSupport.Hash("replacement") },
                "runtime-version" => environment with { RuntimeVersion = "2.0.0" },
                _ => environment with { ExecutionLanguage = "dotnet" }
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync(publication.PublicationRef));
        }

        [Fact]
        public async Task Manifest_Is_Last_And_An_Interrupted_Write_Can_Be_Retried()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            fixture.MemoryPayloads.BeforeWrite = (key, _) => key.Contains("/manifest/", StringComparison.Ordinal)
                ? Task.FromException(new IOException("Injected interrupted publication.")) : Task.CompletedTask;
            await Assert.ThrowsAsync<IOException>(() => fixture.PublishAsync());
            Assert.NotEmpty(fixture.MemoryPayloads.Documents);
            Assert.DoesNotContain(fixture.MemoryPayloads.Documents.Keys, k => k.Contains("/manifest/", StringComparison.Ordinal));
            fixture.MemoryPayloads.BeforeWrite = null;
            var publication = await fixture.PublishAsync();
            Assert.Contains("/manifest/", fixture.MemoryPayloads.Writes.Last());
            await fixture.ReadAsync(publication.PublicationRef);
        }

        [Fact]
        public async Task Lost_Publication_Acknowledgement_Does_Not_Create_A_New_Version()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            fixture.MemoryPayloads.AfterWrite = (key, _) => key.Contains("/manifest/", StringComparison.Ordinal)
                ? Task.FromException(new IOException("Injected lost acknowledgement.")) : Task.CompletedTask;
            await Assert.ThrowsAsync<IOException>(() => fixture.PublishAsync());
            var key = fixture.MemoryPayloads.Documents.Keys.Single(k => k.Contains("/manifest/", StringComparison.Ordinal));
            var count = fixture.MemoryPayloads.Documents.Count; fixture.MemoryPayloads.AfterWrite = null;
            var publication = await fixture.PublishAsync();
            Assert.Equal("pub-" + key.Split('/').Last(), publication.PublicationRef);
            Assert.Equal(count, fixture.MemoryPayloads.Documents.Count);
        }

        [Fact]
        public async Task Concurrent_Identical_Publishers_Converge_On_One_Manifest()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            fixture.MemoryPayloads.BeforeWrite = async (_, _) => await Task.Yield();
            var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => fixture.PublishAsync()));
            Assert.Single(results.Select(p => p.PublicationRef).Distinct());
            Assert.Single(fixture.MemoryPayloads.Documents.Keys.Where(k => k.Contains("/manifest/", StringComparison.Ordinal)));
        }

        [Fact]
        public async Task Returned_Manifest_Mutations_Do_Not_Modify_Persisted_Content()
        {
            using var fixture = new PublicationTestSupport.Fixture(); var publication = await fixture.PublishAsync();
            var original = publication.Manifest.Functions[0];
            ((AiPublicationFunction[])publication.Manifest.Functions)[0] = original with { LogicalName = "tampered" };
            var restored = await fixture.ReadAsync(publication.PublicationRef);
            Assert.Equal(original.LogicalName, restored.Manifest.Functions.Single(f => f.Site == original.Site).LogicalName);
        }

        [Theory]
        [InlineData("latest")]
        [InlineData("pub-not-a-digest")]
        [InlineData("pub-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
        [InlineData("https://source/manifest")]
        public async Task Only_Exact_Content_References_Are_Accepted(string reference)
        {
            using var fixture = new PublicationTestSupport.Fixture();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync(reference));
            Assert.Equal(0, fixture.MemoryPayloads.Reads);
        }
    }
}
