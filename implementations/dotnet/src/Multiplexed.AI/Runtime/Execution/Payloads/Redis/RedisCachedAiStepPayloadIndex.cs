using System.Text.Json;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Payloads.Redis;
using Multiplexed.Abstractions.AI.Execution.Payloads.Stores;
using StackExchange.Redis;

namespace Multiplexed.AI.Runtime.Execution.Payloads.Redis
{
    /// <summary>
    /// Redis-backed cache for archived step payload index entries.
    ///
    /// PURPOSE:
    /// - Avoid Mongo reads for frequent dependency/convergence checks.
    /// - Keep archived step lookup fast during DAG execution.
    /// - Support batch read/write for large DAG retention scenarios.
    ///
    /// PERFORMANCE:
    /// - GetManyAsync uses Redis MGET.
    /// - SetManyAsync uses Redis batch/pipeline.
    /// - TTL refresh uses Redis batch/pipeline.
    ///
    /// IMPORTANT:
    /// - Redis is only a cache.
    /// - Mongo remains the durable source of truth.
    /// - Missing Redis entries must never be treated as missing durable data.
    /// </summary>
    public sealed class RedisCachedAiStepPayloadIndex : IAiStepPayloadIndexCache
    {
        private readonly IConnectionMultiplexer _redis;
        private readonly RedisAiStepPayloadIndexCacheOptions _options;

        public RedisCachedAiStepPayloadIndex(
            IConnectionMultiplexer redis,
            IOptions<AiPayloadStoreOptions> options)
        {
            _redis = redis ?? throw new ArgumentNullException(nameof(redis));
            ArgumentNullException.ThrowIfNull(options);

            _options = options.Value.StepIndexCache;
        }

        /// <inheritdoc />
        public async Task SetAsync(
            AiArchivedStepPayloadIndex entry,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);

            if (!_options.Enabled)
            {
                return;
            }

            await _redis.GetDatabase()
                .StringSetAsync(
                    BuildKey(entry.ExecutionId, entry.StepName),
                    Serialize(entry),
                    GetExpiration())
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task SetManyAsync(
            IReadOnlyCollection<AiArchivedStepPayloadIndex> entries,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entries);

            if (!_options.Enabled || entries.Count == 0)
            {
                return;
            }

            var validEntries = entries
                .Where(x =>
                    x is not null &&
                    !string.IsNullOrWhiteSpace(x.ExecutionId) &&
                    !string.IsNullOrWhiteSpace(x.StepName))
                .ToArray();

            if (validEntries.Length == 0)
            {
                return;
            }

            var db = _redis.GetDatabase();
            var batch = db.CreateBatch();
            var expiration = GetExpiration();

            var tasks = validEntries
                .Select(entry =>
                    batch.StringSetAsync(
                        BuildKey(entry.ExecutionId, entry.StepName),
                        Serialize(entry),
                        expiration))
                .ToArray();

            batch.Execute();

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<AiArchivedStepPayloadIndex?> GetAsync(
            string executionId,
            string stepName,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(stepName);

            if (!_options.Enabled)
            {
                return null;
            }

            var key = BuildKey(executionId, stepName);
            var db = _redis.GetDatabase();

            var value = await db.StringGetAsync(key)
                .ConfigureAwait(false);
            Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics.Record(
                db,
                Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.StepPayloadIndexLoad,
                "GET",
                value);

            if (!value.HasValue)
            {
                return null;
            }

            if (_options.RefreshTtlOnRead)
            {
                await db.KeyExpireAsync(
                        key,
                        GetExpiration())
                    .ConfigureAwait(false);
            }

            return Deserialize(value);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyDictionary<string, AiArchivedStepPayloadIndex>> GetManyAsync(
            string executionId,
            IReadOnlyCollection<string> stepNames,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentNullException.ThrowIfNull(stepNames);

            if (!_options.Enabled || stepNames.Count == 0)
            {
                return new Dictionary<string, AiArchivedStepPayloadIndex>(StringComparer.Ordinal);
            }

            var keyPairs = stepNames
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .Select(stepName => new
                {
                    StepName = stepName,
                    Key = (RedisKey)BuildKey(executionId, stepName)
                })
                .ToArray();

            if (keyPairs.Length == 0)
            {
                return new Dictionary<string, AiArchivedStepPayloadIndex>(StringComparer.Ordinal);
            }

            var db = _redis.GetDatabase();

            // Redis MGET: one round-trip for all requested index entries.
            var values = await db.StringGetAsync(
                    keyPairs.Select(x => x.Key).ToArray())
                .ConfigureAwait(false);
            Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics.Record(
                db,
                Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.StepPayloadIndexLoadMany,
                "MGET",
                values);

            var result = new Dictionary<string, AiArchivedStepPayloadIndex>(StringComparer.Ordinal);

            var keysToRefresh = new List<RedisKey>();

            for (var i = 0; i < keyPairs.Length; i++)
            {
                var value = values[i];

                if (!value.HasValue)
                {
                    continue;
                }

                var entry = Deserialize(value);

                if (entry is null)
                {
                    continue;
                }

                result[keyPairs[i].StepName] = entry;

                if (_options.RefreshTtlOnRead)
                {
                    keysToRefresh.Add(keyPairs[i].Key);
                }
            }

            if (keysToRefresh.Count > 0)
            {
                await RefreshManyTtlAsync(
                        db,
                        keysToRefresh,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return result;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(
            string executionId,
            string stepName,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(stepName);

            if (!_options.Enabled)
            {
                return;
            }

            await _redis.GetDatabase()
                .KeyDeleteAsync(BuildKey(executionId, stepName))
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Refreshes TTL for multiple keys using Redis batching.
        /// </summary>
        private async Task RefreshManyTtlAsync(
            IDatabase db,
            IReadOnlyCollection<RedisKey> keys,
            CancellationToken cancellationToken)
        {
            if (keys.Count == 0)
            {
                return;
            }

            var batch = db.CreateBatch();
            var expiration = GetExpiration();

            var tasks = keys
                .Select(key => batch.KeyExpireAsync(key, expiration))
                .ToArray();

            batch.Execute();

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        /// <summary>
        /// Serializes an archived step index entry.
        /// </summary>
        private static string Serialize(AiArchivedStepPayloadIndex entry)
        {
            return JsonSerializer.Serialize(entry);
        }

        /// <summary>
        /// Deserializes an archived step index entry.
        /// </summary>
        private static AiArchivedStepPayloadIndex? Deserialize(RedisValue value)
        {
            return JsonSerializer.Deserialize<AiArchivedStepPayloadIndex>(
                value.ToString());
        }

        /// <summary>
        /// Gets the configured Redis expiration.
        /// </summary>
        private TimeSpan GetExpiration()
        {
            return TimeSpan.FromSeconds(_options.ExpirationSeconds);
        }

        /// <summary>
        /// Builds the Redis key for one archived step index entry.
        /// </summary>
        private string BuildKey(string executionId, string stepName)
        {
            return $"{_options.KeyPrefix}:{executionId}:{stepName}";
        }
    }
}