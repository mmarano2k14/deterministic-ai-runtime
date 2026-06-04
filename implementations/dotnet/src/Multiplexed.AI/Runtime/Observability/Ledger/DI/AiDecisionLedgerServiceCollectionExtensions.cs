using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Observability.Ledger;
using Multiplexed.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Runtime.Observability.Context;
using Multiplexed.AI.Runtime.Observability.Ledger.Mongo;

namespace Multiplexed.AI.Runtime.Observability.Ledger.DI
{
    /// <summary>
    /// Provides dependency injection registration helpers for the AI decision ledger.
    /// </summary>
    public static class AiDecisionLedgerServiceCollectionExtensions
    {
        /// <summary>
        /// Adds the AI decision ledger services using default best-effort no-op storage.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddAiDecisionLedger(
            this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            return services.AddAiDecisionLedger(options =>
            {
                options.WriteMode = AiDecisionLedgerWriteMode.BestEffort;
                options.StorageMode = AiDecisionLedgerStorageMode.None;
            });
        }

        /// <summary>
        /// Adds the AI decision ledger services using the specified recorder options.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">The recorder options configuration delegate.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddAiDecisionLedger(
            this IServiceCollection services,
            Action<AiDecisionLedgerRecorderOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);

            services.Configure(configure);

            var options = new AiDecisionLedgerRecorderOptions();
            configure(options);

            RegisterRuntimeCorrelationDependencies(services);
            RegisterLedgerStorage(services, options.StorageMode);
            RegisterLedgerRecorder(services, options.WriteMode);

            return services;
        }

        /// <summary>
        /// Adds an in-memory AI decision ledger intended for tests and local demos.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddInMemoryAiDecisionLedger(
            this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            return services.AddAiDecisionLedger(options =>
            {
                options.WriteMode = AiDecisionLedgerWriteMode.BestEffort;
                options.StorageMode = AiDecisionLedgerStorageMode.InMemory;
            });
        }

        /// <summary>
        /// Adds a MongoDB-backed AI decision ledger.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configureMongo">The MongoDB decision ledger options configuration delegate.</param>
        /// <returns>The updated service collection.</returns>
        /// <remarks>
        /// This method requires an <c>IMongoClient</c> registration to already exist in the service collection.
        /// </remarks>
        public static IServiceCollection AddMongoAiDecisionLedger(
            this IServiceCollection services,
            Action<MongoAiDecisionLedgerOptions> configureMongo)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configureMongo);

            services.Configure(configureMongo);

            return services.AddAiDecisionLedger(options =>
            {
                options.WriteMode = AiDecisionLedgerWriteMode.BestEffort;
                options.StorageMode = AiDecisionLedgerStorageMode.Mongo;
            });
        }

        /// <summary>
        /// Adds a MongoDB-backed AI decision ledger using the specified write mode.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="writeMode">The decision ledger write mode.</param>
        /// <param name="configureMongo">The MongoDB decision ledger options configuration delegate.</param>
        /// <returns>The updated service collection.</returns>
        /// <remarks>
        /// This method requires an <c>IMongoClient</c> registration to already exist in the service collection.
        /// </remarks>
        public static IServiceCollection AddMongoAiDecisionLedger(
            this IServiceCollection services,
            AiDecisionLedgerWriteMode writeMode,
            Action<MongoAiDecisionLedgerOptions> configureMongo)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configureMongo);

            services.Configure(configureMongo);

            return services.AddAiDecisionLedger(options =>
            {
                options.WriteMode = writeMode;
                options.StorageMode = AiDecisionLedgerStorageMode.Mongo;
            });
        }

        /// <summary>
        /// Adds a disabled AI decision ledger.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddDisabledAiDecisionLedger(
            this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            return services.AddAiDecisionLedger(options =>
            {
                options.WriteMode = AiDecisionLedgerWriteMode.Disabled;
                options.StorageMode = AiDecisionLedgerStorageMode.None;
            });
        }

        /// <summary>
        /// Registers the minimal runtime correlation dependencies required by the
        /// default decision ledger recorder.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <remarks>
        /// <para>
        /// The decision ledger extension can be used standalone in tests or lightweight
        /// hosts, without registering the full AI runtime module.
        /// </para>
        ///
        /// <para>
        /// Since <see cref="DefaultAiDecisionLedgerRecorder"/> can enrich ledger entries
        /// from the ambient runtime correlation context, this method registers the minimal
        /// passive correlation services needed by the recorder.
        /// </para>
        ///
        /// <para>
        /// These registrations use <c>TryAddSingleton</c>, so they do not override
        /// registrations provided by the full runtime composition.
        /// </para>
        /// </remarks>
        private static void RegisterRuntimeCorrelationDependencies(
            IServiceCollection services)
        {
            //services.TryAddSingleton<IAiRuntimeInstanceIdentity, DefaultAiRuntimeInstanceIdentity>();
            services.TryAddSingleton<IAiRuntimeCorrelationAccessor, AsyncLocalAiRuntimeCorrelationAccessor>();
        }

        /// <summary>
        /// Registers the configured decision ledger storage implementation.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="storageMode">The ledger storage mode.</param>
        private static void RegisterLedgerStorage(
            IServiceCollection services,
            AiDecisionLedgerStorageMode storageMode)
        {
            switch (storageMode)
            {
                case AiDecisionLedgerStorageMode.None:
                    services.TryAddSingleton<IAiDecisionLedger, NoOpAiDecisionLedger>();
                    break;

                case AiDecisionLedgerStorageMode.InMemory:
                    services.TryAddSingleton<IAiDecisionLedger, InMemoryAiDecisionLedger>();
                    break;

                case AiDecisionLedgerStorageMode.Mongo:
                    services.TryAddSingleton<IAiDecisionLedger, MongoAiDecisionLedger>();
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(storageMode),
                        storageMode,
                        "Unsupported AI decision ledger storage mode.");
            }
        }

        /// <summary>
        /// Registers the configured decision ledger recorder implementation.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="writeMode">The ledger write mode.</param>
        private static void RegisterLedgerRecorder(
            IServiceCollection services,
            AiDecisionLedgerWriteMode writeMode)
        {
            if (writeMode == AiDecisionLedgerWriteMode.Disabled)
            {
                services.TryAddSingleton<IAiDecisionLedgerRecorder, NoOpAiDecisionLedgerRecorder>();
                return;
            }

            services.TryAddSingleton<IAiDecisionLedgerRecorder, DefaultAiDecisionLedgerRecorder>();
        }
    }
}