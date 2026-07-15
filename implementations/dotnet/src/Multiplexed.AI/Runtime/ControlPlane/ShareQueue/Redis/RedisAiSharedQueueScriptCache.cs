using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Redis;
using StackExchange.Redis;

namespace Multiplexed.AI.Runtime.ControlPlane.ShareQueue.Redis
{
    /// <summary>
    /// Provides Redis Lua script SHA caching for the Redis shared queue.
    /// </summary>
    /// <remarks>
    /// Redis script cache is server-side.
    /// A script SHA may disappear after Redis restart, failover, flush, or when using another node.
    ///
    /// This helper:
    /// - loads scripts only when needed
    /// - executes by SHA when possible
    /// - reloads automatically when Redis returns NOSCRIPT
    /// </remarks>
    internal sealed class RedisAiSharedQueueScriptCache
    {
        private readonly IConnectionMultiplexer _connection;
        private readonly SemaphoreSlim _loadLock = new(1, 1);

        private byte[]? _enqueueSha;
        private byte[]? _claimNextSha;
        private byte[]? _claimSha;
        private byte[]? _markDispatchedSha;
        private byte[]? _requeueSha;
        private byte[]? _requeueDispatchedSha;
        private byte[]? _cancelSha;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="RedisAiSharedQueueScriptCache"/> class.
        /// </summary>
        /// <param name="connection">The Redis connection multiplexer.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="connection"/> is null.
        /// </exception>
        public RedisAiSharedQueueScriptCache(
            IConnectionMultiplexer connection)
        {
            _connection =
                connection ??
                throw new ArgumentNullException(
                    nameof(connection));
        }

        /// <summary>
        /// Executes the atomic enqueue script.
        /// </summary>
        public Task<RedisResult> ExecuteEnqueueAsync(
            IDatabase database,
            RedisKey[] keys,
            RedisValue[] values)
        {
            return ExecuteAsync(
                database,
                ScriptKind.Enqueue,
                RedisAiSharedQueueScripts.Enqueue,
                keys,
                values);
        }

        /// <summary>
        /// Executes the atomic claim-next script.
        /// </summary>
        public Task<RedisResult> ExecuteClaimNextAsync(
            IDatabase database,
            RedisKey[] keys,
            RedisValue[] values)
        {
            return ExecuteAsync(
                database,
                ScriptKind.ClaimNext,
                RedisAiSharedQueueScripts.ClaimNext,
                keys,
                values);
        }

        /// <summary>
        /// Executes the atomic targeted claim script.
        /// </summary>
        public Task<RedisResult> ExecuteClaimAsync(
            IDatabase database,
            RedisKey[] keys,
            RedisValue[] values)
        {
            return ExecuteAsync(
                database,
                ScriptKind.Claim,
                RedisAiSharedQueueScripts.Claim,
                keys,
                values);
        }

        /// <summary>
        /// Executes the atomic mark-dispatched script.
        /// </summary>
        public Task<RedisResult> ExecuteMarkDispatchedAsync(
            IDatabase database,
            RedisKey[] keys,
            RedisValue[] values)
        {
            return ExecuteAsync(
                database,
                ScriptKind.MarkDispatched,
                RedisAiSharedQueueScripts.MarkDispatched,
                keys,
                values);
        }

        /// <summary>
        /// Executes the atomic requeue script.
        /// </summary>
        public Task<RedisResult> ExecuteRequeueAsync(
            IDatabase database,
            RedisKey[] keys,
            RedisValue[] values)
        {
            return ExecuteAsync(
                database,
                ScriptKind.Requeue,
                RedisAiSharedQueueScripts.Requeue,
                keys,
                values);
        }

        /// <summary>
        /// Executes the atomic requeue-dispatched script.
        /// </summary>
        public Task<RedisResult> ExecuteRequeueDispatchedAsync(
            IDatabase database,
            RedisKey[] keys,
            RedisValue[] values)
        {
            return ExecuteAsync(
                database,
                ScriptKind.RequeueDispatched,
                RedisAiSharedQueueScripts.RequeueDispatched,
                keys,
                values);
        }

        /// <summary>
        /// Executes the atomic cancel script.
        /// </summary>
        public Task<RedisResult> ExecuteCancelAsync(
            IDatabase database,
            RedisKey[] keys,
            RedisValue[] values)
        {
            return ExecuteAsync(
                database,
                ScriptKind.Cancel,
                RedisAiSharedQueueScripts.Cancel,
                keys,
                values);
        }

        /// <summary>
        /// Executes a Lua script by SHA and reloads it automatically
        /// when Redis reports NOSCRIPT.
        /// </summary>
        private async Task<RedisResult> ExecuteAsync(
            IDatabase database,
            ScriptKind kind,
            string script,
            RedisKey[] keys,
            RedisValue[] values)
        {
            ArgumentNullException.ThrowIfNull(database);

            var sha =
                await GetOrLoadShaAsync(
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
            catch (RedisServerException exception)
                when (IsNoScript(exception))
            {
                sha =
                    await ReloadShaAsync(
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
        private async Task<byte[]> GetOrLoadShaAsync(
            ScriptKind kind,
            string script)
        {
            var current =
                GetSha(kind);

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
        /// Loads or reloads a Lua script into Redis
        /// and updates the cached SHA.
        /// </summary>
        private async Task<byte[]> ReloadShaAsync(
            ScriptKind kind,
            string script,
            bool forceReload)
        {
            await _loadLock
                .WaitAsync()
                .ConfigureAwait(false);

            try
            {
                var current =
                    GetSha(kind);

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
                        "No Redis endpoint is available for Lua script loading.");
                }

                var server =
                    _connection.GetServer(
                        endpoint);

                var loaded =
                    await server
                        .ScriptLoadAsync(
                            script)
                        .ConfigureAwait(false);

                if (loaded is null ||
                    loaded.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Redis returned an empty SHA for shared queue script '{kind}'.");
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

        /// <summary>
        /// Determines whether a Redis exception represents a missing Lua script.
        /// </summary>
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
        private byte[]? GetSha(
            ScriptKind kind)
        {
            return kind switch
            {
                ScriptKind.Enqueue =>
                    _enqueueSha,

                ScriptKind.ClaimNext =>
                    _claimNextSha,

                ScriptKind.Claim =>
                    _claimSha,

                ScriptKind.MarkDispatched =>
                    _markDispatchedSha,

                ScriptKind.Requeue =>
                    _requeueSha,

                ScriptKind.RequeueDispatched =>
                    _requeueDispatchedSha,

                ScriptKind.Cancel =>
                    _cancelSha,

                _ =>
                    null
            };
        }

        /// <summary>
        /// Sets a cached script SHA.
        /// </summary>
        private void SetSha(
            ScriptKind kind,
            byte[] sha)
        {
            switch (kind)
            {
                case ScriptKind.Enqueue:
                    _enqueueSha = sha;
                    break;

                case ScriptKind.ClaimNext:
                    _claimNextSha = sha;
                    break;

                case ScriptKind.Claim:
                    _claimSha = sha;
                    break;

                case ScriptKind.MarkDispatched:
                    _markDispatchedSha = sha;
                    break;

                case ScriptKind.Requeue:
                    _requeueSha = sha;
                    break;

                case ScriptKind.RequeueDispatched:
                    _requeueDispatchedSha = sha;
                    break;

                case ScriptKind.Cancel:
                    _cancelSha = sha;
                    break;
            }
        }

        /// <summary>
        /// Defines known shared queue Lua scripts.
        /// </summary>
        private enum ScriptKind
        {
            /// <summary>
            /// Atomic enqueue script.
            /// </summary>
            Enqueue = 0,

            /// <summary>
            /// Atomic claim-next script.
            /// </summary>
            ClaimNext = 1,

            /// <summary>
            /// Atomic mark-dispatched script.
            /// </summary>
            MarkDispatched = 2,

            /// <summary>
            /// Atomic requeue script.
            /// </summary>
            Requeue = 3,

            /// <summary>
            /// Atomic requeue-dispatched recovery script.
            /// </summary>
            RequeueDispatched = 4,

            /// <summary>
            /// Atomic cancel script.
            /// </summary>
            Cancel = 5,

            /// <summary>
            /// Atomic targeted shared-run claim script.
            /// </summary>
            Claim = 6
        }
    }
}