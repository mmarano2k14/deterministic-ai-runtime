using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.Execution.Control;
using StackExchange.Redis;

namespace Multiplexed.AI.Stores.Cache.Redis.Control
{
    /// <summary>
    /// Redis-backed durable store for AI execution control state.
    /// </summary>
    /// <remarks>
    /// This store persists control state independently from DAG execution state.
    /// It is used to coordinate pause, resume, cancellation, and waiting-for-input
    /// behavior across distributed runtime instances.
    /// </remarks>
    public sealed class RedisAiExecutionControlStore : IAiExecutionControlStore
    {
        private static readonly JsonSerializerOptions DefaultJsonOptions = new(JsonSerializerDefaults.Web);

        private readonly IDatabase _database;
        private readonly RedisExecutionControlKeyBuilder _keyBuilder;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisAiExecutionControlStore"/> class.
        /// </summary>
        /// <param name="multiplexer">The Redis connection multiplexer.</param>
        /// <param name="keyBuilder">The Redis execution control key builder.</param>
        /// <param name="jsonOptions">Optional JSON serializer options.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="multiplexer"/> or <paramref name="keyBuilder"/> is null.
        /// </exception>
        public RedisAiExecutionControlStore(
            IConnectionMultiplexer multiplexer,
            RedisExecutionControlKeyBuilder keyBuilder,
            JsonSerializerOptions? jsonOptions = null)
        {
            ArgumentNullException.ThrowIfNull(multiplexer);
            ArgumentNullException.ThrowIfNull(keyBuilder);

            _database = multiplexer.GetDatabase();
            _keyBuilder = keyBuilder;
            _jsonOptions = jsonOptions ?? DefaultJsonOptions;
        }

        /// <inheritdoc />
        public async Task<bool> TryCreateAsync(
            AiExecutionControlState state,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(state);
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(state.ExecutionId))
            {
                throw new ArgumentException("Execution control state must have a valid execution id.", nameof(state));
            }

            if (state.Version <= 0)
            {
                state.Version = 1;
            }

            state.UpdatedAtUtc = DateTime.UtcNow;

            var key = _keyBuilder.BuildExecutionControlKey(state.ExecutionId);
            var payload = JsonSerializer.Serialize(state, _jsonOptions);

            var created = await _database.StringSetAsync(
                    key,
                    payload,
                    expiry: null,
                    when: When.NotExists)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            return created;
        }

        /// <inheritdoc />
        public async Task<AiExecutionControlState?> GetAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = _keyBuilder.BuildExecutionControlKey(executionId);
            var value = await _database.StringGetAsync(key).ConfigureAwait(false);
            Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics.Record(
                _database,
                Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.ExecutionControlStateLoad,
                "GET",
                value);

            cancellationToken.ThrowIfCancellationRequested();

            if (!value.HasValue)
            {
                return null;
            }

            return JsonSerializer.Deserialize<AiExecutionControlState>(
                value.ToString(),
                _jsonOptions);
        }

        /// <inheritdoc />
        public async Task SetAsync(
            AiExecutionControlState state,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(state);
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(state.ExecutionId))
            {
                throw new ArgumentException("Execution control state must have a valid execution id.", nameof(state));
            }

            if (state.Version <= 0)
            {
                state.Version = 1;
            }

            state.UpdatedAtUtc = DateTime.UtcNow;

            var key = _keyBuilder.BuildExecutionControlKey(state.ExecutionId);
            var payload = JsonSerializer.Serialize(state, _jsonOptions);

            await _database.StringSetAsync(key, payload).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <inheritdoc />
        public async Task<bool> TryUpdateAsync(
            AiExecutionControlState state,
            long expectedVersion,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(state);
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(state.ExecutionId))
            {
                throw new ArgumentException("Execution control state must have a valid execution id.", nameof(state));
            }

            if (expectedVersion < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(expectedVersion),
                    expectedVersion,
                    "Expected version cannot be negative.");
            }

            state.Version = expectedVersion + 1;
            state.UpdatedAtUtc = DateTime.UtcNow;

            var key = _keyBuilder.BuildExecutionControlKey(state.ExecutionId);
            var payload = JsonSerializer.Serialize(state, _jsonOptions);

            var result = await _database.ScriptEvaluateAsync(
                    RedisExecutionControlLuaScripts.TryUpdateByVersion,
                    new RedisKey[] { key },
                    new RedisValue[] { expectedVersion, payload })
                .ConfigureAwait(false);
            Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics.RecordInvocation(
                _database,
                Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.LuaExecutionControl);

            cancellationToken.ThrowIfCancellationRequested();

            return (long)result == 1L;
        }

        /// <inheritdoc />
        public async Task<bool> DeleteAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = _keyBuilder.BuildExecutionControlKey(executionId);
            var deleted = await _database.KeyDeleteAsync(key).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            return deleted;
        }
    }
}