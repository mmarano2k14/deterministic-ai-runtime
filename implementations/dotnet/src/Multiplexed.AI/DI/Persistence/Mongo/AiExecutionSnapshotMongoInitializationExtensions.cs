using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Execution.Persistence.Snapshot;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.Runtime.Execution.Persistence.Snapshot.Mongo;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.DI.Persistence.Mongo
{
    /// <summary>
    /// Provides initialization helpers for MongoDB-backed AI execution snapshot persistence.
    /// </summary>
    public static class AiExecutionSnapshotMongoInitializationExtensions
    {
        /// <summary>
        /// Ensures required MongoDB indexes exist for AI execution snapshots.
        /// </summary>
        /// <typeparam name="TContextSnapshot">
        /// The serializable external context snapshot type associated with the execution.
        /// </typeparam>
        /// <param name="serviceProvider">The root service provider.</param>
        /// <param name="options">The Mongo snapshot options.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public static async Task EnsureMongoAiExecutionSnapshotIndexesAsync<TContextSnapshot>(
            this IServiceProvider serviceProvider,
            AiExecutionSnapshotMongoOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(serviceProvider);
            ArgumentNullException.ThrowIfNull(options);

            if (string.IsNullOrWhiteSpace(options.ConnectionString))
            {
                throw new InvalidOperationException(
                    "AI execution snapshot Mongo connection string cannot be null or empty.");
            }

            if (string.IsNullOrWhiteSpace(options.DatabaseName))
            {
                throw new InvalidOperationException(
                    "AI execution snapshot Mongo database name cannot be null or empty.");
            }

            if (string.IsNullOrWhiteSpace(options.CollectionName))
            {
                throw new InvalidOperationException(
                    "AI execution snapshot Mongo collection name cannot be null or empty.");
            }

            var client = new MongoClient(options.ConnectionString);
            var database = client.GetDatabase(options.DatabaseName);

            var collection = database.GetCollection<AiExecutionSnapshotDocument<TContextSnapshot>>(
                options.CollectionName);

            await MongoAiExecutionSnapshotIndexes.EnsureCreatedAsync(
                collection,
                cancellationToken);
        }
    }
}