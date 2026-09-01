using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling.Redis;
using StackExchange.Redis;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Provides Redis Lua script SHA caching with automatic reload on <c>NOSCRIPT</c> errors.
    /// </summary>
    /// <remarks>
    /// Redis script cache is server-side. A script SHA may disappear after Redis restart,
    /// failover, flush, or when the runtime starts using another Redis node.
    /// </remarks>
    public sealed class RedisAiRuntimeScaleOutRequestStoreScriptCache
    {
        /// <summary>
        /// The Redis connection used to load scripts on the server.
        /// </summary>
        private readonly IConnectionMultiplexer connection;

        /// <summary>
        /// Synchronizes Lua script loading and reloading.
        /// </summary>
        private readonly SemaphoreSlim loadLock = new(1, 1);

        /// <summary>
        /// Cached SHA for the atomic create script.
        /// </summary>
        private byte[]? createSha;

        /// <summary>
        /// Cached SHA for the atomic transition script.
        /// </summary>
        private byte[]? transitionSha;

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisAiRuntimeScaleOutRequestStoreScriptCache" /> class.
        /// </summary>
        /// <param name="connection">The Redis connection multiplexer.</param>
        public RedisAiRuntimeScaleOutRequestStoreScriptCache(
            IConnectionMultiplexer connection)
        {
            this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        /// <summary>
        /// Executes the atomic scale-out request create script.
        /// </summary>
        /// <param name="database">The Redis database.</param>
        /// <param name="keys">The Redis keys.</param>
        /// <param name="values">The Redis values.</param>
        /// <returns>The Redis script result.</returns>
        public Task<RedisResult> ExecuteCreateAsync(
            IDatabase database,
            RedisKey[] keys,
            RedisValue[] values)
        {
            return this.ExecuteAsync(
                database,
                ScriptKind.Create,
                RedisAiRuntimeScaleOutRequestStoreScripts.Create,
                keys,
                values);
        }

        /// <summary>
        /// Executes the atomic scale-out request transition script.
        /// </summary>
        /// <param name="database">The Redis database.</param>
        /// <param name="keys">The Redis keys.</param>
        /// <param name="values">The Redis values.</param>
        /// <returns>The Redis script result.</returns>
        public Task<RedisResult> ExecuteTransitionAsync(
            IDatabase database,
            RedisKey[] keys,
            RedisValue[] values)
        {
            return this.ExecuteAsync(
                database,
                ScriptKind.Transition,
                RedisAiRuntimeScaleOutRequestStoreScripts.Transition,
                keys,
                values);
        }

        /// <summary>
        /// Executes a Lua script by SHA and reloads it automatically when Redis reports <c>NOSCRIPT</c>.
        /// </summary>
        /// <param name="database">The Redis database.</param>
        /// <param name="kind">The script kind.</param>
        /// <param name="script">The Lua script text.</param>
        /// <param name="keys">The Redis keys.</param>
        /// <param name="values">The Redis values.</param>
        /// <returns>The Redis script result.</returns>
        private async Task<RedisResult> ExecuteAsync(
            IDatabase database,
            ScriptKind kind,
            string script,
            RedisKey[] keys,
            RedisValue[] values)
        {
            ArgumentNullException.ThrowIfNull(database);

            var sha = await this.GetOrLoadShaAsync(kind, script).ConfigureAwait(false);

            try
            {
                var result = await database
                    .ScriptEvaluateAsync(sha, keys, values)
                    .ConfigureAwait(false);
                Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics.RecordInvocation(
                    database,
                    Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.LuaScaleOutRequest);
                return result;
            }
            catch (RedisServerException exception) when (IsNoScript(exception))
            {
                sha = await this.ReloadShaAsync(kind, script, forceReload: true).ConfigureAwait(false);

                var result = await database
                    .ScriptEvaluateAsync(sha, keys, values)
                    .ConfigureAwait(false);
                Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics.RecordInvocation(
                    database,
                    Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.LuaScaleOutRequest);
                return result;
            }
        }

        /// <summary>
        /// Gets the cached SHA or loads the Lua script into Redis when missing.
        /// </summary>
        /// <param name="kind">The script kind.</param>
        /// <param name="script">The Lua script text.</param>
        /// <returns>The loaded script SHA.</returns>
        private async Task<byte[]> GetOrLoadShaAsync(
            ScriptKind kind,
            string script)
        {
            var current = this.GetSha(kind);

            if (current is not null && current.Length > 0)
            {
                return current;
            }

            return await this.ReloadShaAsync(kind, script, forceReload: false).ConfigureAwait(false);
        }

        /// <summary>
        /// Loads or reloads a Lua script into Redis and updates the cached SHA.
        /// </summary>
        /// <param name="kind">The script kind.</param>
        /// <param name="script">The Lua script text.</param>
        /// <param name="forceReload">Indicates whether the script must be reloaded even when a cached SHA exists.</param>
        /// <returns>The loaded script SHA.</returns>
        private async Task<byte[]> ReloadShaAsync(
            ScriptKind kind,
            string script,
            bool forceReload)
        {
            await this.loadLock.WaitAsync().ConfigureAwait(false);

            try
            {
                var current = this.GetSha(kind);

                if (!forceReload && current is not null && current.Length > 0)
                {
                    return current;
                }

                var endpoint = this.connection.GetEndPoints().FirstOrDefault();

                if (endpoint is null)
                {
                    throw new InvalidOperationException("No Redis endpoint is available for Lua script loading.");
                }

                var server = this.connection.GetServer(endpoint);
                var loaded = await server.ScriptLoadAsync(script).ConfigureAwait(false);

                if (loaded is null || loaded.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Redis returned an empty SHA for scale-out request store script '{kind}'.");
                }

                this.SetSha(kind, loaded);

                return loaded;
            }
            finally
            {
                this.loadLock.Release();
            }
        }

        /// <summary>
        /// Determines whether a Redis exception represents a missing Lua script.
        /// </summary>
        /// <param name="exception">The Redis server exception.</param>
        /// <returns><see langword="true" /> when the exception is <c>NOSCRIPT</c>; otherwise, <see langword="false" />.</returns>
        private static bool IsNoScript(RedisServerException exception)
        {
            return exception.Message.Contains("NOSCRIPT", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets a cached script SHA.
        /// </summary>
        /// <param name="kind">The script kind.</param>
        /// <returns>The cached SHA, or <see langword="null" />.</returns>
        private byte[]? GetSha(ScriptKind kind)
        {
            return kind switch
            {
                ScriptKind.Create => this.createSha,
                ScriptKind.Transition => this.transitionSha,
                _ => null
            };
        }

        /// <summary>
        /// Sets a cached script SHA.
        /// </summary>
        /// <param name="kind">The script kind.</param>
        /// <param name="sha">The loaded script SHA.</param>
        private void SetSha(
            ScriptKind kind,
            byte[] sha)
        {
            switch (kind)
            {
                case ScriptKind.Create:
                    this.createSha = sha;
                    break;

                case ScriptKind.Transition:
                    this.transitionSha = sha;
                    break;
            }
        }

        /// <summary>
        /// Defines known scale-out request store Lua scripts.
        /// </summary>
        private enum ScriptKind
        {
            /// <summary>
            /// Atomic scale-out request create script.
            /// </summary>
            Create = 0,

            /// <summary>
            /// Atomic scale-out request lifecycle transition script.
            /// </summary>
            Transition = 1
        }
    }
}