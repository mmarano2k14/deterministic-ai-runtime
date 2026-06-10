using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Redis;
using Multiplexed.AI.Runtime.ControlPlane.Admission.Reservations;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue.Redis;
using StackExchange.Redis;

namespace Multiplexed.AI.Runtime.ControlPlane.DI
{
    /// <summary>
    /// Provides Redis-backed dependency injection registration for AI runtime control-plane services.
    /// </summary>
    public static class AiControlPlaneRedisServiceCollectionExtensions
    {
        /// <summary>
        /// Replaces the default in-memory shared run store with the Redis-backed shared run store.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">Optional Redis shared run store options configuration.</param>
        /// <returns>The same service collection for chaining.</returns>
        /// <remarks>
        /// This method does not register the Redis connection itself.
        /// The application must already register <see cref="IConnectionMultiplexer"/>.
        ///
        /// Expected usage:
        ///
        /// services.AddSingleton&lt;IConnectionMultiplexer&gt;(
        ///     _ =&gt; ConnectionMultiplexer.Connect("localhost:6379"));
        ///
        /// services.AddAiControlPlane();
        /// services.AddRedisAiSharedRunStore();
        ///
        /// The default <see cref="IAiSharedRunStore"/> registered by AddAiControlPlane
        /// is replaced by <see cref="RedisAiSharedRunStore"/>.
        /// </remarks>
        public static IServiceCollection AddRedisAiSharedRunStore(
            this IServiceCollection services,
            Action<RedisAiSharedRunStoreOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (configure is null)
            {
                services.AddOptions<RedisAiSharedRunStoreOptions>();
            }
            else
            {
                services.Configure(configure);
            }

            services.RemoveAll<IAiSharedRunStore>();
            services.TryAddSingleton<IAiSharedRunStore, RedisAiSharedRunStore>();

            return services;
        }

        /// <summary>
        /// Registers a Redis connection multiplexer and replaces the default in-memory
        /// shared run store with the Redis-backed shared run store.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="connectionString">The Redis connection string.</param>
        /// <param name="configure">Optional Redis shared run store options configuration.</param>
        /// <returns>The same service collection for chaining.</returns>
        /// <remarks>
        /// This overload is convenient for demos, tests, and simple host setups.
        /// Larger applications may prefer to register <see cref="IConnectionMultiplexer"/>
        /// themselves and call <see cref="AddRedisAiSharedRunStore(IServiceCollection, Action{RedisAiSharedRunStoreOptions}?)"/>.
        /// </remarks>
        public static IServiceCollection AddRedisAiSharedRunStore(
            this IServiceCollection services,
            string connectionString,
            Action<RedisAiSharedRunStoreOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException(
                    "Redis connection string cannot be null or empty.",
                    nameof(connectionString));
            }

            services.TryAddSingleton<IConnectionMultiplexer>(
                _ => ConnectionMultiplexer.Connect(connectionString));

            return services.AddRedisAiSharedRunStore(configure);
        }

        /// <summary>
        /// Replaces the default in-memory shared queue with the Redis-backed shared queue.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">Optional Redis shared queue options configuration.</param>
        /// <returns>The same service collection for chaining.</returns>
        /// <remarks>
        /// This method does not register the Redis connection itself.
        /// The application must already register <see cref="IConnectionMultiplexer"/>.
        ///
        /// Expected usage:
        ///
        /// services.AddSingleton&lt;IConnectionMultiplexer&gt;(
        ///     _ =&gt; ConnectionMultiplexer.Connect("localhost:6379"));
        ///
        /// services.AddAiControlPlane();
        /// services.AddRedisAiSharedQueue();
        ///
        /// The default <see cref="IAiSharedQueue"/> registered by AddAiControlPlane
        /// is replaced by <see cref="RedisAiSharedQueue"/>.
        /// </remarks>
        public static IServiceCollection AddRedisAiSharedQueue(
            this IServiceCollection services,
            Action<RedisAiSharedQueueOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (configure is null)
            {
                services.AddOptions<RedisAiSharedQueueOptions>();
            }
            else
            {
                services.Configure(configure);
            }

            services.RemoveAll<IAiSharedQueue>();
            services.TryAddSingleton<IAiSharedQueue, RedisAiSharedQueue>();

            return services;
        }

        /// <summary>
        /// Registers a Redis connection multiplexer and replaces the default in-memory
        /// shared queue with the Redis-backed shared queue.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="connectionString">The Redis connection string.</param>
        /// <param name="configure">Optional Redis shared queue options configuration.</param>
        /// <returns>The same service collection for chaining.</returns>
        /// <remarks>
        /// This overload is convenient for demos, tests, and simple host setups.
        /// Larger applications may prefer to register <see cref="IConnectionMultiplexer"/>
        /// themselves and call <see cref="AddRedisAiSharedQueue(IServiceCollection, Action{RedisAiSharedQueueOptions}?)"/>.
        /// </remarks>
        public static IServiceCollection AddRedisAiSharedQueue(
            this IServiceCollection services,
            string connectionString,
            Action<RedisAiSharedQueueOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException(
                    "Redis connection string cannot be null or empty.",
                    nameof(connectionString));
            }

            services.TryAddSingleton<IConnectionMultiplexer>(
                _ => ConnectionMultiplexer.Connect(connectionString));

            return services.AddRedisAiSharedQueue(configure);
        }

        /// <summary>
        /// Replaces the default in-memory runtime admission reservation store
        /// with the Redis-backed runtime admission reservation store.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">Optional Redis admission reservation options configuration.</param>
        /// <returns>The same service collection for chaining.</returns>
        /// <remarks>
        /// This method does not register the Redis connection itself.
        /// The application must already register <see cref="IConnectionMultiplexer"/>.
        ///
        /// Expected usage:
        ///
        /// services.AddSingleton&lt;IConnectionMultiplexer&gt;(
        ///     _ =&gt; ConnectionMultiplexer.Connect("localhost:6379"));
        ///
        /// services.AddAiControlPlane();
        /// services.AddRedisAiRuntimeAdmissionReservationStore();
        ///
        /// The default <see cref="IAiRuntimeAdmissionReservationStore"/> registered by AddAiControlPlane
        /// is replaced by <see cref="RedisAiRuntimeAdmissionReservationStore"/>.
        /// </remarks>
        public static IServiceCollection AddRedisAiRuntimeAdmissionReservationStore(
            this IServiceCollection services,
            Action<AiRuntimeAdmissionReservationRedisOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (configure is null)
            {
                services.AddOptions<AiRuntimeAdmissionReservationRedisOptions>();
            }
            else
            {
                services.Configure(configure);
            }

            services.RemoveAll<IAiRuntimeAdmissionReservationStore>();
            services.TryAddSingleton<IAiRuntimeAdmissionReservationStore, RedisAiRuntimeAdmissionReservationStore>();

            return services;
        }

        /// <summary>
        /// Registers a Redis connection multiplexer and replaces the default in-memory
        /// runtime admission reservation store with the Redis-backed runtime admission reservation store.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="connectionString">The Redis connection string.</param>
        /// <param name="configure">Optional Redis admission reservation options configuration.</param>
        /// <returns>The same service collection for chaining.</returns>
        /// <remarks>
        /// This overload is convenient for demos, tests, and simple host setups.
        /// Larger applications may prefer to register <see cref="IConnectionMultiplexer"/>
        /// themselves and call <see cref="AddRedisAiRuntimeAdmissionReservationStore(IServiceCollection, Action{AiRuntimeAdmissionReservationRedisOptions}?)"/>.
        /// </remarks>
        public static IServiceCollection AddRedisAiRuntimeAdmissionReservationStore(
            this IServiceCollection services,
            string connectionString,
            Action<AiRuntimeAdmissionReservationRedisOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException(
                    "Redis connection string cannot be null or empty.",
                    nameof(connectionString));
            }

            services.TryAddSingleton<IConnectionMultiplexer>(
                _ => ConnectionMultiplexer.Connect(connectionString));

            return services.AddRedisAiRuntimeAdmissionReservationStore(configure);
        }

        /// <summary>
        /// Replaces all default in-memory Redis-capable control-plane stores with Redis-backed implementations.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configureSharedRunStore">Optional Redis shared run store options configuration.</param>
        /// <param name="configureSharedQueue">Optional Redis shared queue options configuration.</param>
        /// <param name="configureAdmissionReservations">Optional Redis admission reservation options configuration.</param>
        /// <returns>The same service collection for chaining.</returns>
        /// <remarks>
        /// This method does not register the Redis connection itself.
        /// The application must already register <see cref="IConnectionMultiplexer"/>.
        ///
        /// It replaces:
        /// - <see cref="IAiSharedRunStore"/>
        /// - <see cref="IAiSharedQueue"/>
        /// - <see cref="IAiRuntimeAdmissionReservationStore"/>
        /// </remarks>
        public static IServiceCollection AddRedisAiControlPlaneStores(
            this IServiceCollection services,
            Action<RedisAiSharedRunStoreOptions>? configureSharedRunStore = null,
            Action<RedisAiSharedQueueOptions>? configureSharedQueue = null,
            Action<AiRuntimeAdmissionReservationRedisOptions>? configureAdmissionReservations = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddRedisAiSharedRunStore(configureSharedRunStore);
            services.AddRedisAiSharedQueue(configureSharedQueue);
            services.AddRedisAiRuntimeAdmissionReservationStore(configureAdmissionReservations);

            return services;
        }

        /// <summary>
        /// Registers a Redis connection multiplexer and replaces all default in-memory
        /// Redis-capable control-plane stores with Redis-backed implementations.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="connectionString">The Redis connection string.</param>
        /// <param name="configureSharedRunStore">Optional Redis shared run store options configuration.</param>
        /// <param name="configureSharedQueue">Optional Redis shared queue options configuration.</param>
        /// <param name="configureAdmissionReservations">Optional Redis admission reservation options configuration.</param>
        /// <returns>The same service collection for chaining.</returns>
        public static IServiceCollection AddRedisAiControlPlaneStores(
            this IServiceCollection services,
            string connectionString,
            Action<RedisAiSharedRunStoreOptions>? configureSharedRunStore = null,
            Action<RedisAiSharedQueueOptions>? configureSharedQueue = null,
            Action<AiRuntimeAdmissionReservationRedisOptions>? configureAdmissionReservations = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException(
                    "Redis connection string cannot be null or empty.",
                    nameof(connectionString));
            }

            services.TryAddSingleton<IConnectionMultiplexer>(
                _ => ConnectionMultiplexer.Connect(connectionString));

            return services.AddRedisAiControlPlaneStores(
                configureSharedRunStore,
                configureSharedQueue,
                configureAdmissionReservations);
        }
    }
}