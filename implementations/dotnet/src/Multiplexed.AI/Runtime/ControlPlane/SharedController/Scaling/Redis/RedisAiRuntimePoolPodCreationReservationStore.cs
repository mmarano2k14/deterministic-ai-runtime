using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Redis;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using StackExchange.Redis;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Redis-backed atomic Runtime Pool Pod creation reservation authority.
    /// </summary>
    public sealed class RedisAiRuntimePoolPodCreationReservationStore :
        IAiRuntimePoolPodCreationReservationStore
    {
        private const string TryAcquireScript = """
            local key = KEYS[1]
            local member = ARGV[1]
            local leaseDuration = tonumber(ARGV[2])
            local ttlBuffer = tonumber(ARGV[3])
            local activePodCount = tonumber(ARGV[4])
            local maximumPodCount = tonumber(ARGV[5])
            local redisTime = redis.call('TIME')
            local now =
                tonumber(redisTime[1]) * 1000 +
                math.floor(tonumber(redisTime[2]) / 1000)
            local expiresAt = now + leaseDuration

            redis.call('ZREMRANGEBYSCORE', key, '-inf', now)

            local function refreshKeyTtl()
                local latest = redis.call(
                    'ZREVRANGE',
                    key,
                    0,
                    0,
                    'WITHSCORES')

                if #latest < 2 then
                    redis.call('DEL', key)
                    return
                end

                local ttl = tonumber(latest[2]) - now + ttlBuffer
                if ttl < 1 then
                    ttl = 1
                end

                redis.call('PEXPIRE', key, ttl)
            end

            if redis.call('ZSCORE', key, member) then
                redis.call('ZADD', key, expiresAt, member)
                refreshKeyTtl()
                return { 1, tonumber(redis.call('ZCARD', key)) }
            end

            local reservedPodCount = tonumber(redis.call('ZCARD', key))

            if activePodCount + reservedPodCount >= maximumPodCount then
                refreshKeyTtl()
                return { 0, reservedPodCount }
            end

            redis.call('ZADD', key, expiresAt, member)
            refreshKeyTtl()

            return { 1, tonumber(redis.call('ZCARD', key)) }
            """;

        private const string ReleaseScript = """
            local key = KEYS[1]
            local member = ARGV[1]
            local ttlBuffer = tonumber(ARGV[2])
            local redisTime = redis.call('TIME')
            local now =
                tonumber(redisTime[1]) * 1000 +
                math.floor(tonumber(redisTime[2]) / 1000)

            redis.call('ZREMRANGEBYSCORE', key, '-inf', now)
            redis.call('ZREM', key, member)

            local remaining = tonumber(redis.call('ZCARD', key))

            if remaining <= 0 then
                redis.call('DEL', key)
                return 0
            end

            local latest = redis.call(
                'ZREVRANGE',
                key,
                0,
                0,
                'WITHSCORES')
            local ttl = tonumber(latest[2]) - now + ttlBuffer

            if ttl < 1 then
                ttl = 1
            end

            redis.call('PEXPIRE', key, ttl)
            return remaining
            """;

        private readonly IDatabase database;
        private readonly string keyPrefix;

        /// <summary>
        /// Initializes the Redis reservation store.
        /// </summary>
        public RedisAiRuntimePoolPodCreationReservationStore(
            IConnectionMultiplexer connection,
            IOptions<RedisAiRuntimeScaleOutRequestStoreOptions> options)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(options);

            var value =
                options.Value ??
                throw new ArgumentException(
                    "Redis scale-out options must be provided.",
                    nameof(options));

            this.database =
                connection.GetDatabase(value.Database ?? -1);

            this.keyPrefix =
                string.IsNullOrWhiteSpace(value.KeyPrefix)
                    ? AiRedisControlPlaneDefaults.DefaultKeyPrefix
                    : value.KeyPrefix.Trim();
        }

        /// <inheritdoc />
        public async Task<AiRuntimePoolPodCreationReservationAttemptResult>
            TryAcquireAsync(
                string controlPlaneId,
                string poolId,
                string reservationId,
                int activePodCount,
                int maximumPodCount,
                DateTimeOffset expiresAtUtc,
                CancellationToken cancellationToken = default)
        {
            Validate(
                controlPlaneId,
                poolId,
                reservationId,
                activePodCount,
                maximumPodCount,
                expiresAtUtc);

            cancellationToken.ThrowIfCancellationRequested();

            var leaseDuration =
                expiresAtUtc - DateTimeOffset.UtcNow;

            var leaseDurationMilliseconds =
                Math.Max(
                    1L,
                    checked(
                        (long)Math.Ceiling(
                            leaseDuration.TotalMilliseconds)));

            var redisResult =
                await this.database
                    .ScriptEvaluateAsync(
                        TryAcquireScript,
                        new RedisKey[]
                        {
                            this.CreateKey(controlPlaneId, poolId)
                        },
                        new RedisValue[]
                        {
                            reservationId.Trim(),
                            leaseDurationMilliseconds,
                            (long)TimeSpan.FromMinutes(5).TotalMilliseconds,
                            activePodCount,
                            maximumPodCount
                        })
                    .ConfigureAwait(false);

            return ParseTryAcquireResult(
                activePodCount,
                maximumPodCount,
                redisResult);
        }

        /// <inheritdoc />
        public async Task ReleaseAsync(
            string controlPlaneId,
            string poolId,
            string reservationId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);
            cancellationToken.ThrowIfCancellationRequested();

            await this.database
                .ScriptEvaluateAsync(
                    ReleaseScript,
                    new RedisKey[]
                    {
                        this.CreateKey(controlPlaneId, poolId)
                    },
                    new RedisValue[]
                    {
                        reservationId.Trim(),
                        (long)TimeSpan.FromMinutes(5).TotalMilliseconds
                    })
                .ConfigureAwait(false);
        }

        private static AiRuntimePoolPodCreationReservationAttemptResult
            ParseTryAcquireResult(
                int activePodCount,
                int maximumPodCount,
                RedisResult result)
        {
            RedisResult[] values;

            try
            {
                values = (RedisResult[])result!;
            }
            catch (InvalidCastException exception)
            {
                throw new InvalidOperationException(
                    "Redis returned an invalid Runtime Pool Pod creation reservation result.",
                    exception);
            }

            if (values.Length != 2 ||
                values[0].IsNull ||
                values[1].IsNull)
            {
                throw new InvalidOperationException(
                    "Redis returned an incomplete Runtime Pool Pod creation reservation result.");
            }

            return new AiRuntimePoolPodCreationReservationAttemptResult
            {
                Acquired = (long)values[0] == 1L,
                ActivePodCount = activePodCount,
                ReservedPodCount = checked((int)(long)values[1]),
                MaximumPodCount = maximumPodCount
            };
        }

        private RedisKey CreateKey(
            string controlPlaneId,
            string poolId)
        {
            var authority =
                string.Concat(
                    controlPlaneId.Trim(),
                    "\n",
                    poolId.Trim());

            var token =
                Convert.ToHexString(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes(authority)))
                .ToLowerInvariant();

            return string.Concat(
                this.keyPrefix,
                ":runtime-pool-pod-creation:{",
                token,
                "}:reservations");
        }

        private static void Validate(
            string controlPlaneId,
            string poolId,
            string reservationId,
            int activePodCount,
            int maximumPodCount,
            DateTimeOffset expiresAtUtc)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);
            ArgumentOutOfRangeException.ThrowIfNegative(activePodCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPodCount);

            if (expiresAtUtc <= DateTimeOffset.UtcNow)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(expiresAtUtc),
                    expiresAtUtc,
                    "Pod creation reservation expiration must be in the future.");
            }
        }
    }
}
