using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Payloads.Stores;
using Multiplexed.AI.Runtime.Execution.Payloads.Mongo.Stores;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Payloads.Mongo
{
    /// <summary>
    /// Validates exact-key immutable writes in the existing Mongo payload store.
    /// </summary>
    public sealed class MongoAiPayloadStoreImmutableTests : IAsyncLifetime
    {
        private readonly string connectionString;
        private readonly string databaseName;
        private readonly MongoClient client;
        private readonly MongoAiPayloadStore store;

        public MongoAiPayloadStoreImmutableTests()
        {
            this.connectionString =
                Environment.GetEnvironmentVariable("MONGO_TEST_CONNECTION_STRING")
                ?? Environment.GetEnvironmentVariable("MONGODB_TEST_CONNECTION_STRING")
                ?? "mongodb://localhost:27017";
            this.databaseName = $"multiplexed_child_payload_{Guid.NewGuid():N}";
            this.client = new MongoClient(this.connectionString);
            this.store = new MongoAiPayloadStore(
                Options.Create(
                    new AiPayloadStoreOptions
                    {
                        Enabled = true,
                        Provider = "mongo",
                        RequireReplaySafePayloads = true,
                        Mongo = new Multiplexed.Abstractions.AI.Execution.Payloads.Mongo.MongoAiPayloadStoreOptions
                        {
                            Enabled = true,
                            ConnectionString = this.connectionString,
                            DatabaseName = this.databaseName,
                            CollectionName = "payloads"
                        }
                    }));
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            await this.client.DropDatabaseAsync(this.databaseName).ConfigureAwait(false);
        }

        [Fact]
        public async Task SaveImmutableAsync_Should_Be_Idempotent_And_Reject_Conflicting_Content()
        {
            IAiImmutablePayloadStore immutableStore = this.store;
            var metadata = new AiPayloadMetadata
            {
                Kind = "child-dag-definition",
                ExecutionId = "parent-1",
                ContentType = "application/json"
            };

            var first = await immutableStore.SaveImmutableAsync(
                "immutable-sha256-test",
                "{\"value\":1}",
                metadata);
            var second = await immutableStore.SaveImmutableAsync(
                "immutable-sha256-test",
                "{\"value\":1}",
                metadata);

            Assert.Equal("immutable-sha256-test", first);
            Assert.Equal(first, second);
            Assert.Equal("{\"value\":1}", await this.store.LoadAsync(first));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => immutableStore.SaveImmutableAsync(
                    "immutable-sha256-test",
                    "{\"value\":2}",
                    metadata));
        }
    }
}
