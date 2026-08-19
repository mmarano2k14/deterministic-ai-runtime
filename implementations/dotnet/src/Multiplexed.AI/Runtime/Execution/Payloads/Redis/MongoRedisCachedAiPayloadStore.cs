using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Execution.Payloads.Metrics;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Payloads.Stores;
using Multiplexed.AI.Runtime.Execution.Payloads.Mongo.Stores;
using StackExchange.Redis;

namespace Multiplexed.AI.Runtime.Execution.Payloads.Redis
{
    /// <summary>
    /// Mongo-backed Redis cached payload store.
    ///
    /// PURPOSE:
    /// - Uses MongoDB as the durable source of truth.
    /// - Uses Redis as a bounded cache for faster active execution reads.
    /// - Provides replay-safe payload storage while reducing MongoDB read pressure.
    ///
    /// DESIGN:
    /// - Uses composition over inheritance.
    /// - Reuses <see cref="RedisCachedAiPayloadStore"/> as the cache decorator.
    /// - Does not duplicate Redis cache logic.
    ///
    /// IMPORTANT:
    /// - MongoDB remains required for durability and replay safety.
    /// - Redis may be cleared without losing payload recoverability.
    /// </summary>
    public sealed class MongoRedisCachedAiPayloadStore : IAiImmutablePayloadStore
    {
        private readonly MongoAiPayloadStore _mongoStore;
        private readonly RedisCachedAiPayloadStore _cachedStore;

        public MongoRedisCachedAiPayloadStore(
            MongoAiPayloadStore mongoStore,
            IConnectionMultiplexer redis,
            IOptions<AiPayloadStoreOptions> options,
            IAiPayloadMetrics payloadMetrics)
        {
            ArgumentNullException.ThrowIfNull(mongoStore);
            ArgumentNullException.ThrowIfNull(redis);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(payloadMetrics);

            _mongoStore = mongoStore;
            _cachedStore = new RedisCachedAiPayloadStore(
                mongoStore,
                redis,
                options,
                payloadMetrics);
        }

        /// <inheritdoc />
        public Task<string> SaveAsync(
            string content,
            CancellationToken cancellationToken = default)
        {
            return _cachedStore.SaveAsync(content, cancellationToken);
        }

        /// <inheritdoc />
        public Task<string> SaveImmutableAsync(
            string key,
            string content,
            AiPayloadMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            return _mongoStore.SaveImmutableAsync(
                key,
                content,
                metadata,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<string?> LoadAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            return _cachedStore.LoadAsync(key, cancellationToken);
        }

        /// <inheritdoc />
        public Task DeleteAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            return _cachedStore.DeleteAsync(key, cancellationToken);
        }
    }
}