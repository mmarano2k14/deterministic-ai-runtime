using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure
{
    /// <summary>
    /// Registers the authoritative runtime-pool failure journal implementation.
    /// </summary>
    public static class AiRuntimePoolFailureJournalServiceCollectionExtensions
    {
        public static IServiceCollection AddInMemoryAiRuntimePoolFailureJournal(
            this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddSingleton<
                IAiRuntimePoolFailureJournal,
                InMemoryAiRuntimePoolFailureJournal>();

            return services;
        }

        public static IServiceCollection AddMongoAiRuntimePoolFailureJournal(
            this IServiceCollection services,
            Action<AiRuntimePoolFailureJournalMongoOptions>? configureMongo = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (configureMongo is not null)
            {
                services.Configure(configureMongo);
            }
            else
            {
                services.Configure<AiRuntimePoolFailureJournalMongoOptions>(_ => { });
            }

            services.AddSingleton<
                IAiRuntimePoolFailureJournal,
                MongoAiRuntimePoolFailureJournal>();

            return services;
        }

        public static IServiceCollection AddMongoAiRuntimePoolFailureJournal(
            this IServiceCollection services,
            string connectionString,
            string databaseName,
            Action<AiRuntimePoolFailureJournalMongoOptions>? configureMongo = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

            services.Configure<AiRuntimePoolFailureJournalMongoOptions>(options =>
            {
                options.ConnectionString = connectionString;
                options.DatabaseName = databaseName;
                configureMongo?.Invoke(options);
            });

            /*
             * The failure journal must not consume the process-wide IMongoDatabase registration.
             * Snapshot, lifecycle, replay, and forensics persistence can legitimately register a
             * different Mongo database earlier in the same process. Binding through that ambient
             * service would split one physical pool failure across different databases in the
             * parent ProcessHost and its control plane.
             *
             * Capture the exact configured failure-authority database here instead. Both hosts
             * therefore converge on ConnectionString + DatabaseName + CollectionName regardless
             * of unrelated Mongo registrations.
             */
            var authoritativeDatabase =
                new MongoClient(connectionString.Trim())
                    .GetDatabase(databaseName.Trim());

            services.AddSingleton<IAiRuntimePoolFailureJournal>(
                serviceProvider =>
                    new MongoAiRuntimePoolFailureJournal(
                        authoritativeDatabase,
                        serviceProvider.GetRequiredService<
                            IOptions<AiRuntimePoolFailureJournalMongoOptions>>()));

            return services;
        }
    }
}
