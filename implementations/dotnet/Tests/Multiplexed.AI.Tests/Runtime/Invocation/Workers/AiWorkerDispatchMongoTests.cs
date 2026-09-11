using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Durable.Mongo;
using Multiplexed.AI.Tests.Runtime.Invocation.Durable;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers
{
    /// <summary>Opt-in real Mongo keyset filters and constrained acquisition. Missing opt-in is a visible skip.</summary>
    [Trait("Category", "MongoIntegration")]
    public sealed class AiWorkerDispatchMongoTests
    {
        [DurableInvocationMongoFact]
        public async Task Mongo_Keyset_Pages_Preserve_Timestamp_Ties_And_Ordinal_Identity()
        {
            await using var f = new Fixture(); var clock = new DurableInvocationTestSupport.Clock();
            clock.Set(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            var journal = new AiDurableInvocationJournal(f.Store(), clock);
            for (var i = 0; i < 5; i++) await journal.PrepareAsync(DurableInvocationTestSupport.Definition() with
                { Identity = DurableInvocationTestSupport.Identity with { StepName = "mongo-" + i } });
            var ids = new List<string>(); AiWorkerDispatchCursor? cursor = null;
            while (true)
            {
                var page = await f.Store().ListDispatchPageAsync(DurableInvocationTestSupport.Scope, "python", DateTimeOffset.UtcNow, 2, cursor);
                if (page.Count == 0) break;
                ids.AddRange(page.Select(r => r.OperationId)); cursor = new(page[^1].UpdatedAtUtc, page[^1].OperationId);
            }
            Assert.Equal(5, ids.Count); Assert.Equal(5, ids.Distinct().Count()); Assert.Equal(ids.OrderBy(x => x, StringComparer.Ordinal), ids);
        }
        [DurableInvocationMongoFact]
        public async Task Mongo_Discovery_Does_Not_Return_An_Active_Lease_Or_A_Terminal_Result()
        {
            await using var f = new Fixture(); var store = f.Store(); var journal = new AiDurableInvocationJournal(store);
            await journal.PrepareAsync(DurableInvocationTestSupport.Definition());
            Assert.Single(await store.ListDispatchPageAsync(DurableInvocationTestSupport.Scope, "python", DateTimeOffset.UtcNow, 10));
            var leased = (await journal.TryAcquireWorkerLeaseAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity,
                "mongo-worker", TimeSpan.FromMinutes(1), false, 1))!;
            Assert.Empty(await store.ListDispatchPageAsync(DurableInvocationTestSupport.Scope, "python", DateTimeOffset.UtcNow, 10));
            await journal.CompleteAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity, leased.Lease!, new(true, "{}"));
            Assert.Empty(await f.Store().ListDispatchPageAsync(DurableInvocationTestSupport.Scope, "python", DateTimeOffset.UtcNow, 10));
        }
        [DurableInvocationMongoFact]
        public async Task Mongo_Pages_Enforce_All_Ownership_And_Language_Filters()
        {
            await using var f = new Fixture(); var journal = new AiDurableInvocationJournal(f.Store());
            await journal.PrepareAsync(DurableInvocationTestSupport.Definition());
            foreach (var scope in new[] { DurableInvocationTestSupport.Scope with { TenantId = "foreign" },
                DurableInvocationTestSupport.Scope with { TenantGroupId = "foreign" }, DurableInvocationTestSupport.Scope with { ControlPlaneId = "foreign" } })
                Assert.Empty(await f.Store().ListDispatchPageAsync(scope, "python", DateTimeOffset.UtcNow, 10));
            Assert.Empty(await f.Store().ListDispatchPageAsync(DurableInvocationTestSupport.Scope, "typescript", DateTimeOffset.UtcNow, 10));
        }
        [DurableInvocationMongoFact]
        public async Task Concurrent_Mongo_Worker_Acquisition_Has_One_Winner_With_The_Same_Logical_Identity()
        {
            await using var f = new Fixture(); var prepared = await new AiDurableInvocationJournal(f.Store()).PrepareAsync(DurableInvocationTestSupport.Definition());
            var acquired = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => new AiDurableInvocationJournal(f.Store())
                .TryAcquireWorkerLeaseAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity,
                    "worker-" + i, TimeSpan.FromMinutes(1), false, 1)));
            var winner = Assert.Single(acquired.Where(r => r is not null))!;
            Assert.Equal(prepared.OperationId, winner.OperationId); Assert.Equal(prepared.EffectIdempotencyKey, winner.EffectIdempotencyKey); Assert.Equal(1, winner.Lease!.Epoch);
        }
        private sealed class Fixture : IAsyncDisposable
        {
            private static readonly Lazy<IMongoClient> Client = new(() =>
            {
                var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable(DurableInvocationMongoFactAttribute.ConnectionVariable)
                    ?? throw new InvalidOperationException("Explicit test Mongo connection is required."));
                settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10); settings.ConnectTimeout = TimeSpan.FromSeconds(10);
                return new MongoClient(settings);
            });
            private readonly IMongoDatabase _database = Client.Value.GetDatabase("sdk_worker_tests_" + Guid.NewGuid().ToString("N"));
            internal MongoAiDurableInvocationStore Store() => new(_database, Options.Create(new AiDurableInvocationMongoOptions { CollectionName = "invocations" }));
            public async ValueTask DisposeAsync() => await Client.Value.DropDatabaseAsync(_database.DatabaseNamespace.DatabaseName);
        }
    }
}
