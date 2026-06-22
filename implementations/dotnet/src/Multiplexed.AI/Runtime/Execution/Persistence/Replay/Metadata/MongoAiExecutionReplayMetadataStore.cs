using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay.Metadata;

namespace Multiplexed.AI.Runtime.Execution.Persistence.Replay.Metadata
{
    /// <summary>
    /// Mongo-backed replay metadata store used when replay metadata must be shared across processes.
    /// </summary>
    public sealed class MongoAiExecutionReplayMetadataStore : IAiExecutionReplayMetadataStore
    {
        private const string DefaultDatabaseName = "multiplexed-ai";
        private const string DefaultCollectionName = "ai_execution_replay_metadata";

        private readonly IMongoCollection<MongoAiExecutionReplayMetadataDocument> collection;

        public MongoAiExecutionReplayMetadataStore(
            IMongoClient mongoClient)
            : this(
                mongoClient,
                DefaultDatabaseName,
                DefaultCollectionName)
        {
        }

        public MongoAiExecutionReplayMetadataStore(
            IMongoClient mongoClient,
            string? databaseName,
            string? collectionName)
        {
            ArgumentNullException.ThrowIfNull(mongoClient);

            var resolvedDatabaseName =
                string.IsNullOrWhiteSpace(databaseName)
                    ? DefaultDatabaseName
                    : databaseName;

            var resolvedCollectionName =
                string.IsNullOrWhiteSpace(collectionName)
                    ? DefaultCollectionName
                    : collectionName;

            this.collection =
                mongoClient
                    .GetDatabase(resolvedDatabaseName)
                    .GetCollection<MongoAiExecutionReplayMetadataDocument>(resolvedCollectionName);
        }

        public async Task<AiExecutionReplayMetadata?> GetAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
            {
                return null;
            }

            var filter =
                Builders<MongoAiExecutionReplayMetadataDocument>
                    .Filter
                    .Eq(document => document.Id, executionId);

            var document =
                await this.collection
                    .Find(filter)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

            return document?.Metadata;
        }

        public async Task SaveAsync(
            AiExecutionReplayMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(metadata);

            if (string.IsNullOrWhiteSpace(metadata.ExecutionId))
            {
                throw new InvalidOperationException(
                    "Replay metadata cannot be saved because ExecutionId is missing.");
            }

            var document =
                new MongoAiExecutionReplayMetadataDocument
                {
                    Id = metadata.ExecutionId,
                    ExecutionId = metadata.ExecutionId,
                    Metadata = metadata,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };

            var filter =
                Builders<MongoAiExecutionReplayMetadataDocument>
                    .Filter
                    .Eq(existing => existing.Id, metadata.ExecutionId);

            await this.collection
                .ReplaceOneAsync(
                    filter,
                    document,
                    new ReplaceOptions
                    {
                        IsUpsert = true
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private sealed class MongoAiExecutionReplayMetadataDocument
        {
            [BsonId]
            public string Id { get; init; } = string.Empty;

            public string ExecutionId { get; init; } = string.Empty;

            public AiExecutionReplayMetadata Metadata { get; init; } = default!;

            public DateTimeOffset UpdatedAtUtc { get; init; }
        }
    }
}