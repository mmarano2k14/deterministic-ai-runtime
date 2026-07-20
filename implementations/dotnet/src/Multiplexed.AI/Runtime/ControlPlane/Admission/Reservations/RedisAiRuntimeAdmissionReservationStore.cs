using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
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
    /// - One Redis ZSET key is used per runtime instance and per logical control-plane.
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
    /// - Reservation keys are scoped by logical control-plane identifier.
    /// - Reservation members are also prefixed by logical control-plane and runtime instance identifiers.
    /// - Count and release operations defensively remove expired or foreign members before returning results.
    /// </remarks>
    public sealed class RedisAiRuntimeAdmissionReservationStore :
        IAiRuntimeAdmissionReservationStore
    {
        private const string ControlPlaneKeySegment =
            "control-plane";

        private const string ReservationKeySegment =
            "runtime-admission-reservations";

        private const string ReserveScript = """
            local key = KEYS[1]

            local now = tonumber(ARGV[1])
            local expiresAt = tonumber(ARGV[2])
            local keyTtlMs = tonumber(ARGV[3])
            local count = tonumber(ARGV[4])
            local memberPrefix = ARGV[5]

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

            if memberPrefix == nil or memberPrefix == '' then
                return redis.error_reply('memberPrefix must be provided')
            end

            redis.call('ZREMRANGEBYSCORE', key, '-inf', now)

            local existingMembers = redis.call('ZRANGE', key, 0, -1)
            for _, member in ipairs(existingMembers) do
                if string.sub(member, 1, string.len(memberPrefix)) ~= memberPrefix then
                    redis.call('ZREM', key, member)
                end
            end

            for i = 1, count do
                local member = ARGV[5 + i]

                if member == nil or member == '' then
                    return redis.error_reply('reservation member must be provided')
                end

                if string.sub(member, 1, string.len(memberPrefix)) ~= memberPrefix then
                    return redis.error_reply('reservation member does not match memberPrefix')
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
            local memberPrefix = ARGV[4]

            if now == nil then
                return redis.error_reply('now must be provided')
            end

            if count == nil or count <= 0 then
                return redis.error_reply('count must be greater than zero')
            end

            if keyTtlMs == nil or keyTtlMs <= 0 then
                return redis.error_reply('keyTtlMs must be greater than zero')
            end

            if memberPrefix == nil or memberPrefix == '' then
                return redis.error_reply('memberPrefix must be provided')
            end

            redis.call('ZREMRANGEBYSCORE', key, '-inf', now)

            local existingMembers = redis.call('ZRANGE', key, 0, -1)
            for _, member in ipairs(existingMembers) do
                if string.sub(member, 1, string.len(memberPrefix)) ~= memberPrefix then
                    redis.call('ZREM', key, member)
                end
            end

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
            local memberPrefix = ARGV[3]

            if now == nil then
                return redis.error_reply('now must be provided')
            end

            if keyTtlMs == nil or keyTtlMs <= 0 then
                return redis.error_reply('keyTtlMs must be greater than zero')
            end

            if memberPrefix == nil or memberPrefix == '' then
                return redis.error_reply('memberPrefix must be provided')
            end

            redis.call('ZREMRANGEBYSCORE', key, '-inf', now)

            local existingMembers = redis.call('ZRANGE', key, 0, -1)
            for _, member in ipairs(existingMembers) do
                if string.sub(member, 1, string.len(memberPrefix)) ~= memberPrefix then
                    redis.call('ZREM', key, member)
                end
            end

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
        private readonly IAiControlPlaneIdResolver controlPlaneIdResolver;
        private readonly SemaphoreSlim scriptLoadLock = new(1, 1);

        private volatile byte[]? reserveScriptSha;
        private volatile byte[]? releaseScriptSha;
        private volatile byte[]? countScriptSha;

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisAiRuntimeAdmissionReservationStore"/> class.
        /// </summary>
        /// <param name="redis">The Redis connection multiplexer.</param>
        /// <param name="options">The Redis admission reservation options.</param>
        /// <param name="controlPlaneIdResolver">The control-plane identifier resolver.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="redis"/>, <paramref name="options"/>,
        /// or <paramref name="controlPlaneIdResolver"/> is null.
        /// </exception>
        public RedisAiRuntimeAdmissionReservationStore(
            IConnectionMultiplexer redis,
            IOptions<AiRuntimeAdmissionReservationRedisOptions> options,
            IAiControlPlaneIdResolver controlPlaneIdResolver)
        {
            ArgumentNullException.ThrowIfNull(redis);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(controlPlaneIdResolver);

            this.redis =
                redis;

            database =
                redis.GetDatabase();

            this.options =
                options.Value ?? throw new ArgumentNullException(nameof(options));

            this.controlPlaneIdResolver =
                controlPlaneIdResolver;
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

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(cancellationToken)
                    .ConfigureAwait(false);

            var now =
                DateTimeOffset.UtcNow;

            var expiresAt =
                now.Add(options.ReservationTtl);

            var memberPrefix =
                CreateReservationMemberPrefix(
                    controlPlaneId,
                    runtimeInstanceId);

            var values =
                new RedisValue[5 + runCount];

            values[0] =
                now.ToUnixTimeMilliseconds();

            values[1] =
                expiresAt.ToUnixTimeMilliseconds();

            values[2] =
                GetKeyTtlMilliseconds();

            values[3] =
                runCount;

            values[4] =
                memberPrefix;

            for (var index = 0; index < runCount; index++)
            {
                values[5 + index] =
                    CreateReservationMember(
                        memberPrefix);
            }

            await EvaluateShaWithNoScriptRetryAsync(
                    reserveScriptSha!,
                    ReserveScript,
                    new RedisKey[]
                    {
                        GetReservationKey(
                            controlPlaneId,
                            runtimeInstanceId)
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

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(cancellationToken)
                    .ConfigureAwait(false);

            var now =
                DateTimeOffset.UtcNow;

            await EvaluateShaWithNoScriptRetryAsync(
                    releaseScriptSha!,
                    ReleaseScript,
                    new RedisKey[]
                    {
                        GetReservationKey(
                            controlPlaneId,
                            runtimeInstanceId)
                    },
                    new RedisValue[]
                    {
                        now.ToUnixTimeMilliseconds(),
                        runCount,
                        GetKeyTtlMilliseconds(),
                        CreateReservationMemberPrefix(
                            controlPlaneId,
                            runtimeInstanceId)
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

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(cancellationToken)
                    .ConfigureAwait(false);

            var now =
                DateTimeOffset.UtcNow;

            var result =
                await EvaluateShaWithNoScriptRetryAsync(
                        countScriptSha!,
                        CountScript,
                        new RedisKey[]
                        {
                            GetReservationKey(
                                controlPlaneId,
                                runtimeInstanceId)
                        },
                        new RedisValue[]
                        {
                            now.ToUnixTimeMilliseconds(),
                            GetKeyTtlMilliseconds(),
                            CreateReservationMemberPrefix(
                                controlPlaneId,
                                runtimeInstanceId)
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

        /// <summary>
        /// Executes a cached Lua script SHA and reloads scripts once when Redis reports NOSCRIPT.
        /// </summary>
        /// <param name="sha">The cached script SHA.</param>
        /// <param name="script">The Lua script source.</param>
        /// <param name="keys">The Redis keys passed to the script.</param>
        /// <param name="values">The Redis values passed to the script.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The Redis script result.</returns>
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

        /// <summary>
        /// Ensures that all Lua scripts are loaded into Redis.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
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

        /// <summary>
        /// Reloads all Lua scripts into Redis.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="forceReload">A value indicating whether scripts should be force reloaded.</param>
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

        /// <summary>
        /// Gets the cached SHA for a Lua script.
        /// </summary>
        /// <param name="script">The Lua script source.</param>
        /// <returns>The cached script SHA.</returns>
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

        /// <summary>
        /// Gets a connected Redis server that can load Lua scripts.
        /// </summary>
        /// <returns>A Redis server instance.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no Redis endpoint is available.
        /// </exception>
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

        /// <summary>
        /// Resolves the logical control-plane identifier used to scope admission reservation keys.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved logical control-plane identifier.</returns>
        private async Task<string> ResolveControlPlaneIdAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resolvedControlPlaneId =
                await this.controlPlaneIdResolver
                    .ResolveAsync(
                        new AiControlPlaneIdResolutionRequest
                        {
                            Source = "redis-runtime-admission-reservation-store",
                            AllowGeneratedFallback = false
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(resolvedControlPlaneId))
            {
                throw new InvalidOperationException(
                    "The resolved control-plane identifier cannot be null or empty.");
            }

            return resolvedControlPlaneId;
        }

        /// <summary>
        /// Builds the Redis reservation key for a runtime instance inside one logical control-plane.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <returns>The Redis reservation key.</returns>
        private RedisKey GetReservationKey(
            string controlPlaneId,
            string runtimeInstanceId)
        {
            return string.Concat(
                NormalizeKeyPrefix(options.KeyPrefix),
                ":",
                ControlPlaneKeySegment,
                ":",
                NormalizeKeySegment(controlPlaneId),
                ":",
                ReservationKeySegment,
                ":",
                NormalizeKeySegment(runtimeInstanceId));
        }

        /// <summary>
        /// Gets the Redis key TTL in milliseconds.
        /// </summary>
        /// <returns>The Redis key TTL in milliseconds.</returns>
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

        /// <summary>
        /// Normalizes the configured Redis key prefix.
        /// </summary>
        /// <param name="keyPrefix">The configured Redis key prefix.</param>
        /// <returns>The normalized Redis key prefix.</returns>
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

        /// <summary>
        /// Normalizes a value so it can be used as a stable Redis key segment.
        /// </summary>
        /// <param name="value">The value to normalize.</param>
        /// <returns>The normalized Redis key segment.</returns>
        private static string NormalizeKeySegment(
            string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);

            return value
                .Trim()
                .Replace(" ", "-", StringComparison.Ordinal)
                .Replace("\\", "/", StringComparison.Ordinal);
        }

        /// <summary>
        /// Creates the deterministic reservation member prefix for one logical control-plane and runtime instance.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <returns>The reservation member prefix.</returns>
        private static string CreateReservationMemberPrefix(
            string controlPlaneId,
            string runtimeInstanceId)
        {
            return string.Concat(
                NormalizeKeySegment(controlPlaneId),
                ":",
                NormalizeKeySegment(runtimeInstanceId),
                ":");
        }

        /// <summary>
        /// Creates a unique reservation member inside the scoped control-plane/runtime prefix.
        /// </summary>
        /// <param name="memberPrefix">The deterministic reservation member prefix.</param>
        /// <returns>The unique reservation member.</returns>
        private static string CreateReservationMember(
            string memberPrefix)
        {
            return string.Concat(
                memberPrefix,
                Environment.MachineName,
                ":",
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                ":",
                Guid.NewGuid().ToString("N"));
        }

        /// <summary>
        /// Determines whether a Redis exception is caused by a missing cached Lua script.
        /// </summary>
        /// <param name="exception">The Redis server exception.</param>
        /// <returns><c>true</c> when Redis reported NOSCRIPT; otherwise, <c>false</c>.</returns>
        private static bool IsNoScriptException(
            RedisServerException exception)
        {
            return exception.Message.Contains(
                "NOSCRIPT",
                StringComparison.OrdinalIgnoreCase);
        }
    }
}