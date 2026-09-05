using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Execution.Persistence.Snapshot;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.Runtime.Execution.Persistence.Snapshot;
using Multiplexed.AI.Runtime.Execution.Persistence.Snapshot.Mongo;
using Multiplexed.AI.Runtime.Observability.Performance;

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

            services.TryAddSingleton(options);

            services.TryAddSingleton<IMongoClient>(
                _ => AiMongoAttributionDiagnostics.CreateMongoClient(
                    options.ConnectionString,
                    AiMongoAttributionClientRoles.Snapshot));

            services.TryAddSingleton<IMongoDatabase>(sp =>
            {
                var client = sp.GetRequiredService<IMongoClient>();
                return client.GetDatabase(options.DatabaseName);
            });

            services.TryAddSingleton<IAiExecutionSnapshotFactory<TContextSnapshot>, DefaultAiExecutionSnapshotFactory<TContextSnapshot>>();
            services.AddScoped<IAiExecutionSnapshotService<TContextSnapshot>, DefaultAiExecutionSnapshotService<TContextSnapshot>>();

            services.AddSingleton<IAiExecutionSnapshotMongoDatabaseProvider>(
                new AiExecutionSnapshotMongoDatabaseProvider(
                    options.ConnectionString,
                    options.DatabaseName));

            services.AddSingleton<IAiExecutionSnapshotStore<TContextSnapshot>>(sp =>
            {
                var databaseProvider = sp.GetRequiredService<IAiExecutionSnapshotMongoDatabaseProvider>();
                var logger = sp.GetRequiredService<ILogger<MongoAiExecutionSnapshotStore<TContextSnapshot>>>();

                return new MongoAiExecutionSnapshotStore<TContextSnapshot>(
                    databaseProvider.Database,
                    options,
                    logger);
            });

            return services;
        }

        /// <summary>
        /// Provides the dedicated MongoDB database used by execution snapshots.
        /// </summary>
        private interface IAiExecutionSnapshotMongoDatabaseProvider
        {
            /// <summary>
            /// Gets the MongoDB database used by execution snapshots.
            /// </summary>
            IMongoDatabase Database { get; }
        }

        /// <summary>
        /// Default dedicated MongoDB database provider for execution snapshots.
        /// </summary>
        private sealed class AiExecutionSnapshotMongoDatabaseProvider : IAiExecutionSnapshotMongoDatabaseProvider
        {
            private readonly MongoClient client;
            private readonly string databaseName;

            /// <summary>
            /// Initializes a new instance of the <see cref="AiExecutionSnapshotMongoDatabaseProvider"/> class.
            /// </summary>
            /// <param name="connectionString">The MongoDB connection string.</param>
            /// <param name="databaseName">The MongoDB database name.</param>
            public AiExecutionSnapshotMongoDatabaseProvider(
                string connectionString,
                string databaseName)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
                ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

                this.client = AiMongoAttributionDiagnostics.CreateMongoClient(
                    connectionString,
                    AiMongoAttributionClientRoles.Snapshot);
                this.databaseName = databaseName;
            }

            /// <inheritdoc />
            public IMongoDatabase Database =>
                this.client.GetDatabase(this.databaseName);
        }
    }
}