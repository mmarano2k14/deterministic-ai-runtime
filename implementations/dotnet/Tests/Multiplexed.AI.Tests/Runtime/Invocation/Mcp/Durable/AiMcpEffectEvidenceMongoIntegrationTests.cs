using System.Text.Json;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.Abstractions.AI.Invocation.Mcp.Durable;
using Multiplexed.AI.Runtime.Invocation.Mcp;
using Multiplexed.AI.Runtime.Invocation.Mcp.Durable;
using Multiplexed.AI.Runtime.Invocation.Mcp.Durable.Mongo;

namespace Multiplexed.AI.Tests.Runtime.Invocation.McpEffects.Durable
{
    /// <summary>
    /// Explicit Mongo evidence tests. Missing opt-in is a visible skip; a configured but
    /// unreachable Mongo server is a failure.
    /// </summary>
    [Trait("Category", "MongoIntegration")]
    public sealed class AiMcpEffectEvidenceMongoIntegrationTests
    {
        [McpEffectMongoFact]
        public async Task Concurrent_Preparation_Persists_One_Immutable_Effect_Intent()
        {
            await using var fixture = new MongoFixture();
            var request = Request("attempt-a", "{\"value\":1}");
            var records = await Task.WhenAll(Enumerable.Range(0, 20)
                .Select(_ => fixture.NewJournal().PrepareAsync(request)));
            Assert.All(records, record => Assert.Equal(records[0], record));
            Assert.Equal(1L, await fixture.Collection.CountDocumentsAsync(new BsonDocument()));
            var indexes = await (await fixture.Collection.Indexes.ListAsync()).ToListAsync();
            Assert.Contains(indexes, index => index["name"].AsString == "uq_mcp_effect_identity" && index["unique"].AsBoolean);
        }

        [McpEffectMongoFact]
        public async Task Conflicting_Intent_Does_Not_Overwrite_The_First_Record()
        {
            await using var fixture = new MongoFixture();
            var firstRequest = Request("attempt-a", "{\"value\":1}");
            var changedRequest = Request("attempt-b", "{\"value\":2}");
            var original = await fixture.NewJournal().PrepareAsync(firstRequest);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.NewJournal().PrepareAsync(changedRequest));
            Assert.Equal(original, await fixture.NewJournal().GetAsync(original.Scope, original.Intent.Effect.EffectId));
        }

        [McpEffectMongoFact]
        public async Task Revision_Cas_And_Reconciliation_Discovery_Are_Tenant_Scoped()
        {
            await using var fixture = new MongoFixture();
            var prepared = await fixture.NewJournal().PrepareAsync(Request("attempt-a", "{\"value\":1}"));
            var startedAt = prepared.CreatedAtUtc.AddSeconds(1);
            var dispatching = prepared with
            {
                Revision = 1,
                Status = AiMcpEffectEvidenceStatus.Dispatching,
                Attempt = new AiMcpEffectDispatchAttempt("attempt-a", startedAt, startedAt.AddSeconds(30)),
                UpdatedAtUtc = startedAt
            };
            var store = fixture.NewStore();
            Assert.True(await store.TryReplaceAsync(prepared, dispatching));
            Assert.False(await fixture.NewStore().TryReplaceAsync(prepared, dispatching));

            Assert.Empty(await store.ListReconciliationCandidatesAsync(
                prepared.Scope, startedAt.AddMilliseconds(-1), 10));
            Assert.Equal(dispatching, Assert.Single(await store.ListReconciliationCandidatesAsync(
                prepared.Scope, startedAt, 10)));
            Assert.Empty(await store.ListReconciliationCandidatesAsync(
                prepared.Scope with { TenantGroupId = "other" }, startedAt.AddMinutes(1), 10));
        }

        private static AiMcpToolRequest Request(string requestId, string argumentsJson)
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var request = new AiMcpToolRequest(
                AiMcpEffectIdentities.RequestSchemaVersion,
                requestId,
                DateTimeOffset.UtcNow.AddMinutes(1),
                new AiMcpToolInvocationContext(
                    "tenant-a", "group-a", "execution-a", "pipeline-a", "v1", "publish", "step-001"),
                "connection-a", "revision-1", "probe.echo", document.RootElement.Clone());
            return request with { Effect = AiMcpEffectIdentities.Create(request) };
        }

        private sealed class MongoFixture : IAsyncDisposable
        {
            private static readonly Lazy<IMongoClient> Client = new(() => new MongoClient(
                Environment.GetEnvironmentVariable(McpEffectMongoFactAttribute.ConnectionVariable)
                    ?? throw new InvalidOperationException("Explicit Mongo test connection is required.")));
            private readonly IMongoDatabase _database = Client.Value.GetDatabase("sdk_mcp_effect_tests_" + Guid.NewGuid().ToString("N"));
            internal IMongoCollection<BsonDocument> Collection => _database.GetCollection<BsonDocument>("effects");
            internal MongoAiMcpEffectEvidenceStore NewStore() => new(_database,
                Options.Create(new AiMcpEffectEvidenceMongoOptions { CollectionName = "effects" }));
            internal AiMcpEffectEvidenceJournal NewJournal() => new(NewStore());
            public async ValueTask DisposeAsync() => await Client.Value.DropDatabaseAsync(_database.DatabaseNamespace.DatabaseName);
        }
    }

    /// <summary>Unconfigured infrastructure validation is skipped, never reported as passing.</summary>
    public sealed class McpEffectMongoFactAttribute : FactAttribute
    {
        public const string ConnectionVariable = "MULTIPLEXED_TEST_MONGO_MCP_EFFECT_CONNECTION_STRING";

        public McpEffectMongoFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = "Set " + ConnectionVariable + " to a test MongoDB instance to run this integration test.";
        }
    }
}
