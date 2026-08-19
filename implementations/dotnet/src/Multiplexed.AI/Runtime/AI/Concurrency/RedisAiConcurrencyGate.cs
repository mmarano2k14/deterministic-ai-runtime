using System.Text.Json;
using System.Text.Json.Serialization;
using Multiplexed.Abstractions.AI.Concurrency;
using StackExchange.Redis;

namespace Multiplexed.AI.Runtime.AI.Concurrency
{
    /// <summary>
    /// Provides a Redis-backed distributed concurrency gate for AI runtime step execution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This gate is responsible for acquiring and releasing distributed execution capacity
    /// before a DAG step is claimed and executed.
    /// </para>
    ///
    /// <para>
    /// The implementation is intentionally separated from the DAG claim mechanism:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// The concurrency engine decides whether a step is eligible from a policy/configuration perspective.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// This Redis gate acquires distributed capacity across all configured concurrency scopes.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// The DAG execution store remains responsible for atomically claiming the actual step ownership.
    /// </description>
    /// </item>
    /// </list>
    ///
    /// <para>
    /// The gate uses one Redis sorted set per concurrency scope. Each active lease is represented as:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item>
    /// <description><c>member</c>: the lease identifier.</description>
    /// </item>
    /// <item>
    /// <description><c>score</c>: the lease expiration timestamp in Unix milliseconds.</description>
    /// </item>
    /// </list>
    ///
    /// <para>
    /// On every acquisition attempt, expired leases are removed before active leases are counted.
    /// This makes the gate crash-safe: if a worker crashes before releasing its lease, the expired
    /// lease is eventually removed by the next acquisition attempt and capacity is recovered without
    /// relying on fragile counters.
    /// </para>
    ///
    /// <para>
    /// When capacity cannot be acquired, Redis returns a detailed denial payload containing the
    /// blocking scope key, the current active lease count, and the configured limit. This makes
    /// throttling decisions observable and easier to diagnose in distributed environments.
    /// </para>
    ///
    /// <para>
    /// Supported distributed scopes include:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item>
    /// <description><c>global</c>: limits the entire runtime across all workers and instances.</description>
    /// </item>
    /// <item>
    /// <description><c>pipeline</c>: limits all executions of a specific pipeline.</description>
    /// </item>
    /// <item>
    /// <description><c>pipeline-step</c>: limits a logical step within a specific pipeline.</description>
    /// </item>
    /// <item>
    /// <description><c>execution</c>: limits parallel step execution inside a single DAG execution.</description>
    /// </item>
    /// <item>
    /// <description><c>instance</c>: limits capacity consumed by a specific runtime instance.</description>
    /// </item>
    /// </list>
    ///
    /// <para>
    /// The <c>pipeline-step</c> scope intentionally combines the pipeline key and step key.
    /// This avoids accidentally throttling unrelated pipelines that use the same logical step key.
    /// </para>
    /// </remarks>
    public sealed class RedisAiConcurrencyGate : IAiConcurrencyGate
    {
        private static readonly JsonSerializerOptions GateResultJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly LuaScript AcquirePreparedScript = LuaScript.Prepare(
            """
            local scopes = cjson.decode(@scopesJson)
            local leaseId = @leaseId
            local nowMs = tonumber(@nowMs)
            local expiresAtMs = tonumber(@expiresAtMs)

            -- First pass:
            -- Remove expired leases and validate every configured scope before acquiring anything.
            -- This preserves all-or-nothing admission semantics.
            for i, scope in ipairs(scopes) do
                local key = scope[1]
                local limit = tonumber(scope[2])

                if key ~= nil and limit ~= nil and limit > 0 then
                    redis.call('ZREMRANGEBYSCORE', key, '-inf', nowMs)

                    local exists = redis.call('ZSCORE', key, leaseId)
                    if not exists then
                        local current = redis.call('ZCARD', key)

                        if current >= limit then
                            return cjson.encode({
                                allowed = false,
                                blockedKey = key,
                                current = current,
                                limit = limit
                            })
                        end
                    end
                end
            end

            -- Second pass:
            -- Acquire or refresh the same lease id in every configured scope.
            -- Using the same lease id makes repeated acquisition attempts idempotent.
            for i, scope in ipairs(scopes) do
                local key = scope[1]
                local limit = tonumber(scope[2])

                if key ~= nil and limit ~= nil and limit > 0 then
                    redis.call('ZADD', key, expiresAtMs, leaseId)
                end
            end

            return cjson.encode({
                allowed = true
            })
            """);

        private static readonly LuaScript ReleasePreparedScript = LuaScript.Prepare(
            """
            local scopes = cjson.decode(@scopesJson)
            local leaseId = @leaseId

            for i, scope in ipairs(scopes) do
                local key = scope[1]

                if key ~= nil then
                    redis.call('ZREM', key, leaseId)
                end
            end

            return 1
            """);

        private readonly IDatabase _database;

        private LuaScript _acquireScript;
        private LuaScript _releaseScript;

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisAiConcurrencyGate"/> class.
        /// </summary>
        /// <param name="multiplexer">
        /// The Redis connection multiplexer used to access the Redis database.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="multiplexer"/> is <see langword="null"/>.
        /// </exception>
        public RedisAiConcurrencyGate(IConnectionMultiplexer multiplexer)
        {
            ArgumentNullException.ThrowIfNull(multiplexer);

            _database = multiplexer.GetDatabase();

            _acquireScript = AcquirePreparedScript;
            _releaseScript = ReleasePreparedScript;
        }

        /// <summary>
        /// Attempts to acquire distributed concurrency capacity for the supplied execution context.
        /// </summary>
        /// <param name="context">
        /// The concurrency context describing the execution, pipeline, step, runtime instance,
        /// and lease identity participating in admission control.
        /// </param>
        /// <param name="definition">
        /// The resolved concurrency definition containing the enabled flag, concurrency limits,
        /// lease duration, and retry-after behavior.
        /// </param>
        /// <param name="cancellationToken">
        /// A cancellation token used by the caller to cancel the admission operation.
        /// </param>
        /// <returns>
        /// An <see cref="AiConcurrencyDecision"/> allowing execution when all configured scope limits
        /// have available capacity; otherwise, a denied decision with retry-after metadata and a
        /// diagnostic reason describing the blocking scope.
        /// </returns>
        /// <remarks>
        /// <para>
        /// The acquisition is atomic across all configured scopes. If any scope is at capacity,
        /// no lease is acquired in any scope.
        /// </para>
        ///
        /// <para>
        /// The same lease id is added to every applicable scope. This makes release simple and keeps
        /// the acquired capacity tied to the lifecycle of one claimed step execution.
        /// </para>
        ///
        /// <para>
        /// If a worker crashes after acquisition, the lease is not immediately removed. Instead,
        /// it naturally expires based on its score and is removed during a future acquisition attempt.
        /// This prevents permanent capacity leaks.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="context"/> or <paramref name="definition"/> is <see langword="null"/>.
        /// </exception>
        public async Task<AiConcurrencyDecision> TryAcquireAsync(
            AiConcurrencyContext context,
            AiConcurrencyDefinition definition,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(definition);

            if (!definition.Enabled)
            {
                return AiConcurrencyDecision.Allow();
            }

            var scopes = BuildScopes(context, definition);

            if (scopes.Count == 0)
            {
                return AiConcurrencyDecision.Allow();
            }

            var leaseSeconds = Math.Max(1, definition.LeaseSeconds);
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var expiresAtMs = nowMs + TimeSpan.FromSeconds(leaseSeconds).TotalMilliseconds;

            var scopesJson = SerializeScopes(scopes);

            try
            {
                return await TryAcquireAsync(
                        scopesJson,
                        context.LeaseId,
                        nowMs,
                        (long)expiresAtMs,
                        definition)
                    .ConfigureAwait(false);
            }
            catch (RedisServerException ex) when (ex.Message.Contains("NOSCRIPT", StringComparison.OrdinalIgnoreCase))
            {
                _acquireScript = AcquirePreparedScript;

                return await TryAcquireAsync(
                        scopesJson,
                        context.LeaseId,
                        nowMs,
                        (long)expiresAtMs,
                        definition)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Releases previously acquired distributed concurrency capacity for the supplied context.
        /// </summary>
        /// <param name="context">
        /// The concurrency context containing the lease id and the scope identity used during acquisition.
        /// </param>
        /// <param name="definition">
        /// The resolved concurrency definition used to rebuild the same scope keys that were used
        /// during acquisition.
        /// </param>
        /// <param name="cancellationToken">
        /// A cancellation token used by the caller to cancel the release operation.
        /// </param>
        /// <returns>
        /// A task representing the asynchronous release operation.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Release removes the lease id from every configured scope.
        /// </para>
        ///
        /// <para>
        /// Callers should release capacity after the claimed step completes, fails, or is cancelled.
        /// If capacity is acquired but the DAG step claim fails, callers should also release immediately
        /// to avoid holding capacity for a step that was never executed.
        /// </para>
        ///
        /// <para>
        /// Release is idempotent. Removing a lease id that no longer exists has no effect.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="context"/> or <paramref name="definition"/> is <see langword="null"/>.
        /// </exception>
        public async Task ReleaseAsync(
            AiConcurrencyContext context,
            AiConcurrencyDefinition definition,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(definition);

            if (!definition.Enabled)
            {
                return;
            }

            var scopes = BuildScopes(context, definition);

            if (scopes.Count == 0)
            {
                return;
            }

            var scopesJson = SerializeScopes(scopes);

            try
            {
                await ReleaseAsync(scopesJson, context.LeaseId).ConfigureAwait(false);
            }
            catch (RedisServerException ex) when (ex.Message.Contains("NOSCRIPT", StringComparison.OrdinalIgnoreCase))
            {
                _releaseScript = ReleasePreparedScript;
                await ReleaseAsync(scopesJson, context.LeaseId).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Executes the loaded Redis Lua acquisition script.
        /// </summary>
        /// <param name="scopesJson">
        /// The serialized list of Redis concurrency scopes and their limits.
        /// </param>
        /// <param name="leaseId">
        /// The lease identifier to acquire or refresh in every configured scope.
        /// </param>
        /// <param name="nowMs">
        /// The current Unix timestamp in milliseconds.
        /// </param>
        /// <param name="expiresAtMs">
        /// The Unix timestamp in milliseconds at which the lease should be considered expired.
        /// </param>
        /// <param name="definition">
        /// The concurrency definition used to create the deny decision when capacity is unavailable.
        /// </param>
        /// <returns>
        /// An allowed decision when capacity was acquired; otherwise, a denied decision containing
        /// diagnostic information about the blocking scope.
        /// </returns>
        private async Task<AiConcurrencyDecision> TryAcquireAsync(
            string scopesJson,
            string leaseId,
            long nowMs,
            long expiresAtMs,
            AiConcurrencyDefinition definition)
        {
            var result = await _acquireScript.EvaluateAsync(
                _database,
                new
                {
                    scopesJson = (RedisValue)scopesJson,
                    leaseId = (RedisValue)leaseId,
                    nowMs = (RedisValue)nowMs,
                    expiresAtMs = (RedisValue)expiresAtMs
                }).ConfigureAwait(false);

            var gateResult = DeserializeGateResult(result);

            if (gateResult.Allowed)
            {
                return AiConcurrencyDecision.Allow();
            }

            return AiConcurrencyDecision.Deny(
                CreateDeniedReason(gateResult),
                TimeSpan.FromMilliseconds(definition.DefaultRetryAfterMs));
        }

        /// <summary>
        /// Executes the loaded Redis Lua release script.
        /// </summary>
        /// <param name="scopesJson">
        /// The serialized list of Redis concurrency scopes from which the lease should be removed.
        /// </param>
        /// <param name="leaseId">
        /// The lease identifier to remove from every configured scope.
        /// </param>
        /// <returns>
        /// A task representing the asynchronous Redis script execution.
        /// </returns>
        private async Task ReleaseAsync(
            string scopesJson,
            string leaseId)
        {
            await _releaseScript.EvaluateAsync(
                _database,
                new
                {
                    scopesJson = (RedisValue)scopesJson,
                    leaseId = (RedisValue)leaseId
                }).ConfigureAwait(false);
        }

        /// <summary>
        /// Builds the Redis concurrency scopes required by the resolved concurrency definition.
        /// </summary>
        /// <param name="context">
        /// The concurrency context containing execution, pipeline, step, runtime instance,
        /// provider, model, and operation identifiers.
        /// </param>
        /// <param name="definition">
        /// The resolved concurrency definition containing optional limits for each supported scope.
        /// </param>
        /// <returns>
        /// A list of concurrency scopes that should participate in distributed admission control.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Only scopes with a positive configured limit are included.
        /// </para>
        /// <para>
        /// Provider, model, and operation limits are scoped by execution identifier.
        /// This ensures that concurrency configured for one DAG run does not throttle
        /// unrelated executions using the same provider, model, or operation.
        /// </para>
        /// <para>
        /// The actual single-step ownership guarantee still belongs to the DAG claim operation.
        /// This gate only controls distributed capacity, not step ownership.
        /// </para>
        /// </remarks>
        private static List<ConcurrencyScope> BuildScopes(
            AiConcurrencyContext context,
            AiConcurrencyDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentException.ThrowIfNullOrWhiteSpace(context.ExecutionId);

            var scopes =
                new List<ConcurrencyScope>();

            AddScope(
                scopes,
                "ai:concurrency:scope:global",
                definition.MaxGlobalConcurrency);

            AddScope(
                scopes,
                $"ai:concurrency:scope:pipeline:{context.PipelineKey}",
                definition.MaxPipelineConcurrency);

            AddScope(
                scopes,
                $"ai:concurrency:scope:pipeline-step:{context.PipelineKey}:{context.StepKey}",
                definition.MaxStepConcurrency);

            AddScope(
                scopes,
                $"ai:concurrency:scope:provider:{context.ExecutionId}:{context.Provider}",
                definition.MaxProviderConcurrency,
                context.ExecutionId,
                context.Provider);

            AddScope(
                scopes,
                $"ai:concurrency:scope:model:{context.ExecutionId}:{context.Provider}:{context.Model}",
                definition.MaxModelConcurrency,
                context.ExecutionId,
                context.Provider,
                context.Model);

            AddScope(
                scopes,
                $"ai:concurrency:scope:operation:{context.ExecutionId}:{context.Operation}",
                definition.MaxOperationConcurrency,
                context.ExecutionId,
                context.Operation);

            AddScope(
                scopes,
                $"ai:concurrency:scope:execution:{context.ExecutionId}",
                definition.MaxExecutionConcurrency);

            AddScope(
                scopes,
                $"ai:concurrency:scope:instance:{context.RuntimeInstanceId}",
                definition.MaxInstanceConcurrency);

            return scopes;
        }

        /// <summary>
        /// Adds a concurrency scope when its configured limit is positive.
        /// </summary>
        /// <param name="scopes">
        /// The collection to which the scope should be added.
        /// </param>
        /// <param name="key">
        /// The Redis sorted set key representing the concurrency scope.
        /// </param>
        /// <param name="limit">
        /// The maximum number of active leases allowed for the scope.
        /// </param>
        private static void AddScope(
            ICollection<ConcurrencyScope> scopes,
            string key,
            int? limit)
        {
            if (!limit.HasValue || limit.Value <= 0)
            {
                return;
            }

            scopes.Add(new ConcurrencyScope(key, limit.Value));
        }

        /// <summary>
        /// Adds a concurrency scope when its configured limit is positive and all required scope parts are present.
        /// </summary>
        /// <param name="scopes">
        /// The collection to which the scope should be added.
        /// </param>
        /// <param name="key">
        /// The Redis sorted set key representing the concurrency scope.
        /// </param>
        /// <param name="limit">
        /// The maximum number of active leases allowed for the scope.
        /// </param>
        /// <param name="requiredParts">
        /// The required context values needed to make this scope meaningful.
        /// </param>
        /// <remarks>
        /// <para>
        /// This overload is used for optional scopes such as provider, model, and operation.
        /// </para>
        ///
        /// <para>
        /// For example, model-level throttling requires both provider and model to be present.
        /// If either value is missing, the model scope is skipped instead of creating an invalid key.
        /// </para>
        /// </remarks>
        private static void AddScope(
            ICollection<ConcurrencyScope> scopes,
            string key,
            int? limit,
            params string?[] requiredParts)
        {
            if (!limit.HasValue || limit.Value <= 0)
            {
                return;
            }

            if (requiredParts.Any(string.IsNullOrWhiteSpace))
            {
                return;
            }

            scopes.Add(new ConcurrencyScope(key, limit.Value));
        }


        /// <summary>
        /// Serializes concurrency scopes as positional arrays for Lua consumption.
        /// </summary>
        /// <param name="scopes">
        /// The scopes to serialize.
        /// </param>
        /// <returns>
        /// A JSON payload where each scope is represented as <c>[key, limit]</c>.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This intentionally avoids object property name casing issues between C# JSON serialization
        /// and Lua JSON decoding. Lua reads <c>scope[1]</c> as the Redis key and <c>scope[2]</c>
        /// as the numeric limit.
        /// </para>
        /// </remarks>
        private static string SerializeScopes(IEnumerable<ConcurrencyScope> scopes)
        {
            return JsonSerializer.Serialize(
                scopes.Select(scope => scope.ToJsonArray()));
        }

        /// <summary>
        /// Deserializes the JSON response returned by the Redis Lua acquisition script.
        /// </summary>
        /// <param name="result">
        /// The Redis script result.
        /// </param>
        /// <returns>
        /// The deserialized gate result.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the Redis Lua script returns an empty or invalid response.
        /// </exception>
        private static RedisConcurrencyGateResult DeserializeGateResult(RedisResult result)
        {
            var json = result.ToString();

            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidOperationException(
                    "Redis concurrency gate returned an empty acquisition result.");
            }

            var gateResult = JsonSerializer.Deserialize<RedisConcurrencyGateResult>(
                json,
                GateResultJsonOptions);

            if (gateResult is null)
            {
                throw new InvalidOperationException(
                    $"Redis concurrency gate returned an invalid acquisition result: {json}");
            }

            return gateResult;
        }

        /// <summary>
        /// Creates a human-readable denial reason from a Redis concurrency gate result.
        /// </summary>
        /// <param name="gateResult">
        /// The denied gate result returned by Redis.
        /// </param>
        /// <returns>
        /// A diagnostic reason describing which scope blocked the acquisition.
        /// </returns>
        private static string CreateDeniedReason(RedisConcurrencyGateResult gateResult)
        {
            if (string.IsNullOrWhiteSpace(gateResult.BlockedKey))
            {
                return "Concurrency limit reached.";
            }

            return
                $"Concurrency limit reached for scope '{gateResult.BlockedKey}'. " +
                $"Current='{gateResult.Current}', Limit='{gateResult.Limit}'.";
        }

        /// <summary>
        /// Represents a Redis concurrency scope and its maximum active lease limit.
        /// </summary>
        /// <param name="Key">
        /// The Redis sorted set key used to track active leases for this scope.
        /// </param>
        /// <param name="Limit">
        /// The maximum number of active leases allowed in this scope.
        /// </param>
        private sealed record ConcurrencyScope(string Key, int Limit)
        {
            /// <summary>
            /// Converts the scope into a positional array consumed by Redis Lua scripts.
            /// </summary>
            /// <returns>
            /// An array in the form <c>[key, limit]</c>.
            /// </returns>
            public object[] ToJsonArray()
            {
                return [Key, Limit];
            }
        }

        /// <summary>
        /// Represents the structured result returned by the Redis Lua acquisition script.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The Lua script returns JSON instead of a simple integer so the runtime can expose
        /// useful diagnostic information when admission is denied.
        /// </para>
        /// </remarks>
        private sealed class RedisConcurrencyGateResult
        {
            /// <summary>
            /// Gets or sets a value indicating whether distributed capacity was acquired.
            /// </summary>
            [JsonPropertyName("allowed")]
            public bool Allowed { get; set; }

            /// <summary>
            /// Gets or sets the Redis scope key that blocked acquisition.
            /// </summary>
            [JsonPropertyName("blockedKey")]
            public string? BlockedKey { get; set; }

            /// <summary>
            /// Gets or sets the current number of active leases in the blocking scope.
            /// </summary>
            [JsonPropertyName("current")]
            public long? Current { get; set; }

            /// <summary>
            /// Gets or sets the configured concurrency limit for the blocking scope.
            /// </summary>
            [JsonPropertyName("limit")]
            public long? Limit { get; set; }
        }
    }
}