using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle
{
    /// <summary>
    /// Provides dependency-injection registration extensions for the runtime lifecycle journal.
    /// </summary>
    public static class AiRuntimeLifecycleJournalServiceCollectionExtensions
    {
        /// <summary>
        /// Adds the in-memory runtime lifecycle journal.
        /// </summary>
        public static IServiceCollection AddInMemoryAiRuntimeLifecycleJournal(
            this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddSingleton<IAiRuntimeLifecycleJournal, InMemoryAiRuntimeLifecycleJournal>();

            return services;
        }

        /// <summary>
        /// Adds the MongoDB runtime lifecycle journal using an existing registered database.
        /// </summary>
        public static IServiceCollection AddMongoAiRuntimeLifecycleJournal(
            this IServiceCollection services,
            Action<AiRuntimeLifecycleJournalMongoOptions>? configureMongo = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (configureMongo is not null)
            {
                services.Configure(configureMongo);
            }
            else
            {
                services.Configure<AiRuntimeLifecycleJournalMongoOptions>(_ => { });
            }

            services.AddSingleton<IAiRuntimeLifecycleJournal, MongoAiRuntimeLifecycleJournal>();

            return services;
        }

        /// <summary>
        /// Adds the MongoDB runtime lifecycle journal and registers MongoDB dependencies.
        /// </summary>
        public static IServiceCollection AddMongoAiRuntimeLifecycleJournal(
            this IServiceCollection services,
            string connectionString,
            string databaseName,
            Action<AiRuntimeLifecycleJournalMongoOptions>? configureMongo = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

            services.Configure<AiRuntimeLifecycleJournalMongoOptions>(options =>
            {
                options.ConnectionString = connectionString;
                options.DatabaseName = databaseName;
                configureMongo?.Invoke(options);
            });

            services.TryAddSingleton<IMongoClient>(_ => new MongoClient(connectionString));
            services.TryAddSingleton(provider =>
            {
                var client = provider.GetRequiredService<IMongoClient>();

                return client.GetDatabase(databaseName);
            });

            return services.AddMongoAiRuntimeLifecycleJournal();
        }
    }
}
