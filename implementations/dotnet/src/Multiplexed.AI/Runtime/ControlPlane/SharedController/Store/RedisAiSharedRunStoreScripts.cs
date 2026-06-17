namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Store
{
    /// <summary>
    /// Contains Lua scripts used by the Redis shared run store.
    /// </summary>
    internal static class RedisAiSharedRunStoreScripts
    {
        /// <summary>
        /// Atomically creates a shared run record if it does not already exist.
        /// </summary>
        /// <remarks>
        /// Expected keys:
        /// - KEYS[1]: Redis hash key for the shared run record.
        /// - KEYS[2]: Redis sorted-set index key for shared runs.
        ///
        /// Expected arguments:
        /// - ARGV[1]: shared run id.
        /// - ARGV[2]: submitted-at score in Unix milliseconds.
        /// - ARGV[3]: expiration in seconds, or 0 when expiration is disabled.
        /// - ARGV[4..n]: Redis hash field/value pairs.
        ///
        /// This script is intentionally field-agnostic.
        /// The C# store layer provides all hash fields dynamically, including
        /// executionContextSnapshotJson.
        ///
        /// The script does not parse JSON. Values are stored as plain Redis hash values.
        /// </remarks>
        public const string Create = """
            local runKey = KEYS[1]
            local indexKey = KEYS[2]

            local sharedRunId = ARGV[1]
            local submittedAtScore = tonumber(ARGV[2])
            local expireSeconds = tonumber(ARGV[3])

            if redis.call('EXISTS', runKey) == 1 then
                return 'duplicate'
            end

            if ((#ARGV - 3) % 2) ~= 0 then
                return 'invalid-field-pairs'
            end

            for i = 4, #ARGV, 2 do
                redis.call('HSET', runKey, ARGV[i], ARGV[i + 1])
            end

            redis.call('ZADD', indexKey, submittedAtScore, sharedRunId)

            if expireSeconds ~= nil and expireSeconds > 0 then
                redis.call('EXPIRE', runKey, expireSeconds)
            end

            return 'created'
            """;

        /// <summary>
        /// Atomically cancels a shared run when it exists and is not already terminal.
        /// </summary>
        /// <remarks>
        /// Expected keys:
        /// - KEYS[1]: Redis hash key for the shared run record.
        ///
        /// Expected arguments:
        /// - ARGV[1]: cancellation reason.
        /// - ARGV[2]: identity that requested cancellation.
        /// - ARGV[3]: source that requested cancellation.
        /// - ARGV[4]: updated-at timestamp.
        ///
        /// This script does not modify executionContextSnapshotJson.
        /// The execution context snapshot represents creation/submission context
        /// and remains stable after cancellation.
        /// </remarks>
        public const string Cancel = """
            local runKey = KEYS[1]

            local reason = ARGV[1]
            local requestedBy = ARGV[2]
            local source = ARGV[3]
            local updatedAtUtc = ARGV[4]

            if redis.call('EXISTS', runKey) == 0 then
                return 'missing'
            end

            local status = redis.call('HGET', runKey, 'status')

            if status == 'Completed' or status == 'Failed' or status == 'Cancelled' then
                return 'terminal'
            end

            redis.call('HSET', runKey, 'status', 'Cancelled')
            redis.call('HSET', runKey, 'reason', reason)
            redis.call('HSET', runKey, 'failureReason', reason)
            redis.call('HSET', runKey, 'requestedBy', requestedBy)
            redis.call('HSET', runKey, 'source', source)
            redis.call('HSET', runKey, 'updatedAtUtc', updatedAtUtc)

            return 'cancelled'
            """;

        /// <summary>
        /// Atomically marks a shared run as dispatched when it exists and is not terminal.
        /// </summary>
        /// <remarks>
        /// Expected keys:
        /// - KEYS[1]: Redis hash key for the shared run record.
        ///
        /// Expected arguments:
        /// - ARGV[1]: runtime instance id.
        /// - ARGV[2]: local runtime run id.
        /// - ARGV[3]: durable execution id.
        /// - ARGV[4]: dispatch reason.
        /// - ARGV[5]: updated-at timestamp.
        ///
        /// This script does not modify executionContextSnapshotJson.
        /// The execution context snapshot remains the original submission context.
        /// </remarks>
        public const string MarkDispatched = """
            local runKey = KEYS[1]

            local runtimeInstanceId = ARGV[1]
            local localRunId = ARGV[2]
            local executionId = ARGV[3]
            local reason = ARGV[4]
            local updatedAtUtc = ARGV[5]

            if redis.call('EXISTS', runKey) == 0 then
                return 'missing'
            end

            local status = redis.call('HGET', runKey, 'status')

            if status == 'Completed' or status == 'Failed' or status == 'Cancelled' then
                return 'terminal'
            end

            redis.call('HSET', runKey, 'status', 'Dispatched')
            redis.call('HSET', runKey, 'assignedRuntimeInstanceId', runtimeInstanceId)
            redis.call('HSET', runKey, 'localRunId', localRunId)
            redis.call('HSET', runKey, 'executionId', executionId)
            redis.call('HSET', runKey, 'reason', reason)
            redis.call('HSET', runKey, 'updatedAtUtc', updatedAtUtc)

            return 'dispatched'
            """;
    }
}