using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Execution.Payloads.Redis;
using Multiplexed.Abstractions.AI.Execution.Payloads.Stores;
using StackExchange.Redis;

namespace Multiplexed.AI.Runtime.Execution.Payloads.Redis
{
    /// <summary>
    /// Redis-only payload store.
    ///
    /// PURPOSE:
    /// - Provides a direct Redis-backed payload store.
    /// - Useful for non-replay-safe or temporary payload scenarios.
    ///
    /// DESIGN:
    /// - Stores payload content directly in Redis.
    /// - Applies TTL to all payload entries.
    ///
    /// IMPORTANT:
    /// - This store is not durable.
    /// - This store is not replay-safe after Redis eviction, expiration or flush.
    /// - For production replay-safe payloads, prefer Mongo or Mongo-Redis.
    /// </summary>
    public sealed class RedisAiPayloadStore : IAiPayloadStore
    {
        private readonly IConnectionMultiplexer _redis;
        private readonly RedisAiPayloadCacheOptions _options;

        public RedisAiPayloadStore(
            IConnectionMultiplexer redis,
            IOptions<AiPayloadStoreOptions> options)
        {
            ArgumentNullException.ThrowIfNull(redis);
            ArgumentNullException.ThrowIfNull(options);

            _redis = redis;
            _options = options.Value.RedisCache;
        }

        /// <inheritdoc />
        public async Task<string> SaveAsync(
            string content,
            CancellationToken cancellationToken = default)
        {
            var id = Guid.NewGuid().ToString("N");
            var db = _redis.GetDatabase();

            await db.StringSetAsync(
                    BuildKey(id),
                    content,
                    TimeSpan.FromSeconds(_options.ExpirationSeconds))
                .ConfigureAwait(false);

            return id;
        }

        /// <inheritdoc />
        public async Task<string?> LoadAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            var db = _redis.GetDatabase();

            var value = await db.StringGetAsync(BuildKey(key))
                .ConfigureAwait(false);
            Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics.Record(
                db,
                Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.PayloadRedisLoad,
                "GET",
                value);

            return value.HasValue ? value.ToString() : null;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            var db = _redis.GetDatabase();

            await db.KeyDeleteAsync(BuildKey(key))
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Builds the Redis key used for direct Redis payload storage.
        /// </summary>
        private string BuildKey(string key)
        {
            return $"{_options.KeyPrefix}:{key}";
        }
    }
}