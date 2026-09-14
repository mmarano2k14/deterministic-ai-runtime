using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Durable.Mongo;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Durable
{
    /// <summary>
    /// Explicit Mongo integration suite. Uses a random test database, never a configured
    /// application database. Missing opt-in is a visible skip; a configured unreachable
    /// Mongo server is a failure, not a skipped or silently successful test.
    /// </summary>
    [Trait("Category", "MongoIntegration")]
    public sealed class AiDurableInvocationMongoIntegrationTests
    {
        [DurableInvocationMongoFact]
        public async Task Concurrent_Preparation_Uses_One_Typed_Identity_Across_Store_Instances()
        {
            await using var fixture = new MongoFixture();
            var records = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => fixture.NewJournal()
                .PrepareAsync(DurableInvocationTestSupport.Definition())));
            Assert.All(records, record => Assert.Equal(records[0], record));
            Assert.Equal(1L, await fixture.Collection.CountDocumentsAsync(new BsonDocument()));
            var indexes = await (await fixture.Collection.Indexes.ListAsync()).ToListAsync();
            Assert.Contains(indexes, index => index["name"].AsString == "uq_durable_invocation_identity" && index["unique"].AsBoolean);
        }

        [DurableInvocationMongoFact]
        public async Task Conflicting_Immutable_Publication_Does_Not_Overwrite_Preparation()
        {
            await using var fixture = new MongoFixture();
            var journal = fixture.NewJournal();
            var definition = DurableInvocationTestSupport.Definition();
            var original = await journal.PrepareAsync(definition);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.NewJournal().PrepareAsync(definition with
                { Target = definition.Target with { PublicationSha256 = DurableInvocationTestSupport.Digest('e') } }));
            Assert.Equal(original, await journal.GetAsync(definition.Scope, definition.Identity));
        }

        [DurableInvocationMongoFact]
        public async Task Case_Distinct_Tenants_Do_Not_Share_An_Operation()
        {
            await using var fixture = new MongoFixture();
            var journal = fixture.NewJournal();
            var definition = DurableInvocationTestSupport.Definition();
            var first = await journal.PrepareAsync(definition);
            var secondDefinition = definition with
            {
                Identity = definition.Identity with { TenantId = "TENANT-A" },
                Scope = definition.Scope with { TenantId = "TENANT-A" }
            };
            var second = await journal.PrepareAsync(secondDefinition);
            Assert.NotEqual(first.OperationId, second.OperationId);
            Assert.Equal(first, await journal.GetAsync(definition.Scope, definition.Identity));
            Assert.Equal(second, await journal.GetAsync(secondDefinition.Scope, secondDefinition.Identity));
            Assert.Equal(2L, await fixture.Collection.CountDocumentsAsync(new BsonDocument()));
        }

        [DurableInvocationMongoFact]
        public async Task Concurrent_Assignment_And_Duplicate_Result_Are_Atomic_In_Mongo()
        {
            await using var fixture = new MongoFixture();
            await fixture.NewJournal().PrepareAsync(DurableInvocationTestSupport.Definition());
            var leases = await Task.WhenAll(Enumerable.Range(0, 16).Select(index => fixture.NewJournal().TryAcquireLeaseAsync(
                DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity, "worker-" + index, TimeSpan.FromMinutes(2))));
            var winner = Assert.Single(leases.Where(record => record is not null))!;
            var completions = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => fixture.NewJournal().CompleteAsync(
                DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity, winner.Lease!, DurableInvocationTestSupport.Result())));
            Assert.Equal(1, completions.Count(value => value == AiDurableInvocationCompletionStatus.Accepted));
            Assert.Equal(15, completions.Count(value => value == AiDurableInvocationCompletionStatus.AlreadyAccepted));
            var current = (await fixture.NewJournal().GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity))!;
            Assert.Equal(2, current.Revision);
            Assert.Equal(AiDurableInvocationContinuationStatus.Pending, current.ContinuationStatus);
        }

        [DurableInvocationMongoFact]
        public async Task Result_And_Scheduled_Continuation_Survive_A_New_Store_Instance()
        {
            await using var fixture = new MongoFixture();
            var journal = fixture.NewJournal();
            var completed = await CompleteAsync(journal);
            var scheduled = await journal.MarkContinuationScheduledAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity);
            var replacementStore = fixture.NewStore();
            var reloaded = new AiDurableInvocationJournal(replacementStore);
            Assert.Equal(scheduled, Assert.Single(await replacementStore.ListContinuationCandidatesAsync(DurableInvocationTestSupport.Scope, 10)));
            Assert.Equal(completed.Result, (await reloaded.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity))!.Result);
            await reloaded.AcknowledgeContinuationAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity,
                DurableInvocationTestSupport.Ack(completed));
            Assert.Empty(await replacementStore.ListContinuationCandidatesAsync(DurableInvocationTestSupport.Scope, 10));
        }

        [DurableInvocationMongoFact]
        public async Task Database_Clock_Rejects_A_Late_Result_Even_When_The_Caller_Clock_Is_Stale()
        {
            await using var fixture = new MongoFixture();
            var journal = fixture.NewJournal();
            await journal.PrepareAsync(DurableInvocationTestSupport.Definition());
            var leased = (await journal.TryAcquireLeaseAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity,
                "worker-a", TimeSpan.FromSeconds(2)))!;
            var staleClock = new DurableInvocationTestSupport.Clock(); staleClock.Set(leased.UpdatedAtUtc);
            var staleCaller = new AiDurableInvocationJournal(fixture.NewStore(), staleClock);
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            await Assert.ThrowsAsync<InvalidOperationException>(() => staleCaller.CompleteAsync(DurableInvocationTestSupport.Scope,
                DurableInvocationTestSupport.Identity, leased.Lease!, DurableInvocationTestSupport.Result()));
            Assert.Null((await journal.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity))!.Result);
            var next = (await journal.TryAcquireLeaseAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity,
                "worker-b", TimeSpan.FromMinutes(1)))!;
            Assert.Equal(leased.EffectIdempotencyKey, next.EffectIdempotencyKey);
            Assert.Equal(2, next.Lease!.Epoch);
            Assert.Equal(AiDurableInvocationCompletionStatus.LeaseRejected, await journal.CompleteAsync(DurableInvocationTestSupport.Scope,
                DurableInvocationTestSupport.Identity, leased.Lease, DurableInvocationTestSupport.Result()));
        }

        [DurableInvocationMongoFact]
        public async Task Discovery_Queries_Respect_Language_And_Ownership_Scope()
        {
            await using var fixture = new MongoFixture();
            var journal = fixture.NewJournal();
            var prepared = await journal.PrepareAsync(DurableInvocationTestSupport.Definition());
            var store = fixture.NewStore();
            Assert.Equal(prepared, Assert.Single(await store.ListDispatchCandidatesAsync(DurableInvocationTestSupport.Scope,
                "python", DateTimeOffset.UtcNow, 10)));
            Assert.Empty(await store.ListDispatchCandidatesAsync(DurableInvocationTestSupport.Scope,
                "typescript", DateTimeOffset.UtcNow, 10));
            Assert.Empty(await store.ListDispatchCandidatesAsync(DurableInvocationTestSupport.Scope with { ControlPlaneId = "other" },
                "python", DateTimeOffset.UtcNow, 10));
            Assert.Null(await store.GetAsync(DurableInvocationTestSupport.Scope with { TenantGroupId = "other" }, DurableInvocationTestSupport.Identity));
        }

        [DurableInvocationMongoFact]
        public async Task A_Conflicting_Terminal_Result_Is_Rejected_Across_Store_Instances()
        {
            await using var fixture = new MongoFixture();
            var completed = await CompleteAsync(fixture.NewJournal());
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.NewJournal().CompleteAsync(DurableInvocationTestSupport.Scope,
                DurableInvocationTestSupport.Identity, completed.Lease!, DurableInvocationTestSupport.Result(json: "{\"value\":999}")));
            Assert.Equal(completed, await fixture.NewJournal().GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity));
        }

        private static async Task<AiDurableInvocationRecord> CompleteAsync(AiDurableInvocationJournal journal)
        {
            await journal.PrepareAsync(DurableInvocationTestSupport.Definition());
            var leased = (await journal.TryAcquireLeaseAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity,
                "worker-a", TimeSpan.FromMinutes(2)))!;
            Assert.Equal(AiDurableInvocationCompletionStatus.Accepted, await journal.CompleteAsync(DurableInvocationTestSupport.Scope,
                DurableInvocationTestSupport.Identity, leased.Lease!, DurableInvocationTestSupport.Result()));
            return (await journal.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity))!;
        }

        private sealed class MongoFixture : IAsyncDisposable
        {
            private static readonly Lazy<IMongoClient> Client = new(() => new MongoClient(
                Environment.GetEnvironmentVariable(DurableInvocationMongoFactAttribute.ConnectionVariable)
                    ?? throw new InvalidOperationException("Explicit Mongo test connection is required.")));
            private readonly IMongoDatabase _database = Client.Value.GetDatabase("sdk_invocation_tests_" + Guid.NewGuid().ToString("N"));
            internal IMongoCollection<BsonDocument> Collection => _database.GetCollection<BsonDocument>("invocations");
            internal MongoAiDurableInvocationStore NewStore() => new(_database,
                Options.Create(new AiDurableInvocationMongoOptions { CollectionName = "invocations" }));
            internal AiDurableInvocationJournal NewJournal() => new(NewStore());
            public async ValueTask DisposeAsync() => await Client.Value.DropDatabaseAsync(_database.DatabaseNamespace.DatabaseName);
        }
    }

    /// <summary>Do not report an unconfigured infrastructure test as a passing test.</summary>
    public sealed class DurableInvocationMongoFactAttribute : FactAttribute
    {
        public const string ConnectionVariable = "MULTIPLEXED_TEST_MONGO_INVOCATION_CONNECTION_STRING";
        public DurableInvocationMongoFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = "Set " + ConnectionVariable + " to a test MongoDB instance to run this integration test.";
        }
    }
}
