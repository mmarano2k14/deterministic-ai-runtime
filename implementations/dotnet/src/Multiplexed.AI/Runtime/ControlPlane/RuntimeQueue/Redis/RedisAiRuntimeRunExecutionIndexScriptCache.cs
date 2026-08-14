using StackExchange.Redis;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue.Redis
{
    /// <summary>
    /// Provides Redis Lua script SHA caching for the Redis runtime run execution index.
    /// </summary>
    /// <remarks>
    /// Redis script cache is server-side. A script SHA may disappear after Redis restart,
    /// failover, flush, or when using another node.
    ///
    /// This helper:
    /// - loads scripts only when needed;
    /// - executes by SHA when possible;
    /// - reloads automatically when Redis returns NOSCRIPT.
    /// </remarks>
    internal sealed class RedisAiRuntimeRunExecutionIndexScriptCache
    {
        private readonly IConnectionMultiplexer _connection;
        private readonly SemaphoreSlim _loadLock = new(1, 1);

        private byte[]? _registerQueuedSha;
        private byte[]? _tryRegisterQueuedSha;
        private byte[]? _markStartedSha;
        private byte[]? _markCompletedSha;
        private byte[]? _markFailedSha;
        private byte[]? _markCancelledSha;

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisAiRuntimeRunExecutionIndexScriptCache"/> class.
        /// </summary>
        /// <param name="connection">The Redis connection multiplexer.</param>
        public RedisAiRuntimeRunExecutionIndexScriptCache(
            IConnectionMultiplexer connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        /// <summary>
        /// Executes the atomic register-queued script.
        /// </summary>
        public Task<RedisResult> ExecuteRegisterQueuedAsync(
            IDatabase database,
            RedisKey[] keys,
            RedisValue[] values)
        {
            return ExecuteAsync(
                database,
                ScriptKind.RegisterQueued,
                RedisAiRuntimeRunExecutionIndexScripts.RegisterQueued,
                keys,
                values);
        }

        /// <summary>
        /// Executes the atomic try-register-queued script.
        /// </summary>
        public Task<RedisResult> ExecuteTryRegisterQueuedAsync(
            IDatabase database,
            RedisKey[] keys,
            RedisValue[] values)
        {
            return ExecuteAsync(
                database,
                ScriptKind.TryRegisterQueued,
                RedisAiRuntimeRunExecutionIndexScripts.TryRegisterQueued,
                keys,
                values);
        }

        /// <summary>
        /// Executes the atomic mark-started script.
        /// </summary>
        public Task<RedisResult> ExecuteMarkStartedAsync(
            IDatabase database,
            RedisKey[] keys,
            RedisValue[] values)
        {
            return ExecuteAsync(
                database,
                ScriptKind.MarkStarted,
                RedisAiRuntimeRunExecutionIndexScripts.MarkStarted,
                keys,
                values);
        }

        /// <summary>
        /// Executes the atomic mark-completed script.
        /// </summary>
        public Task<RedisResult> ExecuteMarkCompletedAsync(
            IDatabase database,
            RedisKey[] keys,
            RedisValue[] values)
        {
            return ExecuteAsync(
                database,
                ScriptKind.MarkCompleted,
                RedisAiRuntimeRunExecutionIndexScripts.MarkCompleted,
                keys,
                values);
        }

        /// <summary>
        /// Executes the atomic mark-failed script.
        /// </summary>
        public Task<RedisResult> ExecuteMarkFailedAsync(
            IDatabase database,
            RedisKey[] keys,
            RedisValue[] values)
        {
            return ExecuteAsync(
                database,
                ScriptKind.MarkFailed,
                RedisAiRuntimeRunExecutionIndexScripts.MarkFailed,
                keys,
                values);
        }

        /// <summary>
        /// Executes the atomic mark-cancelled script.
        /// </summary>
        public Task<RedisResult> ExecuteMarkCancelledAsync(
            IDatabase database,
            RedisKey[] keys,
            RedisValue[] values)
        {
            return ExecuteAsync(
                database,
                ScriptKind.MarkCancelled,
                RedisAiRuntimeRunExecutionIndexScripts.MarkCancelled,
                keys,
                values);
        }

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

                var endpoint =
                    _connection
                        .GetEndPoints()
                        .FirstOrDefault();

                if (endpoint is null)
                {
                    throw new InvalidOperationException(
                        "No Redis endpoint is available for runtime run execution index Lua script loading.");
                }

                var server =
                    _connection.GetServer(endpoint);

                var loaded = await server
                    .ScriptLoadAsync(script)
                    .ConfigureAwait(false);

                if (loaded is null ||
                    loaded.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Redis returned an empty SHA for runtime run execution index script '{kind}'.");
                }

                SetSha(
                    kind,
                    loaded);

                return loaded;
            }
            finally
            {
                _loadLock.Release();
            }
        }

        private static bool IsNoScript(
            RedisServerException exception)
        {
            return exception.Message.Contains(
                "NOSCRIPT",
                StringComparison.OrdinalIgnoreCase);
        }

        private byte[]? GetSha(
            ScriptKind kind)
        {
            return kind switch
            {
                ScriptKind.RegisterQueued => _registerQueuedSha,
                ScriptKind.TryRegisterQueued => _tryRegisterQueuedSha,
                ScriptKind.MarkStarted => _markStartedSha,
                ScriptKind.MarkCompleted => _markCompletedSha,
                ScriptKind.MarkFailed => _markFailedSha,
                ScriptKind.MarkCancelled => _markCancelledSha,
                _ => null
            };
        }

        private void SetSha(
            ScriptKind kind,
            byte[] sha)
        {
            switch (kind)
            {
                case ScriptKind.RegisterQueued:
                    _registerQueuedSha = sha;
                    break;

                case ScriptKind.TryRegisterQueued:
                    _tryRegisterQueuedSha = sha;
                    break;

                case ScriptKind.MarkStarted:
                    _markStartedSha = sha;
                    break;

                case ScriptKind.MarkCompleted:
                    _markCompletedSha = sha;
                    break;

                case ScriptKind.MarkFailed:
                    _markFailedSha = sha;
                    break;

                case ScriptKind.MarkCancelled:
                    _markCancelledSha = sha;
                    break;
            }
        }

        private enum ScriptKind
        {
            RegisterQueued = 0,
            TryRegisterQueued = 1,
            MarkStarted = 2,
            MarkCompleted = 3,
            MarkFailed = 4,
            MarkCancelled = 5
        }
    }
}
