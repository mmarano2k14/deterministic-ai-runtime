using System.Text.Json;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.Abstractions.AI.Invocation.Mcp.Durable;
using Multiplexed.AI.Runtime.Invocation.Mcp;
using Multiplexed.AI.Runtime.Invocation.Mcp.Durable;
using Multiplexed.AI.Runtime.Invocation.Mcp.Durable.Mongo;

namespace Multiplexed.AI.Tests.Runtime.Invocation.McpEffects.Durable
{
    /// <summary>Opt-in persistence proofs for MCP recovery across reconstructed journal instances.</summary>
    [Trait("Category", "MongoIntegration")]
    public sealed class AiMcpEffectRecoveryMongoIntegrationTests
    {
        [McpEffectMongoFact]
        public async Task Dispatching_Evidence_Survives_Journal_Recreation_And_Blocks_Reemission()
        {
            await using var fixture = new MongoFixture();
            var request = CreateRequest("attempt-a");
            var firstJournal = fixture.NewJournal();
            var prepared = await firstJournal.PrepareAsync(request);
            var dispatching = await firstJournal.TryBeginDispatchAsync(prepared, request);
            Assert.NotNull(dispatching);

            var freshStore = fixture.NewStore();
            var candidate = Assert.Single(await freshStore.ListReconciliationCandidatesAsync(
                dispatching!.Scope,
                dispatching.Attempt!.StartedAtUtc,
                10));
            Assert.Equal(AiMcpEffectEvidenceStatus.Dispatching, candidate.Status);
            Assert.Equal(dispatching.Scope, candidate.Scope);
            Assert.Equal(dispatching.Intent.Effect.EffectId, candidate.Intent.Effect.EffectId);
            Assert.Equal("attempt-a", candidate.Attempt!.RequestId);

            var physical = new CountingTransport();
            var restarted = new AiDurableMcpToolTransport(fixture.NewJournal(), physical);
            var second = request with
            {
                RequestId = "attempt-b",
                DeadlineUtc = request.DeadlineUtc.AddMinutes(1)
            };

            var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.InvokeAsync(second));

            Assert.Contains("Dispatching", blocked.Message);
            Assert.Equal(0, physical.Calls);
        }

        [McpEffectMongoFact]
        public async Task Completed_Evidence_Survives_Journal_Recreation_And_Replays_Locally()
        {
            await using var fixture = new MongoFixture();
            var request = CreateRequest("attempt-a");
            var firstJournal = fixture.NewJournal();
            var prepared = await firstJournal.PrepareAsync(request);
            var dispatching = await firstJournal.TryBeginDispatchAsync(prepared, request);
            Assert.NotNull(dispatching);
            var completed = await firstJournal.TryCompleteAsync(
                dispatching!,
                Response("attempt-a", "persisted"));
            Assert.NotNull(completed);

            var physical = new CountingTransport();
            var restarted = new AiDurableMcpToolTransport(fixture.NewJournal(), physical);
            var second = request with
            {
                RequestId = "attempt-b",
                DeadlineUtc = request.DeadlineUtc.AddMinutes(1)
            };

            var replay = await restarted.InvokeAsync(second);

            Assert.Equal("attempt-b", replay.GetProperty("requestId").GetString());
            Assert.Equal("persisted", replay.GetProperty("structuredContent").GetProperty("value").GetString());
            Assert.Equal(0, physical.Calls);
        }

        private static AiMcpToolRequest CreateRequest(string requestId)
        {
            using var document = JsonDocument.Parse("{\"value\":\"same\"}");
            var request = new AiMcpToolRequest(
                AiMcpEffectIdentities.RequestSchemaVersion,
                requestId,
                DateTimeOffset.UtcNow.AddMinutes(5),
                new AiMcpToolInvocationContext(
                    "tenant-a", "group-a", "execution-a", "pipeline-a", "v1", "publish", "step-001"),
                "connection-a",
                "revision-1",
                "probe.echo",
                document.RootElement.Clone());
            return request with { Effect = AiMcpEffectIdentities.Create(request) };
        }

        private static JsonElement Response(string requestId, string value) =>
            JsonSerializer.SerializeToElement(new
            {
                schemaVersion = 1,
                requestId,
                isError = false,
                content = Array.Empty<object>(),
                structuredContent = new { value }
            });

        private sealed class CountingTransport : IAiMcpToolTransport
        {
            private int _calls;
            internal int Calls => Volatile.Read(ref _calls);

            public Task<JsonElement> InvokeAsync(
                AiMcpToolRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref _calls);
                return Task.FromResult(Response(request.RequestId, "unexpected"));
            }
        }

        private sealed class MongoFixture : IAsyncDisposable
        {
            private static readonly Lazy<IMongoClient> Client = new(() => new MongoClient(
                Environment.GetEnvironmentVariable(McpEffectMongoFactAttribute.ConnectionVariable)
                    ?? throw new InvalidOperationException("Explicit Mongo test connection is required.")));
            private readonly IMongoDatabase _database = Client.Value.GetDatabase(
                "sdk_mcp_effect_recovery_tests_" + Guid.NewGuid().ToString("N"));

            internal MongoAiMcpEffectEvidenceStore NewStore() => new(
                _database,
                Options.Create(new AiMcpEffectEvidenceMongoOptions { CollectionName = "effects" }));

            internal AiMcpEffectEvidenceJournal NewJournal() => new(NewStore());

            public async ValueTask DisposeAsync() =>
                await Client.Value.DropDatabaseAsync(_database.DatabaseNamespace.DatabaseName);
        }
    }
}
