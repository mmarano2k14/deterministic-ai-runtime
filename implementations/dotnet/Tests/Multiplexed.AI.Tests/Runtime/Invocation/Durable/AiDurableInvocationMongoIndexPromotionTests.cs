using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Durable.Mongo;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Durable
{
    [Trait("Category", "MongoIntegration")]
    public sealed class AiDurableInvocationMongoIndexPromotionTests
    {
        [DurableInvocationMongoFact]
        public async Task Production_Indexes_Include_Hybrid_Dispatch_And_Continuation_V2_Key_Order()
        {
            await using var fixture = new Fixture();
            var store = fixture.Store();
            await new AiDurableInvocationJournal(store).PrepareAsync(DurableInvocationTestSupport.Definition());

            using var cursor = await fixture.Collection.Indexes.ListAsync();
            var indexes = await cursor.ToListAsync();

            AssertIndex(indexes, "ix_durable_invocation_dispatch",
                "controlPlaneId", "tenantId", "tenantGroupId", "language", "status", "leaseExpiresAt", "updatedAt");
            AssertIndex(indexes, "ix_durable_invocation_dispatch_prepared_v2",
                "controlPlaneId", "tenantId", "tenantGroupId", "language", "status", "updatedAt", "_id");
            AssertIndex(indexes, "ix_durable_invocation_continuation_v2",
                "controlPlaneId", "tenantId", "tenantGroupId", "continuationStatus", "status", "updatedAt", "_id");
        }

        [DurableInvocationMongoFact]
        public async Task Hybrid_Dispatch_Paging_Preserves_Reference_Order_And_Excludes_Live_Leases()
        {
            await using var fixture = new Fixture();
            var queryNow = DateTimeOffset.Parse("2026-09-21T00:00:00+00:00");
            var records = new[]
            {
                await CreateRecordAsync("prepared-a", queryNow.AddMinutes(-10), RecordKind.Prepared),
                await CreateRecordAsync("expired-a", queryNow.AddMinutes(-9), RecordKind.ExpiredLease),
                await CreateRecordAsync("prepared-b", queryNow.AddMinutes(-8), RecordKind.Prepared),
                await CreateRecordAsync("live-a", queryNow.AddMinutes(-4), RecordKind.LiveLease),
                await CreateRecordAsync("expired-b", queryNow.AddMinutes(-3), RecordKind.ExpiredLease)
            };
            await fixture.Collection.InsertManyAsync(records.Select(record =>
                DurableInvocationTestSupport.Codec<BsonDocument>("Encode", record)));

            var expected = records
                .Where(record => record.Status == AiDurableInvocationStatus.Prepared ||
                    record.Status == AiDurableInvocationStatus.Leased && record.Lease!.ExpiresAtUtc <= queryNow)
                .OrderBy(record => record.UpdatedAtUtc)
                .ThenBy(record => record.OperationId, StringComparer.Ordinal)
                .Select(record => record.OperationId)
                .ToArray();

            var actual = new List<string>();
            AiWorkerDispatchCursor? after = null;
            while (true)
            {
                var page = await fixture.Store().ListDispatchPageAsync(
                    DurableInvocationTestSupport.Scope, "python", queryNow, 2, after);
                if (page.Count == 0) break;
                actual.AddRange(page.Select(record => record.OperationId));
                after = new AiWorkerDispatchCursor(page[^1].UpdatedAtUtc, page[^1].OperationId);
            }

            Assert.Equal(expected, actual);
            Assert.DoesNotContain(records.Single(record => record.Definition.Identity.StepName == "live-a").OperationId, actual);
        }

        private static void AssertIndex(IReadOnlyList<BsonDocument> indexes, string name, params string[] expectedKeys)
        {
            var index = Assert.Single(indexes.Where(document => document["name"].AsString == name));
            Assert.Equal(expectedKeys, index["key"].AsBsonDocument.Names.ToArray());
        }

        private static async Task<AiDurableInvocationRecord> CreateRecordAsync(
            string stepName, DateTimeOffset updatedAt, RecordKind kind)
        {
            var clock = new DurableInvocationTestSupport.Clock();
            clock.Set(updatedAt);
            var store = new DurableInvocationTestSupport.MemoryStore(clock);
            var journal = new AiDurableInvocationJournal(store, clock);
            var identity = DurableInvocationTestSupport.Identity with { StepName = stepName };
            var definition = DurableInvocationTestSupport.Definition() with { Identity = identity };
            var prepared = await journal.PrepareAsync(definition);
            if (kind == RecordKind.Prepared) return prepared;

            clock.Advance(TimeSpan.FromMilliseconds(1));
            var duration = kind == RecordKind.ExpiredLease ? TimeSpan.FromMilliseconds(1) : TimeSpan.FromMinutes(5);
            return (await journal.TryAcquireLeaseAsync(
                definition.Scope, definition.Identity, "worker-" + stepName, duration))!;
        }

        private enum RecordKind { Prepared, ExpiredLease, LiveLease }

        private sealed class Fixture : IAsyncDisposable
        {
            private readonly IMongoClient _client;
            private readonly IMongoDatabase _database;
            internal IMongoCollection<BsonDocument> Collection { get; }

            internal Fixture()
            {
                var connectionString = Environment.GetEnvironmentVariable(DurableInvocationMongoFactAttribute.ConnectionVariable)
                    ?? throw new InvalidOperationException("Explicit test Mongo connection is required.");
                var settings = MongoClientSettings.FromConnectionString(connectionString);
                settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10);
                settings.ConnectTimeout = TimeSpan.FromSeconds(10);
                _client = new MongoClient(settings);
                _database = _client.GetDatabase("sdk_invocation_h5_" + Guid.NewGuid().ToString("N"));
                Collection = _database.GetCollection<BsonDocument>("invocations");
            }

            internal MongoAiDurableInvocationStore Store() => new(
                _database, Options.Create(new AiDurableInvocationMongoOptions { CollectionName = "invocations" }));

            public async ValueTask DisposeAsync() =>
                await _client.DropDatabaseAsync(_database.DatabaseNamespace.DatabaseName);
        }
    }
}
