using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.Observability.Performance;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Provides service registration extensions for runtime recovery forensics.
    /// </summary>
    public static class AiRuntimeRecoveryForensicsServiceCollectionExtensions
    {
        /// <summary>
        /// Adds no-op runtime recovery forensics services.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddNoopAiRuntimeRecoveryForensics(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.TryAddSingleton<IAiRuntimeRecoveryForensicsRecorder, NoopAiRuntimeRecoveryForensicsRecorder>();
            AddRecoveryForensicsProjectionSink(services);

            return services;
        }

        /// <summary>
        /// Adds in-memory runtime recovery forensics services.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">The optional options configuration delegate.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddInMemoryAiRuntimeRecoveryForensics(
            this IServiceCollection services,
            Action<AiRuntimeRecoveryForensicsOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (configure is not null)
            {
                services.Configure(configure);
            }
            else
            {
                services.Configure<AiRuntimeRecoveryForensicsOptions>(_ => { });
            }

            services.AddSingleton<IAiRuntimeRecoveryForensicsStore, InMemoryAiRuntimeRecoveryForensicsStore>();
            services.AddSingleton<IAiRuntimeRecoveryForensicsRecorder, BestEffortAiRuntimeRecoveryForensicsRecorder>();
            AddRecoveryForensicsProjectionSink(services);

            return services;
        }

        /// <summary>
        /// Adds MongoDB runtime recovery forensics services using an existing registered MongoDB database.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configureForensics">The optional forensics options configuration delegate.</param>
        /// <param name="configureMongo">The optional MongoDB options configuration delegate.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddMongoAiRuntimeRecoveryForensics(
            this IServiceCollection services,
            Action<AiRuntimeRecoveryForensicsOptions>? configureForensics = null,
            Action<AiRuntimeRecoveryForensicsMongoOptions>? configureMongo = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (configureForensics is not null)
            {
                services.Configure(configureForensics);
            }
            else
            {
                services.Configure<AiRuntimeRecoveryForensicsOptions>(_ => { });
            }

            if (configureMongo is not null)
            {
                services.Configure(configureMongo);
            }
            else
            {
                services.Configure<AiRuntimeRecoveryForensicsMongoOptions>(_ => { });
            }

            services.AddSingleton<IAiRuntimeRecoveryForensicsStore, MongoAiRuntimeRecoveryForensicsStore>();
            services.AddSingleton<IAiRuntimeRecoveryForensicsRecorder, BestEffortAiRuntimeRecoveryForensicsRecorder>();
            AddRecoveryForensicsProjectionSink(services);
            services.AddSingleton<IAiRuntimeRecoveryForensicsQueryService, MongoAiRuntimeRecoveryForensicsQueryService>();

            return services;
        }

        /// <summary>
        /// Adds MongoDB runtime recovery forensics services and registers MongoDB client and database dependencies.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="connectionString">The MongoDB connection string.</param>
        /// <param name="databaseName">The MongoDB database name.</param>
        /// <param name="configureForensics">The optional forensics options configuration delegate.</param>
        /// <param name="configureMongo">The optional MongoDB options configuration delegate.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddMongoAiRuntimeRecoveryForensics(
            this IServiceCollection services,
            string connectionString,
            string databaseName,
            Action<AiRuntimeRecoveryForensicsOptions>? configureForensics = null,
            Action<AiRuntimeRecoveryForensicsMongoOptions>? configureMongo = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

            services.Configure<AiRuntimeRecoveryForensicsMongoOptions>(options =>
            {
                options.ConnectionString = connectionString;
                options.DatabaseName = databaseName;
                configureMongo?.Invoke(options);
            });

            services.TryAddSingleton<IMongoClient>(
                _ => AiMongoAttributionDiagnostics.CreateMongoClient(
                    connectionString,
                    AiMongoAttributionClientRoles.RecoveryForensics));
            services.TryAddSingleton(provider =>
            {
                var client = provider.GetRequiredService<IMongoClient>();

                return client.GetDatabase(databaseName);
            });

            return services.AddMongoAiRuntimeRecoveryForensics(configureForensics, null);
        }
        /// <summary>
        /// Registers the single Event Manager projection sink for runtime recovery forensics.
        /// </summary>
        /// <param name="services">The service collection.</param>
        private static void AddRecoveryForensicsProjectionSink(IServiceCollection services)
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IAiControlPlaneEventSink, RecoveryForensicsAiControlPlaneEventSink>());
        }

    }
}