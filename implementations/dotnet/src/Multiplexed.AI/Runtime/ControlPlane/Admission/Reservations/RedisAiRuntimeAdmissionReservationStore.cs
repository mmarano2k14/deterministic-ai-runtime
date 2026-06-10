using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using StackExchange.Redis;
using System.Globalization;

namespace Multiplexed.AI.Runtime.ControlPlane.Admission.Reservations
{
    /// <summary>
    /// Redis-backed implementation of runtime admission reservations.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Provides distributed admission reservation tracking.
    /// - Prevents multiple control-plane processes, pumps, or workers from repeatedly
    ///   selecting the same runtime instance before heartbeat/capacity snapshots catch up.
    ///
    /// DESIGN:
    /// - One Redis ZSET key is used per runtime instance.
    /// - Each reservation is stored as a unique GUID-based ZSET member.
    /// - The ZSET score is the reservation expiration timestamp in Unix milliseconds.
    /// - Lua scripts are loaded into Redis and executed by SHA using EVALSHA.
    ///
    /// IMPORTANT:
    /// - A reservation is not a run.
    /// - A reservation is temporary capacity accounting.
    /// - This implementation does not send Lua script text on each operation.
    /// - Scripts are loaded once and then executed by SHA.
    /// - If Redis evicts scripts and returns NOSCRIPT, scripts are reloaded and retried once.
    /// </remarks>
    public sealed class RedisAiRuntimeAdmissionReservationStore :
        IAiRuntimeAdmissionReservationStore
    {
        private const string ReservationKeySegment =
            "runtime-admission-reservations";

        private const string ReserveScript = """
            local key = KEYS[1]

            local now = tonumber(ARGV[1])
            local expiresAt = tonumber(ARGV[2])
            local keyTtlMs = tonumber(ARGV[3])
            local count = tonumber(ARGV[4])

            if now == nil then
                return redis.error_reply('now must be provided')
            end

            if expiresAt == nil then
                return redis.error_reply('expiresAt must be provided')
            end

            if keyTtlMs == nil or keyTtlMs <= 0 then
                return redis.error_reply('keyTtlMs must be greater than zero')
            end

            if count == nil or count <= 0 then
                return redis.error_reply('count must be greater than zero')
            end

            redis.call('ZREMRANGEBYSCORE', key, '-inf', now)

            for i = 1, count do
                local member = ARGV[4 + i]

                if member == nil or member == '' then
                    return redis.error_reply('reservation member must be provided')
                end

                redis.call('ZADD', key, expiresAt, member)
            end

            redis.call('PEXPIRE', key, keyTtlMs)

            return redis.call('ZCARD', key)
            """;

        private const string ReleaseScript = """
            local key = KEYS[1]

            local now = tonumber(ARGV[1])
            local count = tonumber(ARGV[2])
            local keyTtlMs = tonumber(ARGV[3])

            if now == nil then
                return redis.error_reply('now must be provided')
            end

            if count == nil or count <= 0 then
                return redis.error_reply('count must be greater than zero')
            end

            if keyTtlMs == nil or keyTtlMs <= 0 then
                return redis.error_reply('keyTtlMs must be greater than zero')
            end

            redis.call('ZREMRANGEBYSCORE', key, '-inf', now)

            local current = tonumber(redis.call('ZCARD', key))

            if current <= 0 then
                redis.call('DEL', key)
                return 0
            end

            local members = redis.call('ZRANGE', key, 0, count - 1)

            if #members > 0 then
                redis.call('ZREM', key, unpack(members))
            end

            local remaining = tonumber(redis.call('ZCARD', key))

            if remaining <= 0 then
                redis.call('DEL', key)
                return 0
            end

            redis.call('PEXPIRE', key, keyTtlMs)

            return remaining
            """;

        private const string CountScript = """
            local key = KEYS[1]

            local now = tonumber(ARGV[1])
            local keyTtlMs = tonumber(ARGV[2])

            if now == nil then
                return redis.error_reply('now must be provided')
            end

            if keyTtlMs == nil or keyTtlMs <= 0 then
                return redis.error_reply('keyTtlMs must be greater than zero')
            end

            redis.call('ZREMRANGEBYSCORE', key, '-inf', now)

            local remaining = tonumber(redis.call('ZCARD', key))

            if remaining <= 0 then
                redis.call('DEL', key)
                return 0
            end

            redis.call('PEXPIRE', key, keyTtlMs)

            return remaining
            """;

        private readonly IConnectionMultiplexer redis;
        private readonly IDatabase database;
        private readonly AiRuntimeAdmissionReservationRedisOptions options;
        private readonly SemaphoreSlim scriptLoadLock = new(1, 1);

        private volatile byte[]? reserveScriptSha;
        private volatile byte[]? releaseScriptSha;
        private volatile byte[]? countScriptSha;

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisAiRuntimeAdmissionReservationStore"/> class.
        /// </summary>
        /// <param name="redis">The Redis connection multiplexer.</param>
        /// <param name="options">The Redis admission reservation options.</param>
        public RedisAiRuntimeAdmissionReservationStore(
            IConnectionMultiplexer redis,
            IOptions<AiRuntimeAdmissionReservationRedisOptions> options)
        {
            ArgumentNullException.ThrowIfNull(redis);
            ArgumentNullException.ThrowIfNull(options);

            this.redis =
                redis;

            database =
                redis.GetDatabase();

            this.options =
                options.Value ?? throw new ArgumentNullException(nameof(options));
        }

        /// <inheritdoc />
        public async Task ReserveAsync(
            string runtimeInstanceId,
            int runCount = 1,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runCount);

            cancellationToken.ThrowIfCancellationRequested();

            await EnsureScriptsLoadedAsync(cancellationToken)
                .ConfigureAwait(false);

            var now =
                DateTimeOffset.UtcNow;

            var expiresAt =
                now.Add(options.ReservationTtl);

            var values =
                new RedisValue[4 + runCount];

            values[0] =
                now.ToUnixTimeMilliseconds();

            values[1] =
                expiresAt.ToUnixTimeMilliseconds();

            values[2] =
                GetKeyTtlMilliseconds();

            values[3] =
                runCount;

            for (var index = 0; index < runCount; index++)
            {
                values[4 + index] =
                    CreateReservationMember(runtimeInstanceId);
            }

            await EvaluateShaWithNoScriptRetryAsync(
                    reserveScriptSha!,
                    ReserveScript,
                    new RedisKey[]
                    {
                        GetReservationKey(runtimeInstanceId)
                    },
                    values,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task ReleaseAsync(
            string runtimeInstanceId,
            int runCount = 1,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runCount);

            cancellationToken.ThrowIfCancellationRequested();

            await EnsureScriptsLoadedAsync(cancellationToken)
                .ConfigureAwait(false);

            var now =
                DateTimeOffset.UtcNow;

            await EvaluateShaWithNoScriptRetryAsync(
                    releaseScriptSha!,
                    ReleaseScript,
                    new RedisKey[]
                    {
                        GetReservationKey(runtimeInstanceId)
                    },
                    new RedisValue[]
                    {
                        now.ToUnixTimeMilliseconds(),
                        runCount,
                        GetKeyTtlMilliseconds()
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<int> GetReservedRunCountAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            await EnsureScriptsLoadedAsync(cancellationToken)
                .ConfigureAwait(false);

            var now =
                DateTimeOffset.UtcNow;

            var result =
                await EvaluateShaWithNoScriptRetryAsync(
                        countScriptSha!,
                        CountScript,
                        new RedisKey[]
                        {
                            GetReservationKey(runtimeInstanceId)
                        },
                        new RedisValue[]
                        {
                            now.ToUnixTimeMilliseconds(),
                            GetKeyTtlMilliseconds()
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            if (result.IsNull)
            {
                return 0;
            }

            var count =
                (long)result;

            if (count <= 0)
            {
                return 0;
            }

            if (count > int.MaxValue)
            {
                return int.MaxValue;
            }

            return (int)count;
        }

        private async Task<RedisResult> EvaluateShaWithNoScriptRetryAsync(
            byte[] sha,
            string script,
            RedisKey[] keys,
            RedisValue[] values,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await database
                    .ScriptEvaluateAsync(
                        sha,
                        keys,
                        values)
                    .ConfigureAwait(false);
            }
            catch (RedisServerException exception) when (IsNoScriptException(exception))
            {
                await ReloadScriptsAsync(
                        cancellationToken,
                        forceReload: true)
                    .ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                var reloadedSha =
                    GetShaForScript(script);

                return await database
                    .ScriptEvaluateAsync(
                        reloadedSha,
                        keys,
                        values)
                    .ConfigureAwait(false);
            }
        }

        private async Task EnsureScriptsLoadedAsync(
            CancellationToken cancellationToken)
        {
            if (reserveScriptSha is not null &&
                releaseScriptSha is not null &&
                countScriptSha is not null)
            {
                return;
            }

            await ReloadScriptsAsync(
                    cancellationToken,
                    forceReload: false)
                .ConfigureAwait(false);
        }

        private async Task ReloadScriptsAsync(
            CancellationToken cancellationToken,
            bool forceReload)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await scriptLoadLock
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                if (!forceReload &&
                    reserveScriptSha is not null &&
                    releaseScriptSha is not null &&
                    countScriptSha is not null)
                {
                    return;
                }

                var server =
                    GetRedisServer();

                reserveScriptSha =
                    await server
                        .ScriptLoadAsync(ReserveScript)
                        .ConfigureAwait(false);

                releaseScriptSha =
                    await server
                        .ScriptLoadAsync(ReleaseScript)
                        .ConfigureAwait(false);

                countScriptSha =
                    await server
                        .ScriptLoadAsync(CountScript)
                        .ConfigureAwait(false);
            }
            finally
            {
                scriptLoadLock.Release();
            }
        }

        private byte[] GetShaForScript(
            string script)
        {
            if (ReferenceEquals(script, ReserveScript) ||
                string.Equals(script, ReserveScript, StringComparison.Ordinal))
            {
                return reserveScriptSha!;
            }

            if (ReferenceEquals(script, ReleaseScript) ||
                string.Equals(script, ReleaseScript, StringComparison.Ordinal))
            {
                return releaseScriptSha!;
            }

            return countScriptSha!;
        }

        private IServer GetRedisServer()
        {
            var endpoints =
                redis.GetEndPoints();

            if (endpoints.Length == 0)
            {
                throw new InvalidOperationException(
                    "No Redis endpoint is available to load admission reservation scripts.");
            }

            foreach (var endpoint in endpoints)
            {
                var server =
                    redis.GetServer(endpoint);

                if (server.IsConnected)
                {
                    return server;
                }
            }

            return redis.GetServer(endpoints[0]);
        }

        private RedisKey GetReservationKey(
            string runtimeInstanceId)
        {
            return $"{NormalizeKeyPrefix(options.KeyPrefix)}:{ReservationKeySegment}:{runtimeInstanceId}";
        }

        private long GetKeyTtlMilliseconds()
        {
            var keyTtl =
                options.KeyTtl > options.ReservationTtl
                    ? options.KeyTtl
                    : options.ReservationTtl.Add(TimeSpan.FromMinutes(1));

            return Math.Max(
                1,
                (long)keyTtl.TotalMilliseconds);
        }

        private static string NormalizeKeyPrefix(
            string keyPrefix)
        {
            if (string.IsNullOrWhiteSpace(keyPrefix))
            {
                return "multiplexed:ai";
            }

            return keyPrefix
                .Trim()
                .TrimEnd(':');
        }

        private static string CreateReservationMember(
            string runtimeInstanceId)
        {
            return string.Concat(
                runtimeInstanceId,
                ":",
                Environment.MachineName,
                ":",
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                ":",
                Guid.NewGuid().ToString("N"));
        }

        private static bool IsNoScriptException(
            RedisServerException exception)
        {
            return exception.Message.Contains(
                "NOSCRIPT",
                StringComparison.OrdinalIgnoreCase);
        }
    }
}