using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.DI.Persistence.Mongo;
using System;

namespace Multiplexed.AI.DI.Persistence
{
    /// <summary>
    /// Registers AI execution snapshot persistence based on the configured engine options.
    /// </summary>
    public static class AiExecutionSnapshotServiceCollectionExtensions
    {
        /// <summary>
        /// Registers execution snapshot persistence services for the configured provider.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="options">The engine options.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddAiExecutionSnapshots(
            this IServiceCollection services,
            AiEngineOptions options)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(options);

            if (!options.Snapshots.Enabled)
            {
                return services;
            }

            if (options.Snapshots.Mongo.Enabled)
            {
                if (string.IsNullOrWhiteSpace(options.Snapshots.Mongo.ConnectionString))
                {
                    throw new InvalidOperationException(
                        "Execution snapshots Mongo provider is enabled, but Snapshots:Mongo:ConnectionString is null or empty.");
                }

                if (string.IsNullOrWhiteSpace(options.Snapshots.Mongo.DatabaseName))
                {
                    throw new InvalidOperationException(
                        "Execution snapshots Mongo provider is enabled, but Snapshots:Mongo:DatabaseName is null or empty.");
                }

                if (string.IsNullOrWhiteSpace(options.Snapshots.Mongo.CollectionName))
                {
                    throw new InvalidOperationException(
                        "Execution snapshots Mongo provider is enabled, but Snapshots:Mongo:CollectionName is null or empty.");
                }

                services.AddMongoAiExecutionSnapshots<ExecutionContextSnapshot>(mongo =>
                {
                    mongo.ConnectionString = options.Snapshots.Mongo.ConnectionString;
                    mongo.DatabaseName = options.Snapshots.Mongo.DatabaseName;
                    mongo.CollectionName = options.Snapshots.Mongo.CollectionName;
                });

                return services;
            }

            throw new InvalidOperationException(
                "Execution snapshots are enabled, but no supported snapshot provider is configured.");
        }
    }
}