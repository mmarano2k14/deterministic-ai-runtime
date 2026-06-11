using StackExchange.Redis;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Store
{
    /// <summary>
    /// Provides Redis Lua script SHA caching with automatic reload on NOSCRIPT errors.
    /// </summary>
    /// <remarks>
    /// Redis script cache is server-side.
    /// A script SHA may disappear after Redis restart, failover, flush, or when using another node.
    ///
    /// This helper keeps the runtime code clean by:
    /// - loading scripts only when needed
    /// - executing by SHA when possible
    /// - falling back to SCRIPT LOAD when Redis returns NOSCRIPT
    /// </remarks>
    public sealed class RedisAiSharedRunStoreScriptCache
    {
        private readonly IConnectionMultiplexer _connection;
        private readonly SemaphoreSlim _loadLock = new(1, 1);

        private byte[]? _createSha;
        private byte[]? _cancelSha;
        private byte[]? _markDispatchedSha;

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisAiSharedRunStoreScriptCache"/> class.
        /// </summary>
        /// <param name="connection">The Redis connection multiplexer.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="connection"/> is null.
        /// </exception>
        public RedisAiSharedRunStoreScriptCache(
            IConnectionMultiplexer connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        /// <summary>
        /// Executes the atomic shared run create script.
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
            return ExecuteAsync(
                database,
                ScriptKind.Create,
                RedisAiSharedRunStoreScripts.Create,
                keys,
                values);
        }

        /// <summary>
        /// Executes the atomic shared run cancel script.
        /// </summary>
        /// <param name="database">The Redis database.</param>
        /// <param name="keys">The Redis keys.</param>
        /// <param name="values">The Redis values.</param>
        /// <returns>The Redis script result.</returns>
        public Task<RedisResult> ExecuteCancelAsync(
            IDatabase database,
            RedisKey[] keys,
            RedisValue[] values)
        {
            return ExecuteAsync(
                database,
                ScriptKind.Cancel,
                RedisAiSharedRunStoreScripts.Cancel,
                keys,
                values);
        }

        /// <summary>
        /// Executes the atomic shared run mark-dispatched script.
        /// </summary>
        /// <param name="database">The Redis database.</param>
        /// <param name="keys">The Redis keys.</param>
        /// <param name="values">The Redis values.</param>
        /// <returns>The Redis script result.</returns>
        public Task<RedisResult> ExecuteMarkDispatchedAsync(
            IDatabase database,
            RedisKey[] keys,
            RedisValue[] values)
        {
            return ExecuteAsync(
                database,
                ScriptKind.MarkDispatched,
                RedisAiSharedRunStoreScripts.MarkDispatched,
                keys,
                values);
        }

        /// <summary>
        /// Executes a Lua script by SHA and reloads it automatically when Redis reports NOSCRIPT.
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

            var sha = await GetOrLoadShaAsync(
                    kind,
                    script)
                .ConfigureAwait(false);

            try
            {
                return await database
                    .ScriptEvaluateAsync(
                        sha,
                        keys,
                        values)
                    .ConfigureAwait(false);
            }
            catch (RedisServerException exception) when (IsNoScript(exception))
            {
                sha = await ReloadShaAsync(
                        kind,
                        script,
                        forceReload: true)
                    .ConfigureAwait(false);

                return await database
                    .ScriptEvaluateAsync(
                        sha,
                        keys,
                        values)
                    .ConfigureAwait(false);
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
            var current = GetSha(kind);

            if (current is not null &&
                current.Length > 0)
            {
                return current;
            }

            return await ReloadShaAsync(
                    kind,
                    script,
                    forceReload: false)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Loads or reloads a Lua script into Redis and updates the cached SHA.
        /// </summary>
        /// <param name="kind">The script kind.</param>
        /// <param name="script">The Lua script text.</param>
        /// <param name="forceReload">
        /// Indicates whether the script must be reloaded even when a cached SHA exists.
        /// </param>
        /// <returns>The loaded script SHA.</returns>
        private async Task<byte[]> ReloadShaAsync(
            ScriptKind kind,
            string script,
            bool forceReload)
        {
            await _loadLock.WaitAsync().ConfigureAwait(false);

            try
            {
                var current = GetSha(kind);

                if (!forceReload &&
                    current is not null &&
                    current.Length > 0)
                {
                    return current;
                }

                var endpoint = _connection.GetEndPoints().FirstOrDefault();

                if (endpoint is null)
                {
                    throw new InvalidOperationException(
                        "No Redis endpoint is available for Lua script loading.");
                }

                var server = _connection.GetServer(endpoint);

                var loaded = await server
                    .ScriptLoadAsync(script)
                    .ConfigureAwait(false);

                if (loaded is null ||
                    loaded.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Redis returned an empty SHA for shared run store script '{kind}'.");
                }

                SetSha(kind, loaded);

                return loaded;
            }
            finally
            {
                _loadLock.Release();
            }
        }

        /// <summary>
        /// Determines whether a Redis exception represents a missing Lua script.
        /// </summary>
        /// <param name="exception">The Redis server exception.</param>
        /// <returns><c>true</c> when the exception is NOSCRIPT; otherwise, <c>false</c>.</returns>
        private static bool IsNoScript(
            RedisServerException exception)
        {
            return exception.Message.Contains(
                "NOSCRIPT",
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets a cached script SHA.
        /// </summary>
        /// <param name="kind">The script kind.</param>
        /// <returns>The cached SHA, or <c>null</c>.</returns>
        private byte[]? GetSha(
            ScriptKind kind)
        {
            return kind switch
            {
                ScriptKind.Create => _createSha,
                ScriptKind.Cancel => _cancelSha,
                ScriptKind.MarkDispatched => _markDispatchedSha,
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
                    _createSha = sha;
                    break;

                case ScriptKind.Cancel:
                    _cancelSha = sha;
                    break;

                case ScriptKind.MarkDispatched:
                    _markDispatchedSha = sha;
                    break;
            }
        }

        /// <summary>
        /// Defines known shared run store Lua scripts.
        /// </summary>
        private enum ScriptKind
        {
            /// <summary>
            /// Atomic shared run create script.
            /// </summary>
            Create = 0,

            /// <summary>
            /// Atomic shared run cancel script.
            /// </summary>
            Cancel = 1,

            /// <summary>
            /// Atomic shared run mark-dispatched script.
            /// </summary>
            MarkDispatched = 2
        }
    }
}