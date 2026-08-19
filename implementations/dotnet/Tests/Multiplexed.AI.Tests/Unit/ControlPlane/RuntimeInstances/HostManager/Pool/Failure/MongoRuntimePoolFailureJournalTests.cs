using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Failure
{
    /// <summary>
    /// Validates the shared durable runtime-pool failure authority.
    /// </summary>
    public sealed class MongoRuntimePoolFailureJournalTests : IAsyncLifetime
    {
        private readonly string connectionString;
        private readonly MongoClient client;
        private readonly IMongoDatabase database;
        private readonly IMongoCollection<BsonDocument> collection;
        private readonly IOptions<AiRuntimePoolFailureJournalMongoOptions> options;
        private readonly string databaseName;

        public MongoRuntimePoolFailureJournalTests()
        {
            this.connectionString =
                Environment.GetEnvironmentVariable("MONGO_TEST_CONNECTION_STRING")
                ?? Environment.GetEnvironmentVariable("MONGODB_TEST_CONNECTION_STRING")
                ?? "mongodb://localhost:27017";

            this.databaseName =
                CreateDatabaseName("authority");
            this.client = new MongoClient(this.connectionString);
            this.database = this.client.GetDatabase(this.databaseName);
            this.options = Options.Create(
                new AiRuntimePoolFailureJournalMongoOptions
                {
                    ConnectionString = this.connectionString,
                    DatabaseName = this.databaseName,
                    CollectionName = "runtime_pool_failure_tests",
                    EnsureIndexes = true
                });
            this.collection =
                this.database.GetCollection<BsonDocument>(
                    this.options.Value.CollectionName);
        }

        public async Task InitializeAsync()
        {
            await this.collection
                .DeleteManyAsync(Builders<BsonDocument>.Filter.Empty)
                .ConfigureAwait(false);
        }

        public async Task DisposeAsync()
        {
            await this.client
                .DropDatabaseAsync(this.databaseName)
                .ConfigureAwait(false);
        }

        private static string CreateDatabaseName(string role)
        {
            return $"multiplexed_rpf_{role}_{Guid.NewGuid():N}";
        }

        [Fact]
        public async Task RecordAsync_Should_Remain_Durable_Across_Journal_Instances()
        {
            var first =
                new MongoAiRuntimePoolFailureJournal(
                    this.database,
                    this.options);
            var timestamp = DateTimeOffset.UtcNow;

            await first.RecordAsync(
                RuntimePoolFailureJournalTests.CreateObservation(
                    "failure-2",
                    "runtime-2") with
                {
                    ObservedAtUtc = timestamp.AddSeconds(1)
                });
            await first.RecordAsync(
                RuntimePoolFailureJournalTests.CreateObservation(
                    "failure-1",
                    "runtime-1") with
                {
                    ObservedAtUtc = timestamp
                });

            var second =
                new MongoAiRuntimePoolFailureJournal(
                    this.database,
                    this.options);
            var failures =
                await second.ListByHostIdAsync("host-01");

            Assert.Equal(
                new[] { "failure-1", "failure-2" },
                failures.Select(failure => failure.FailureId));
        }

        [Fact]
        public async Task RecordAsync_Should_Be_Idempotent_But_Reject_Conflicting_Payload()
        {
            var journal =
                new MongoAiRuntimePoolFailureJournal(
                    this.database,
                    this.options);
            var observation =
                RuntimePoolFailureJournalTests.CreateObservation(
                    "failure-idempotent",
                    "runtime-1");

            var first = await journal.RecordAsync(observation);
            var second = await journal.RecordAsync(observation);

            Assert.Equal(first, second);

            var exception =
                await Assert.ThrowsAsync<AiRuntimePoolFailureConflictException>(
                    () =>
                        journal.RecordAsync(
                            observation with
                            {
                                RuntimeInstanceId = "runtime-2",
                                RouteId = "route-runtime-2"
                            }));

            Assert.Equal("failure-idempotent", exception.FailureId);
        }

        [Fact]
        public async Task ListByRuntimeInstanceIdAsync_Should_Not_Leak_Sibling_Failures()
        {
            var journal =
                new MongoAiRuntimePoolFailureJournal(
                    this.database,
                    this.options);

            await journal.RecordAsync(
                RuntimePoolFailureJournalTests.CreateObservation(
                    "failure-runtime-1",
                    "runtime-1"));
            await journal.RecordAsync(
                RuntimePoolFailureJournalTests.CreateObservation(
                    "failure-runtime-2",
                    "runtime-2"));

            var failures =
                await journal.ListByRuntimeInstanceIdAsync("runtime-1");

            var failure = Assert.Single(failures);
            Assert.Equal("failure-runtime-1", failure.FailureId);
            Assert.Equal("runtime-1", failure.RuntimeInstanceId);
        }

        [Fact]
        public async Task ExplicitRegistration_Should_Ignore_Ambient_Database_And_Write_To_Configured_Authority()
        {
            var ambientDatabaseName =
                CreateDatabaseName("ambient");
            var ambientDatabase = this.client.GetDatabase(ambientDatabaseName);
            var ambientCollection =
                ambientDatabase.GetCollection<BsonDocument>(
                    this.options.Value.CollectionName);

            try
            {
                var services = new ServiceCollection();
                services.AddSingleton<IMongoDatabase>(ambientDatabase);
                services.AddMongoAiRuntimePoolFailureJournal(
                    this.connectionString,
                    this.databaseName,
                    options =>
                    {
                        options.CollectionName = this.options.Value.CollectionName;
                        options.EnsureIndexes = false;
                    });

                using var provider = services.BuildServiceProvider();
                var journal =
                    provider.GetRequiredService<IAiRuntimePoolFailureJournal>();

                await journal.RecordAsync(
                    RuntimePoolFailureJournalTests.CreateObservation(
                        "failure-explicit-authority",
                        "runtime-explicit-authority"));

                var authoritativeCount =
                    await this.collection.CountDocumentsAsync(
                        Builders<BsonDocument>.Filter.Eq(
                            "_id",
                            "failure-explicit-authority"));
                var ambientCount =
                    await ambientCollection.CountDocumentsAsync(
                        Builders<BsonDocument>.Filter.Eq(
                            "_id",
                            "failure-explicit-authority"));

                Assert.Equal(1L, authoritativeCount);
                Assert.Equal(0L, ambientCount);
            }
            finally
            {
                await this.client
                    .DropDatabaseAsync(ambientDatabaseName)
                    .ConfigureAwait(false);
            }
        }

        [Fact]
        public async Task RecordAsync_Should_Create_Required_Query_Indexes()
        {
            var journal =
                new MongoAiRuntimePoolFailureJournal(
                    this.database,
                    this.options);

            await journal.RecordAsync(
                RuntimePoolFailureJournalTests.CreateObservation(
                    "failure-indexes",
                    "runtime-indexes"));

            using var cursor = await this.collection.Indexes.ListAsync();
            var documents = await cursor.ToListAsync();
            var names =
                documents
                    .Select(document => document["name"].AsString)
                    .ToArray();

            Assert.Contains("ix_failure_poolId_observedAt_failureId", names);
            Assert.Contains("ix_failure_hostId_observedAt_failureId", names);
            Assert.Contains(
                "ix_failure_runtimeInstanceId_observedAt_failureId",
                names);
        }
    }
}
