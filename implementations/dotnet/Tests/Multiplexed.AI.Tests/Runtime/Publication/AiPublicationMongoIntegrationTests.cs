using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Execution.Payloads.Mongo;
using Multiplexed.Abstractions.AI.Execution.Payloads.Stores;
using Multiplexed.AI.Runtime.Execution.Payloads.Mongo.Stores;

namespace Multiplexed.AI.Tests.Runtime.Publication
{
    /// <summary>Explicit opt-in against the existing real Mongo payload store, with a unique collection per case.</summary>
    public sealed class AiPublicationMongoIntegrationTests
    {
        [PublicationMongoFact]
        public async Task Concurrent_Identical_Publication_Uses_The_Real_Immutable_Mongo_Store()
        {
            await WithStoreAsync(async store =>
            {
                using var fixture = new PublicationTestSupport.Fixture(payloadStore: store);
                var publications = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.PublishAsync()));
                Assert.Single(publications.Select(p => p.PublicationRef).Distinct());
                await fixture.ReadAsync(publications[0].PublicationRef);
            });
        }

        [PublicationMongoFact]
        public async Task Run_Pin_Rejects_Conflicting_Publication_Through_Mongo_Uniqueness()
        {
            await WithStoreAsync(async store =>
            {
                using var fixture = new PublicationTestSupport.Fixture(payloadStore: store);
                var first = await fixture.PublishAsync(); var second = await fixture.PublishAsync(PublicationTestSupport.Upload("2"));
                var run = await fixture.CreateAsync(first);
                await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateAsync(second));
                Assert.Equal(first.PublicationRef, (await fixture.TargetAsync(run))!.PublicationRef);
            });
        }

        [PublicationMongoFact]
        public async Task Fresh_Services_Read_The_Stored_Version_After_Another_Version_Is_Published()
        {
            await WithStoreAsync(async store =>
            {
                string reference;
                using (var first = new PublicationTestSupport.Fixture(payloadStore: store))
                {
                    reference = (await first.PublishAsync()).PublicationRef;
                    await first.PublishAsync(PublicationTestSupport.Upload("2"));
                }
                using var restored = new PublicationTestSupport.Fixture(payloadStore: store);
                Assert.Equal(reference, (await restored.ReadAsync(reference)).PublicationRef);
            });
        }

        [PublicationMongoFact]
        public async Task Missing_Stored_Definition_Does_Not_Fall_Back_To_A_Provider()
        {
            await WithStoreAsync(async store =>
            {
                using var fixture = new PublicationTestSupport.Fixture(payloadStore: store);
                var publication = await fixture.PublishAsync();
                await store.DeleteAsync(publication.Manifest.Definition.Key);
                await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync(publication.PublicationRef));
                Assert.Equal(0, fixture.LatestLookups);
            });
        }

        private static async Task WithStoreAsync(Func<MongoAiPayloadStore, Task> action)
        {
            var connection = System.Environment.GetEnvironmentVariable(PublicationMongoFactAttribute.Variable)!;
            var collection = "publication_test_" + Guid.NewGuid().ToString("N");
            const string databaseName = "multiplexed_sdk_publication_tests";
            var client = new MongoClient(connection);
            var database = client.GetDatabase(databaseName);
            var options = Options.Create(new AiPayloadStoreOptions { Provider = "mongo", Mongo = new MongoAiPayloadStoreOptions
                { Enabled = true, ConnectionString = connection, DatabaseName = databaseName, CollectionName = collection } });
            try { await action(new MongoAiPayloadStore(options)); }
            finally { await database.DropCollectionAsync(collection); }
        }
    }

    public sealed class PublicationMongoFactAttribute : FactAttribute
    {
        public const string Variable = "MULTIPLEXED_TEST_MONGO_PUBLICATION_CONNECTION_STRING";
        public PublicationMongoFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(Variable)))
                Skip = "Set " + Variable + " to a dedicated MongoDB test instance to execute this case.";
        }
    }
}
