namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue.Redis
{
    /// <summary>
    /// Contains Lua scripts used by the Redis runtime run execution index.
    /// </summary>
    internal static class RedisAiRuntimeRunExecutionIndexScripts
    {
        /// <summary>
        /// Atomically registers or replaces a queued runtime run index entry.
        /// </summary>
        /// <remarks>
        /// Expected keys:
        /// - KEYS[1]: runtime run index item hash key.
        /// - KEYS[2]: global all-runs sorted-set index key.
        /// - KEYS[3]: tenant all-runs sorted-set index key, optional.
        ///
        /// Expected arguments:
        /// - ARGV[1]: run id.
        /// - ARGV[2]: created-at score.
        /// - ARGV[3]: expiration in seconds, or 0 when expiration is disabled.
        /// - ARGV[4..n]: Redis hash field/value pairs.
        /// </remarks>
        public const string RegisterQueued = """
            local itemKey = KEYS[1]
            local allIndexKey = KEYS[2]
            local tenantAllIndexKey = nil

            if #KEYS >= 3 then
                tenantAllIndexKey = KEYS[3]
            end

            local runId = ARGV[1]
            local score = tonumber(ARGV[2])
            local expireSeconds = tonumber(ARGV[3])

            if ((#ARGV - 3) % 2) ~= 0 then
                return 'invalid-field-pairs'
            end

            redis.call('DEL', itemKey)

            for i = 4, #ARGV, 2 do
                redis.call('HSET', itemKey, ARGV[i], ARGV[i + 1])
            end

            redis.call('ZADD', allIndexKey, score, runId)

            if tenantAllIndexKey ~= nil and tenantAllIndexKey ~= '' then
                redis.call('ZADD', tenantAllIndexKey, score, runId)
            end

            if expireSeconds ~= nil and expireSeconds > 0 then
                redis.call('EXPIRE', itemKey, expireSeconds)

                if tenantAllIndexKey ~= nil and tenantAllIndexKey ~= '' then
                    redis.call('EXPIRE', tenantAllIndexKey, expireSeconds)
                end
            end

            return 'registered'
            """;

        /// <summary>
        /// Atomically registers a queued runtime run only when its item key does not already exist.
        /// </summary>
        /// <remarks>
        /// The key and argument contract is identical to <see cref="RegisterQueued"/>.
        /// </remarks>
        public const string TryRegisterQueued = """
            local itemKey = KEYS[1]
            local allIndexKey = KEYS[2]
            local tenantAllIndexKey = nil

            if #KEYS >= 3 then
                tenantAllIndexKey = KEYS[3]
            end

            local runId = ARGV[1]
            local score = tonumber(ARGV[2])
            local expireSeconds = tonumber(ARGV[3])

            if ((#ARGV - 3) % 2) ~= 0 then
                return 'invalid-field-pairs'
            end

            if redis.call('EXISTS', itemKey) == 1 then
                return 'existing'
            end

            for i = 4, #ARGV, 2 do
                redis.call('HSET', itemKey, ARGV[i], ARGV[i + 1])
            end

            redis.call('ZADD', allIndexKey, score, runId)

            if tenantAllIndexKey ~= nil and tenantAllIndexKey ~= '' then
                redis.call('ZADD', tenantAllIndexKey, score, runId)
            end

            if expireSeconds ~= nil and expireSeconds > 0 then
                redis.call('EXPIRE', itemKey, expireSeconds)

                if tenantAllIndexKey ~= nil and tenantAllIndexKey ~= '' then
                    redis.call('EXPIRE', tenantAllIndexKey, expireSeconds)
                end
            end

            return 'registered'
            """;

        /// <summary>
        /// Atomically marks a runtime run as started.
        /// </summary>
        public const string MarkStarted = """
            local itemKey = KEYS[1]

            local executionId = ARGV[1]
            local nowUtc = ARGV[2]

            if redis.call('EXISTS', itemKey) == 0 then
                return 'missing'
            end

            redis.call('HSET', itemKey, 'executionId', executionId)
            redis.call('HSET', itemKey, 'status', 'running')
            redis.call('HSET', itemKey, 'failureReason', '')
            redis.call('HSET', itemKey, 'startedAtUtc', nowUtc)
            redis.call('HSET', itemKey, 'completedAtUtc', '')

            return 'started'
            """;

        /// <summary>
        /// Atomically marks a runtime run as completed unless recovery has already
        /// taken ownership of the runtime run.
        /// </summary>
        public const string MarkCompleted = """
            local itemKey = KEYS[1]

            local executionId = ARGV[1]
            local nowUtc = ARGV[2]

            if redis.call('EXISTS', itemKey) == 0 then
                return 'missing'
            end

            local currentStatus =
                redis.call('HGET', itemKey, 'status') or ''

            if currentStatus == 'requeued-for-recovery' then
                return 'ignored-requeued-for-recovery'
            end

            local existingStartedAtUtc =
                redis.call('HGET', itemKey, 'startedAtUtc') or ''

            redis.call('HSET', itemKey, 'executionId', executionId)
            redis.call('HSET', itemKey, 'status', 'completed')
            redis.call('HSET', itemKey, 'failureReason', '')
            redis.call(
                'HSET',
                itemKey,
                'startedAtUtc',
                existingStartedAtUtc ~= '' and existingStartedAtUtc or nowUtc)
            redis.call('HSET', itemKey, 'completedAtUtc', nowUtc)

            return 'completed'
            """;

        /// <summary>
        /// Atomically marks a runtime run as failed unless recovery has already
        /// taken ownership of the runtime run.
        /// </summary>
        public const string MarkFailed = """
            local itemKey = KEYS[1]

            local executionId = ARGV[1]
            local failureReason = ARGV[2]
            local nowUtc = ARGV[3]

            if redis.call('EXISTS', itemKey) == 0 then
                return 'missing'
            end

            local currentStatus =
                redis.call('HGET', itemKey, 'status') or ''

            if currentStatus == 'requeued-for-recovery' then
                return 'ignored-requeued-for-recovery'
            end

            if executionId ~= nil and executionId ~= '' then
                redis.call('HSET', itemKey, 'executionId', executionId)
            end

            redis.call('HSET', itemKey, 'status', 'failed')
            redis.call('HSET', itemKey, 'failureReason', failureReason)
            redis.call('HSET', itemKey, 'completedAtUtc', nowUtc)

            return 'failed'
            """;

        /// <summary>
        /// Atomically marks a runtime run as cancelled.
        /// </summary>
        public const string MarkCancelled = """
            local itemKey = KEYS[1]

            local executionId = ARGV[1]
            local reason = ARGV[2]
            local nowUtc = ARGV[3]

            if redis.call('EXISTS', itemKey) == 0 then
                return 'missing'
            end

            if executionId ~= nil and executionId ~= '' then
                redis.call('HSET', itemKey, 'executionId', executionId)
            end

            redis.call('HSET', itemKey, 'status', 'cancelled')
            redis.call('HSET', itemKey, 'failureReason', reason)
            redis.call('HSET', itemKey, 'completedAtUtc', nowUtc)

            return 'cancelled'
            """;
    }
}
