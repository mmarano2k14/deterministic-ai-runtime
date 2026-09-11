using System.Text;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers
{
    /// <summary>Original-owner restoration, real TRN authorization and actual immutable publication reads.</summary>
    public sealed class AiWorkerPublicationPreparationTests
    {
        [Theory]
        [InlineData("python")]
        [InlineData("typescript")]
        [InlineData("dotnet")]
        public async Task Projects_Only_The_Exact_Selected_Function_And_Dependencies(string language)
        {
            using var f = new WorkerTestSupport.PublishedFixture(); var (_, invocation) = await f.PrepareAsync(language);
            var bundle = await f.Preparer.PrepareAsync(invocation);
            Assert.Equal(invocation.Definition.Target, bundle.Target); Assert.Equal(language, bundle.Runtime.ExecutionLanguage);
            Assert.Single(bundle.Sources); Assert.Equal("main.txt", bundle.EntryPointPath); Assert.Equal("run", bundle.EntryPointSymbol);
            Assert.Equal("code-1", Decode(bundle.Sources[0].Base64Url));
            Assert.Equal("dependency-1", Decode(Assert.Single(Assert.Single(bundle.Dependencies).Files).Base64Url));
            var json = JsonSerializer.Serialize(bundle);
            foreach (var forbidden in new[] { "publication/", "ContextKey", "Namespaces", "Trns", "TenantGroupId", "UserId" })
                Assert.DoesNotContain(forbidden, json);
        }
        [Fact]
        public async Task Background_Dispatch_Restores_Then_Clears_The_Original_Context()
        {
            using var f = new WorkerTestSupport.PublishedFixture(); var (_, invocation) = await f.PrepareAsync();
            f.Publication.Accessor.Clear(); await f.Preparer.PrepareAsync(invocation); Assert.Null(f.Publication.Accessor.Current);
        }
        [Fact]
        public async Task Ambient_Foreign_Context_Is_Not_Used_As_The_Execution_Owner()
        {
            using var f = new WorkerTestSupport.PublishedFixture(); var (_, invocation) = await f.PrepareAsync();
            var previous = PublicationTestSupport.Identity(tenant: "other", group: "other", user: "other");
            f.Publication.Accessor.Set(previous); await f.Preparer.PrepareAsync(invocation);
            Assert.Same(previous, f.Publication.Accessor.Current); f.Publication.Accessor.Clear();
        }
        [Fact]
        public async Task Existing_Execute_Capability_Is_Required_Before_Artifact_Reads()
        {
            using var f = new WorkerTestSupport.PublishedFixture(); var (parent, invocation) = await f.PrepareAsync();
            parent.ExecutionContextSnapshot!.Namespaces[0].Trns.Clear(); await f.Publication.Store.SaveRecordAsync(parent);
            var reads = f.Publication.MemoryPayloads.Reads;
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Preparer.PrepareAsync(invocation));
            Assert.Equal(reads, f.Publication.MemoryPayloads.Reads); Assert.Null(f.Publication.Accessor.Current);
        }
        [Fact]
        public async Task A_Changed_Execution_Owner_Cannot_Reuse_The_Original_Run_Pin()
        {
            using var f = new WorkerTestSupport.PublishedFixture(); var (parent, invocation) = await f.PrepareAsync();
            parent.ExecutionContextSnapshot!.UserId = "other-user"; await f.Publication.Store.SaveRecordAsync(parent);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Preparer.PrepareAsync(invocation));
        }
        [Theory]
        [InlineData("tenant")]
        [InlineData("group")]
        public async Task Foreign_Parent_Snapshot_Is_Refused(string field)
        {
            using var f = new WorkerTestSupport.PublishedFixture(); var (parent, invocation) = await f.PrepareAsync();
            if (field == "tenant") parent.ExecutionContextSnapshot!.TenantId = "other"; else parent.ExecutionContextSnapshot!.TenantGroupId = "other";
            await f.Publication.Store.SaveRecordAsync(parent);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Preparer.PrepareAsync(invocation));
        }
        [Theory]
        [InlineData(AiExecutionStatus.Completed)]
        [InlineData(AiExecutionStatus.Cancelled)]
        [InlineData(AiExecutionStatus.Failed)]
        public async Task Terminal_Parent_Cannot_Start_New_Work(AiExecutionStatus status)
        {
            using var f = new WorkerTestSupport.PublishedFixture(); var (parent, invocation) = await f.PrepareAsync();
            parent.Status = status; await f.Publication.Store.SaveRecordAsync(parent);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Preparer.PrepareAsync(invocation));
        }
        [Fact]
        public async Task Republication_Does_Not_Change_The_Worker_Bytes_For_An_Existing_Run()
        {
            using var f = new WorkerTestSupport.PublishedFixture(); var (_, invocation) = await f.PrepareAsync();
            var newer = await f.Publication.PublishAsync(PublicationTestSupport.Upload("2"));
            var bundle = await f.Preparer.PrepareAsync(invocation);
            Assert.NotEqual(newer.PublicationRef, bundle.Target.PublicationRef); Assert.Equal("code-1", Decode(bundle.Sources[0].Base64Url));
        }
        [Fact]
        public async Task Missing_Selected_Source_Is_Not_Replaced_By_Newer_Material()
        {
            using var f = new WorkerTestSupport.PublishedFixture(); var (_, invocation) = await f.PrepareAsync();
            foreach (var key in f.Publication.MemoryPayloads.Documents.Keys.Where(k => k.Contains("/file/", StringComparison.Ordinal)).ToArray())
                f.Publication.MemoryPayloads.Documents.TryRemove(key, out _);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Preparer.PrepareAsync(invocation));
            Assert.Null(f.Publication.Accessor.Current);
        }
        [Fact]
        public async Task Corrupted_Artifact_Cannot_Cross_The_Worker_Boundary()
        {
            using var f = new WorkerTestSupport.PublishedFixture(); var (_, invocation) = await f.PrepareAsync();
            f.Publication.MemoryPayloads.OnRead = (key, value) => key.Contains("/file/", StringComparison.Ordinal) ? "{}" : value;
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Preparer.PrepareAsync(invocation));
        }
        [Fact]
        public async Task Missing_Pinned_Runtime_Is_An_Explicit_Error()
        {
            using var f = new WorkerTestSupport.PublishedFixture(); var (_, invocation) = await f.PrepareAsync();
            f.Publication.Environments.Entries.Clear();
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Preparer.PrepareAsync(invocation));
        }
        [Fact]
        public async Task Cancellation_Restores_The_Previous_Context()
        {
            using var f = new WorkerTestSupport.PublishedFixture(); var (_, invocation) = await f.PrepareAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Preparer.PrepareAsync(invocation, new CancellationToken(true)));
            Assert.Null(f.Publication.Accessor.Current);
        }
        private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(
            value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4)));
    }
}
