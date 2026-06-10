using System.Text.Json;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using StackExchange.Redis;

namespace Multiplexed.AI.Runtime.ControlPlane.Discovery.Redis
{
    /// <summary>
    /// Provides a Redis-backed implementation of the control-plane discovery store.
    /// </summary>
    public sealed class RedisAiControlPlaneDiscoveryStore : IAiControlPlaneDiscoveryStore
    {
        private const string DefaultKeyPrefix = "multiplexed:ai";
        private const string KeySegment = "control-plane:discovery";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly IConnectionMultiplexer connectionMultiplexer;
        private readonly string keyPrefix;

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisAiControlPlaneDiscoveryStore"/> class.
        /// </summary>
        /// <param name="connectionMultiplexer">The Redis connection multiplexer.</param>
        public RedisAiControlPlaneDiscoveryStore(IConnectionMultiplexer connectionMultiplexer)
            : this(connectionMultiplexer, DefaultKeyPrefix)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisAiControlPlaneDiscoveryStore"/> class.
        /// </summary>
        /// <param name="connectionMultiplexer">The Redis connection multiplexer.</param>
        /// <param name="keyPrefix">The Redis key prefix.</param>
        public RedisAiControlPlaneDiscoveryStore(
            IConnectionMultiplexer connectionMultiplexer,
            string keyPrefix)
        {
            ArgumentNullException.ThrowIfNull(connectionMultiplexer);

            this.connectionMultiplexer = connectionMultiplexer;
            this.keyPrefix =
                string.IsNullOrWhiteSpace(keyPrefix)
                    ? DefaultKeyPrefix
                    : keyPrefix.Trim();
        }

        /// <inheritdoc />
        public async Task PublishAsync(
            AiControlPlaneDiscoveryDescriptor descriptor,
            TimeSpan? ttl,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(descriptor.RedisDiscoveryKey))
            {
                throw new ArgumentException(
                    "The Redis discovery key cannot be null or empty.",
                    nameof(descriptor));
            }

            if (string.IsNullOrWhiteSpace(descriptor.ControlPlaneId))
            {
                throw new ArgumentException(
                    "The control-plane identifier cannot be null or empty.",
                    nameof(descriptor));
            }

            var database = connectionMultiplexer.GetDatabase();
            var key = BuildDiscoveryKey(descriptor.RedisDiscoveryKey);
            var payload = JsonSerializer.Serialize(descriptor, JsonOptions);

            await database
                .StringSetAsync(
                    key,
                    payload)
                .ConfigureAwait(false);

            if (ttl.HasValue)
            {
                await database
                    .KeyExpireAsync(
                        key,
                        ttl.Value)
                    .ConfigureAwait(false);
            }
            else
            {
                await database
                    .KeyPersistAsync(key)
                    .ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public async Task<AiControlPlaneDiscoveryDescriptor?> GetAsync(
            string redisDiscoveryKey,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(redisDiscoveryKey);
            cancellationToken.ThrowIfCancellationRequested();

            var database = connectionMultiplexer.GetDatabase();
            var key = BuildDiscoveryKey(redisDiscoveryKey);

            var payload =
                await database
                    .StringGetAsync(key)
                    .ConfigureAwait(false);

            if (!payload.HasValue)
            {
                return null;
            }

            var json = (string)payload!;

            return JsonSerializer.Deserialize<AiControlPlaneDiscoveryDescriptor>(
                json,
                JsonOptions);
        }

        /// <inheritdoc />
        public async Task DeleteAsync(
            string redisDiscoveryKey,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(redisDiscoveryKey);
            cancellationToken.ThrowIfCancellationRequested();

            var database = connectionMultiplexer.GetDatabase();
            var key = BuildDiscoveryKey(redisDiscoveryKey);

            await database
                .KeyDeleteAsync(key)
                .ConfigureAwait(false);
        }

        private RedisKey BuildDiscoveryKey(string redisDiscoveryKey)
        {
            var normalizedDiscoveryKey =
                redisDiscoveryKey
                    .Trim()
                    .Replace(" ", "-", StringComparison.Ordinal)
                    .Replace("\\", "/", StringComparison.Ordinal);

            return $"{keyPrefix}:{KeySegment}:{normalizedDiscoveryKey}";
        }
    }
}