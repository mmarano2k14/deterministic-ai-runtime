using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads.Mongo;
using Multiplexed.Abstractions.AI.Execution.Payloads.Redis;
using Multiplexed.Abstractions.AI.Execution.Payloads.Stores;
using Multiplexed.Abstractions.AI.Execution.Persistence.Snapshot;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.Runtime.Configuration;
using Multiplexed.AI.Runtime.Execution;
using Multiplexed.AI.Tests.Integration.Runtime.Execution.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Integration.Runtime.Execution
{
    public sealed class AiDagExecutionEngineMongoSnapshotIntegrationTests
    {
        private const string ConnectionString = "mongodb://localhost:27017";
        private const string DatabaseName = "multiplexed_ai_tests";
        private const string CollectionName = "ai_execution_engine_snapshots_tests";

        private static AiEngineOptions CreateOptions(bool snapshotsEnabled)
        {
            return new AiEngineOptions
            {
                JsonPipelineDefinitionFilePath = "config\\dag-complex-10-steps.json",
                Snapshots = new AiExecutionSnapshotOptions
                {
                    Enabled = snapshotsEnabled,
                    Mongo = new AiExecutionSnapshotMongoOptions
                    {
                        Enabled = snapshotsEnabled,
                        CollectionName = CollectionName
                    }
                },
                // 🔥 FIX: ensure payload store is configured for all integration tests
                PayloadStore = new AiPayloadStoreOptions
                {
                    Enabled = true,
                    Provider = "mongo-redis",
                    RequireReplaySafePayloads = true,
                    MaxInlineSizeBytes = 512,
                    Mongo = new MongoAiPayloadStoreOptions
                    {
                        Enabled = true,
                        ConnectionString = ConnectionString,
                        DatabaseName = DatabaseName,
                        CollectionName = "payloads_snapshot_tests"
                    },
                    RedisCache = new RedisAiPayloadCacheOptions
                    {
                        Enabled = true,
                        KeyPrefix = "test:ai:payload:snapshot",
                        ExpirationSeconds = 120,
                        MaxCacheablePayloadBytes = 1024 * 1024
                    },
                    StepIndexCache = new RedisAiStepPayloadIndexCacheOptions
                    {
                        Enabled = true,
                        KeyPrefix = "test:ai:step-index:snapshot",
                        ExpirationSeconds = 120,
                        RefreshTtlOnRead = true
                    }
                },
                Cleanup = new AiExecutionCleanupOptions
                {
                    AutoCleanupOnCompleted = false,
                    AutoCleanupOnFailed = false,
                    SuppressCleanupExceptions = true
                }
            };
        }

        [Fact]
        public async Task ExecuteNextAsync_Should_Persist_Terminal_Snapshot_To_Mongo_When_Enabled()
        {
            var options = CreateOptions(snapshotsEnabled: true);

            await using var host = await AiDagExecutionEngineFixture.CreateAsync(
                options,
                mongoConnectionString: ConnectionString,
                mongoDatabaseName: DatabaseName);

            var engine = host.Engine;

            var created = await engine.CreateAsync("dag-complex", "hello");

            AiExecutionRecord result = null!;

            for (var i = 0; i < 50; i++)
            {
                result = await engine.ExecuteNextAsync(created.ExecutionId);

                if (result.IsTerminal)
                {
                    break;
                }
            }

            Assert.NotNull(result);
            Assert.True(result.IsTerminal);

            var snapshotStore =
                host.ServiceProvider.GetRequiredService<IAiExecutionSnapshotStore<ExecutionContextSnapshot>>();

            var snapshot = await snapshotStore.GetAsync(result.ExecutionId);

            Assert.NotNull(snapshot);
            Assert.Equal(result.ExecutionId, snapshot!.ExecutionId);
            Assert.Equal(result.PipelineName, snapshot.PipelineName);
            Assert.Equal(result.Status.ToString(), snapshot.Status);
            Assert.Equal(result.ContextKey, snapshot.ContextKey);
            Assert.NotNull(snapshot.Record);
            Assert.NotNull(snapshot.State);
            Assert.NotEmpty(snapshot.Steps);
            Assert.Equal(snapshot.State.Steps.Count, snapshot.Steps.Count);

            // PERF2-2E contract: State.Steps is the single durable Mongo representation.
            // The public top-level Steps view is rehydrated by the store on read.
            var mongoClient = new MongoClient(ConnectionString);
            var rawCollection = mongoClient
                .GetDatabase(DatabaseName)
                .GetCollection<BsonDocument>(CollectionName);

            var rawSnapshot = await rawCollection
                .Find(Builders<BsonDocument>.Filter.Eq("ExecutionId", result.ExecutionId))
                .FirstOrDefaultAsync();

            Assert.NotNull(rawSnapshot);
            Assert.False(rawSnapshot!.Contains("Steps"));
            Assert.True(rawSnapshot.Contains("State"));
            Assert.True(rawSnapshot["State"].AsBsonDocument.Contains("Steps"));
        }

        [Fact]
        public async Task ExecuteAllAsync_Should_Not_Persist_Snapshot_When_Disabled()
        {
            var options = CreateOptions(snapshotsEnabled: false);

            await using var host = await AiDagExecutionEngineFixture.CreateAsync(
                options,
                mongoConnectionString: ConnectionString,
                mongoDatabaseName: DatabaseName);

            var engine = host.Engine;

            var created = await engine.CreateAsync("dag-complex", "hello");
            var result = await engine.ExecuteAllAsync(created.ExecutionId);

            Assert.True(result.IsTerminal);

            var snapshotStore =
                host.ServiceProvider.GetService<IAiExecutionSnapshotStore<ExecutionContextSnapshot>>();

            if (snapshotStore is not null)
            {
                var snapshot = await snapshotStore.GetAsync(result.ExecutionId);
                Assert.Null(snapshot);
            }
        }
    }
}
