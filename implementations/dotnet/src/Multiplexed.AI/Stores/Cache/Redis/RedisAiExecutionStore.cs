using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.AI.Stores.Creation;
using StackExchange.Redis;

namespace Multiplexed.AI.Stores.Cache.Redis
{
    /// <summary>
    /// Redis-backed implementation of <see cref="IAiExecutionStore"/>.
    ///
    /// This store provides:
    /// - Primary persistence for execution records and states
    /// - Atomic compare-and-swap updates using Redis Lua scripting
    /// - Atomic restore for replay / recovery flows
    ///
    /// Key design goals:
    /// - Ensure consistency under concurrency
    /// - Prevent multiple workers from advancing the same execution step
    /// - Keep record/state updates strictly aligned
    /// </summary>
    public sealed class RedisAiExecutionStore : IAiExecutionStore, IAiExecutionCreateIfAbsentStore
    {
        private readonly IConnectionMultiplexer _multiplexer;
        private readonly IDatabase _database;
        private readonly IAiExecutionKeyBuilder _keyBuilder;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// Prepared Lua script using named parameters.
        ///
        /// IMPORTANT:
        /// - Uses @param syntax (NOT KEYS/ARGV)
        /// - Compatible with StackExchange.Redis LuaScript.Prepare
        /// </summary>
        private static readonly LuaScript TryUpdatePreparedScript = LuaScript.Prepare(
            """
            local currentRecordJson = redis.call('GET', @recordKey)
            if not currentRecordJson then
                return 0
            end

            local currentRecord = cjson.decode(currentRecordJson)
            if not currentRecord then
                return 0
            end

            -- Optimistic concurrency check
            if currentRecord.ExecutionStepKey ~= @expectedStepKey then
                return 0
            end

            -- Apply update atomically
            redis.call('SET', @recordKey, @newRecordJson)
            redis.call('SET', @stateKey, @newStateJson)

            return 1
            """);

        /// <summary>
        /// Prepared Lua script used to restore record and state atomically.
        ///
        /// PURPOSE:
        /// - Used by replay / recovery flows
        /// - Ensures record and state are restored together
        /// - Avoids partial restore if a process crashes between two writes
        /// </summary>
        private static readonly LuaScript RestorePreparedScript = LuaScript.Prepare(
            """
            redis.call('SET', @recordKey, @recordJson)
            redis.call('SET', @stateKey, @stateJson)

            return 1
            """);

        /// <summary>
        /// Loaded script bound to Redis server (executed via SHA).
        /// </summary>
        private LoadedLuaScript _tryUpdateLoadedScript;

        /// <summary>
        /// Loaded restore script bound to Redis server (executed via SHA).
        /// </summary>
        private LoadedLuaScript _restoreLoadedScript;

        /// <summary>
        /// Initializes a new instance of the Redis execution store.
        /// </summary>
        public RedisAiExecutionStore(
            IConnectionMultiplexer multiplexer,
            IAiExecutionKeyBuilder keyBuilder)
        {
            ArgumentNullException.ThrowIfNull(multiplexer);
            ArgumentNullException.ThrowIfNull(keyBuilder);

            _multiplexer = multiplexer;
            _database = multiplexer.GetDatabase();
            _keyBuilder = keyBuilder;

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = false
            };

            _tryUpdateLoadedScript = LoadTryUpdateScript();
            _restoreLoadedScript = LoadRestoreScript();
        }

        /// <summary>
        /// Creates a new execution record and state.
        ///
        /// This operation overwrites existing values.
        /// Intended for initialization only.
        /// </summary>
        public async Task CreateAsync(
            AiExecutionRecord record,
            AiExecutionState state,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(state);

            if (!string.Equals(record.ExecutionId, state.ExecutionId, StringComparison.Ordinal))
                throw new ArgumentException("Record and State must share the same ExecutionId.");

            var recordKey = _keyBuilder.GetExecutionRecordKey(record.ExecutionId);
            var stateKey = _keyBuilder.GetExecutionStateKey(state.ExecutionId);

            var recordPayload = JsonSerializer.Serialize(record, _jsonOptions);
            var statePayload = JsonSerializer.Serialize(state, _jsonOptions);

            await _database.StringSetAsync(recordKey, recordPayload);
            await _database.StringSetAsync(stateKey, statePayload);
        }

        /// <summary>
        /// Atomically creates one exact execution record/state pair when both Redis keys are absent.
        /// </summary>
        /// <param name="record">The execution record carrying the exact identifier.</param>
        /// <param name="state">The execution state carrying the same exact identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><c>true</c> when the pair was created; otherwise <c>false</c>.</returns>
        public async Task<bool> TryCreateIfAbsentAsync(
            AiExecutionRecord record,
            AiExecutionState state,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(state);

            if (!string.Equals(record.ExecutionId, state.ExecutionId, StringComparison.Ordinal))
            {
                throw new ArgumentException("Record and State must share the same ExecutionId.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var recordKey = _keyBuilder.GetExecutionRecordKey(record.ExecutionId);
            var stateKey = _keyBuilder.GetExecutionStateKey(state.ExecutionId);
            var transaction = _database.CreateTransaction();

            transaction.AddCondition(Condition.KeyNotExists(recordKey));
            transaction.AddCondition(Condition.KeyNotExists(stateKey));

            _ = transaction.StringSetAsync(
                recordKey,
                JsonSerializer.Serialize(record, _jsonOptions));

            _ = transaction.StringSetAsync(
                stateKey,
                JsonSerializer.Serialize(state, _jsonOptions));

            return await transaction.ExecuteAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Retrieves an execution record.
        /// </summary>
        public async Task<AiExecutionRecord?> GetRecordAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));

            var value = await _database.StringGetAsync(_keyBuilder.GetExecutionRecordKey(executionId));

            if (!value.HasValue)
                return null;

            return JsonSerializer.Deserialize<AiExecutionRecord>((string)value!, _jsonOptions);
        }

        /// <summary>
        /// Retrieves an execution state.
        /// </summary>
        public async Task<AiExecutionState?> GetStateAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));

            var value = await _database.StringGetAsync(_keyBuilder.GetExecutionStateKey(executionId));

            if (!value.HasValue)
                return null;

            return JsonSerializer.Deserialize<AiExecutionState>((string)value!, _jsonOptions);
        }

        /// <summary>
        /// Attempts to update the execution record and state atomically.
        ///
        /// Uses optimistic concurrency with ExecutionStepKey:
        /// - Only one caller can update for a given step key
        /// - Prevents duplicate execution
        /// </summary>
        public async Task<bool> TryUpdateAsync(
            string executionId,
            string expectedStepKey,
            AiExecutionRecord record,
            AiExecutionState state,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));

            if (string.IsNullOrWhiteSpace(expectedStepKey))
                throw new ArgumentException("Expected step key cannot be null or empty.", nameof(expectedStepKey));

            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(state);

            var recordKey = _keyBuilder.GetExecutionRecordKey(executionId);
            var stateKey = _keyBuilder.GetExecutionStateKey(executionId);

            var recordPayload = JsonSerializer.Serialize(record, _jsonOptions);
            var statePayload = JsonSerializer.Serialize(state, _jsonOptions);

            try
            {
                var result = await _database.ScriptEvaluateAsync(
                    _tryUpdateLoadedScript,
                    new
                    {
                        recordKey = (RedisKey)recordKey,
                        stateKey = (RedisKey)stateKey,
                        expectedStepKey = (RedisValue)expectedStepKey,
                        newRecordJson = (RedisValue)recordPayload,
                        newStateJson = (RedisValue)statePayload
                    });

                return (int)result! == 1;
            }
            catch (RedisServerException ex) when (ex.Message.Contains("NOSCRIPT", StringComparison.OrdinalIgnoreCase))
            {
                _tryUpdateLoadedScript = LoadTryUpdateScript();

                var result = await _database.ScriptEvaluateAsync(
                    _tryUpdateLoadedScript,
                    new
                    {
                        recordKey = (RedisKey)recordKey,
                        stateKey = (RedisKey)stateKey,
                        expectedStepKey = (RedisValue)expectedStepKey,
                        newRecordJson = (RedisValue)recordPayload,
                        newStateJson = (RedisValue)statePayload
                    });

                return (int)result! == 1;
            }
        }

        /// <summary>
        /// Saves an execution record independently.
        ///
        /// This operation overwrites the current persisted record value.
        /// </summary>
        public async Task SaveRecordAsync(
            AiExecutionRecord record,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);

            var recordPayload = JsonSerializer.Serialize(record, _jsonOptions);

            await _database.StringSetAsync(
                _keyBuilder.GetExecutionRecordKey(record.ExecutionId),
                recordPayload);
        }

        /// <summary>
        /// Saves an execution state independently.
        ///
        /// This operation overwrites the current persisted state value.
        /// </summary>
        public async Task SaveStateAsync(
            string executionId,
            AiExecutionState state,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));

            ArgumentNullException.ThrowIfNull(state);

            var statePayload = JsonSerializer.Serialize(state, _jsonOptions);

            await _database.StringSetAsync(
                _keyBuilder.GetExecutionStateKey(executionId),
                statePayload);
        }

        /// <summary>
        /// Deletes an execution record.
        ///
        /// This operation is idempotent. If the key does not exist,
        /// the method completes successfully without throwing.
        /// </summary>
        public async Task DeleteRecordAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));

            await _database.KeyDeleteAsync(_keyBuilder.GetExecutionRecordKey(executionId));
        }

        /// <summary>
        /// Deletes an execution state.
        ///
        /// This operation is idempotent. If the key does not exist,
        /// the method completes successfully without throwing.
        /// </summary>
        public async Task DeleteStateAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));

            await _database.KeyDeleteAsync(_keyBuilder.GetExecutionStateKey(executionId));
        }

        /// <summary>
        /// Restores an execution record and state atomically without optimistic concurrency checks.
        ///
        /// PURPOSE:
        /// - Used by replay / recovery flows
        /// - Overwrites the persisted record and state directly
        ///
        /// BEHAVIOR:
        /// - Record and state must share the same execution identifier
        /// - Existing values are replaced atomically inside Redis
        /// - No expected step key is required
        /// </summary>
        public async Task RestoreAsync(
            AiExecutionRecord record,
            AiExecutionState state,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(state);

            if (string.IsNullOrWhiteSpace(record.ExecutionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(record));

            if (string.IsNullOrWhiteSpace(state.ExecutionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(state));

            if (!string.Equals(record.ExecutionId, state.ExecutionId, StringComparison.Ordinal))
                throw new ArgumentException("Record and State must share the same ExecutionId.");

            var recordKey = _keyBuilder.GetExecutionRecordKey(record.ExecutionId);
            var stateKey = _keyBuilder.GetExecutionStateKey(state.ExecutionId);

            var recordPayload = JsonSerializer.Serialize(record, _jsonOptions);
            var statePayload = JsonSerializer.Serialize(state, _jsonOptions);

            try
            {
                var result = await _database.ScriptEvaluateAsync(
                    _restoreLoadedScript,
                    new
                    {
                        recordKey = (RedisKey)recordKey,
                        stateKey = (RedisKey)stateKey,
                        recordJson = (RedisValue)recordPayload,
                        stateJson = (RedisValue)statePayload
                    });

                if ((int)result! != 1)
                {
                    throw new InvalidOperationException(
                        $"Redis restore script returned unexpected result for execution '{record.ExecutionId}'.");
                }
            }
            catch (RedisServerException ex) when (ex.Message.Contains("NOSCRIPT", StringComparison.OrdinalIgnoreCase))
            {
                _restoreLoadedScript = LoadRestoreScript();

                var result = await _database.ScriptEvaluateAsync(
                    _restoreLoadedScript,
                    new
                    {
                        recordKey = (RedisKey)recordKey,
                        stateKey = (RedisKey)stateKey,
                        recordJson = (RedisValue)recordPayload,
                        stateJson = (RedisValue)statePayload
                    });

                if ((int)result! != 1)
                {
                    throw new InvalidOperationException(
                        $"Redis restore script returned unexpected result for execution '{record.ExecutionId}' after script reload.");
                }
            }
        }

        /// <summary>
        /// Loads the Lua script into Redis and returns the SHA-bound instance.
        /// </summary>
        private LoadedLuaScript LoadTryUpdateScript()
        {
            var endpoint = _multiplexer.GetEndPoints().First();
            var server = _multiplexer.GetServer(endpoint);

            return TryUpdatePreparedScript.Load(server);
        }

        /// <summary>
        /// Loads the restore Lua script into Redis and returns the SHA-bound instance.
        /// </summary>
        private LoadedLuaScript LoadRestoreScript()
        {
            var endpoint = _multiplexer.GetEndPoints().First();
            var server = _multiplexer.GetServer(endpoint);

            return RestorePreparedScript.Load(server);
        }
    }
}