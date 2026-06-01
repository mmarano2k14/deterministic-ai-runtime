using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Execution.Persistence.Snapshot;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.Runtime.Execution.Persistence.Snapshot;
using Multiplexed.AI.Runtime.Execution.Persistence.Snapshot.Mongo;

namespace Multiplexed.AI.DI.Persistence.Mongo
{
    /// <summary>
    /// Registers MongoDB-backed execution snapshot persistence.
    /// </summary>
    public static class AiExecutionSnapshotMongoServiceCollectionExtensions
    {
        /// <summary>
        /// Registers MongoDB-backed execution snapshot services.
        /// </summary>
        /// <typeparam name="TContextSnapshot">
        /// The execution context snapshot type persisted by the snapshot store.
        /// </typeparam>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">The Mongo snapshot configuration delegate.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddMongoAiExecutionSnapshots<TContextSnapshot>(
            this IServiceCollection services,
            Action<AiExecutionSnapshotMongoOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);

            var options = new AiExecutionSnapshotMongoOptions();
            configure(options);

            if (string.IsNullOrWhiteSpace(options.ConnectionString))
            {
                throw new InvalidOperationException(
                    "Mongo snapshot ConnectionString cannot be null or empty.");
            }

            if (string.IsNullOrWhiteSpace(options.DatabaseName))
            {
                throw new InvalidOperationException(
                    "Mongo snapshot DatabaseName cannot be null or empty.");
            }

            if (string.IsNullOrWhiteSpace(options.CollectionName))
            {
                throw new InvalidOperationException(
                    "Mongo snapshot CollectionName cannot be null or empty.");
            }

            services.TryAddSingleton<IAiExecutionSnapshotFactory<TContextSnapshot>, DefaultAiExecutionSnapshotFactory<TContextSnapshot>>();
            services.AddScoped<IAiExecutionSnapshotService<TContextSnapshot>, DefaultAiExecutionSnapshotService<TContextSnapshot>>();

            services.TryAddSingleton<IMongoClient>(_ => new MongoClient(options.ConnectionString));

            services.TryAddSingleton<IMongoDatabase>(sp =>
            {
                var client = sp.GetRequiredService<IMongoClient>();
                return client.GetDatabase(options.DatabaseName);
            });

            services.TryAddSingleton<IAiExecutionSnapshotStore<TContextSnapshot>>(sp =>
            {
                var database = sp.GetRequiredService<IMongoDatabase>();
                var logger = sp.GetRequiredService<ILogger<MongoAiExecutionSnapshotStore<TContextSnapshot>>>();

                return new MongoAiExecutionSnapshotStore<TContextSnapshot>(
                    database,
                    options,
                    logger);
            });

            return services;
        }
    }
}