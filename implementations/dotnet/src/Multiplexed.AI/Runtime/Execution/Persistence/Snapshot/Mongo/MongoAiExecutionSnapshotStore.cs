using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Execution.Persistence.Snapshot;
using Multiplexed.AI.Configuration;

namespace Multiplexed.AI.Runtime.Execution.Persistence.Snapshot.Mongo
{
    /// <summary>
    /// MongoDB implementation of <see cref="IAiExecutionSnapshotStore{TContextSnapshot}"/>.
    /// </summary>
    /// <typeparam name="TContextSnapshot">
    /// The serializable external context snapshot type associated with the execution.
    /// </typeparam>
    public sealed class MongoAiExecutionSnapshotStore<TContextSnapshot>
        : IAiExecutionSnapshotStore<TContextSnapshot>
    {
        private readonly IMongoCollection<AiExecutionSnapshotDocument<TContextSnapshot>> _collection;
        private readonly ILogger<MongoAiExecutionSnapshotStore<TContextSnapshot>> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="MongoAiExecutionSnapshotStore{TContextSnapshot}"/> class.
        /// </summary>
        public MongoAiExecutionSnapshotStore(
            IMongoDatabase database,
            AiExecutionSnapshotMongoOptions options,
            ILogger<MongoAiExecutionSnapshotStore<TContextSnapshot>> logger)
        {
            ArgumentNullException.ThrowIfNull(database);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);

            if (string.IsNullOrWhiteSpace(options.CollectionName))
            {
                throw new InvalidOperationException(
                    "AI execution snapshot Mongo collection name cannot be null or empty.");
            }

            _collection = database.GetCollection<AiExecutionSnapshotDocument<TContextSnapshot>>(
                options.CollectionName);

            _logger = logger;

            Console.WriteLine(
                $"[MONGO SNAPSHOT STORE] " +
                $"Database='{database.DatabaseNamespace.DatabaseName}', " +
                $"Collection='{options.CollectionName}', " +
                $"ContextType='{typeof(TContextSnapshot).FullName}'.");
        }

        /// <inheritdoc />
        public async Task UpsertAsync(
            AiExecutionSnapshotDocument<TContextSnapshot> snapshot,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.ExecutionId);

            var utcNow = DateTime.UtcNow;
            snapshot.UpdatedAtUtc = utcNow;

            var filter = Builders<AiExecutionSnapshotDocument<TContextSnapshot>>
                .Filter
                .Eq(x => x.ExecutionId, snapshot.ExecutionId);

            var update = Builders<AiExecutionSnapshotDocument<TContextSnapshot>>
                .Update
                .SetOnInsert(x => x.ExecutionId, snapshot.ExecutionId)
                .SetOnInsert(x => x.CreatedAtUtc, snapshot.CreatedAtUtc)
                .Set(x => x.PipelineName, snapshot.PipelineName)
                .Set(x => x.Status, snapshot.Status)
                .Set(x => x.ContextKey, snapshot.ContextKey)
                .Set(x => x.ContextSnapshot, snapshot.ContextSnapshot)
                .Set(x => x.UpdatedAtUtc, snapshot.UpdatedAtUtc)
                .Set(x => x.CompletedAtUtc, snapshot.CompletedAtUtc)
                .Set(x => x.Record, snapshot.Record)
                .Set(x => x.State, snapshot.State)
                .Set(x => x.Steps, snapshot.Steps)
                .Set(x => x.Events, snapshot.Events);

            Console.WriteLine(
                $"[MONGO SNAPSHOT STORE UPSERT] " +
                $"Database='{_collection.Database.DatabaseNamespace.DatabaseName}', " +
                $"Collection='{_collection.CollectionNamespace.CollectionName}', " +
                $"ExecutionId='{snapshot.ExecutionId}', " +
                $"Status='{snapshot.Status}'.");

            try
            {
                var result = await _collection.UpdateOneAsync(
                        filter,
                        update,
                        new UpdateOptions { IsUpsert = true },
                        cancellationToken)
                    .ConfigureAwait(false);

                Console.WriteLine(
                    $"[MONGO SNAPSHOT STORE UPSERT RESULT] " +
                    $"ExecutionId='{snapshot.ExecutionId}', " +
                    $"MatchedCount='{result.MatchedCount}', " +
                    $"ModifiedCount='{result.ModifiedCount}', " +
                    $"UpsertedId='{result.UpsertedId?.ToString() ?? string.Empty}'.");

                _logger.LogDebug(
                    "AI execution snapshot upserted for execution {ExecutionId}.",
                    snapshot.ExecutionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to upsert AI execution snapshot for execution {ExecutionId}.",
                    snapshot.ExecutionId);

                _logger.LogError(
                    ex,
                    "Failed to upsert AI execution snapshot for execution {ExecutionId}. Error={Error}",
                    snapshot.ExecutionId,
                    ex.ToString());

                Console.WriteLine(ex.ToString());

                throw;
            }
        }

        /// <inheritdoc />
        public async Task<AiExecutionSnapshotDocument<TContextSnapshot>?> GetAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            var filter = Builders<AiExecutionSnapshotDocument<TContextSnapshot>>
                .Filter
                .Eq(x => x.ExecutionId, executionId);

            Console.WriteLine(
                $"[MONGO SNAPSHOT STORE GET] " +
                $"Database='{_collection.Database.DatabaseNamespace.DatabaseName}', " +
                $"Collection='{_collection.CollectionNamespace.CollectionName}', " +
                $"ExecutionId='{executionId}', " +
                $"ContextType='{typeof(TContextSnapshot).FullName}'.");

            try
            {
                var snapshot = await _collection
                    .Find(filter)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                Console.WriteLine(
                    $"[MONGO SNAPSHOT STORE GET RESULT] " +
                    $"Database='{_collection.Database.DatabaseNamespace.DatabaseName}', " +
                    $"Collection='{_collection.CollectionNamespace.CollectionName}', " +
                    $"ExecutionId='{executionId}', " +
                    $"Found='{snapshot is not null}', " +
                    $"SnapshotExecutionId='{snapshot?.ExecutionId ?? string.Empty}', " +
                    $"SnapshotStatus='{snapshot?.Status.ToString() ?? string.Empty}'.");

                return snapshot;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to load AI execution snapshot for execution {ExecutionId}.",
                    executionId);

                Console.WriteLine(
                    $"[MONGO SNAPSHOT STORE GET ERROR] " +
                    $"Database='{_collection.Database.DatabaseNamespace.DatabaseName}', " +
                    $"Collection='{_collection.CollectionNamespace.CollectionName}', " +
                    $"ExecutionId='{executionId}', " +
                    $"Error='{ex}'.");

                throw;
            }
        }

        /// <inheritdoc />
        public async Task DeleteAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            var filter = Builders<AiExecutionSnapshotDocument<TContextSnapshot>>
                .Filter
                .Eq(x => x.ExecutionId, executionId);

            try
            {
                await _collection
                    .DeleteOneAsync(filter, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogDebug(
                    "AI execution snapshot deleted for execution {ExecutionId}.",
                    executionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to delete AI execution snapshot for execution {ExecutionId}.",
                    executionId);

                throw;
            }
        }
    }
}
