namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling.Redis
{
    /// <summary>
    /// Contains Lua scripts used by the Redis runtime scale-out request store.
    /// </summary>
    internal static class RedisAiRuntimeScaleOutRequestStoreScripts
    {
        /// <summary>
        /// Atomically creates a scale-out request record when no duplicate pending request exists.
        /// </summary>
        /// <remarks>
        /// Keys:
        /// - KEYS[1] = request hash key.
        /// - KEYS[2] = all requests sorted-set index key.
        /// - KEYS[3] = pending requests sorted-set index key.
        /// - KEYS[4] = shared run lookup key.
        /// - KEYS[5] = deduplication key.
        /// - KEYS[6] = request id to control-plane id lookup key.
        ///
        /// Arguments:
        /// - ARGV[1] = request id.
        /// - ARGV[2] = shared run id.
        /// - ARGV[3] = created-at score.
        /// - ARGV[4] = record expiration seconds.
        /// - ARGV[5] = deduplication expiration seconds.
        /// - ARGV[6] = enable deduplication flag.
        /// - ARGV[7] = control-plane id.
        /// - ARGV[8...] = hash field pairs.
        /// </remarks>
        public const string Create = """
            local requestKey = KEYS[1]
            local allIndexKey = KEYS[2]
            local pendingIndexKey = KEYS[3]
            local sharedRunKey = KEYS[4]
            local dedupKey = KEYS[5]
            local requestControlPlaneKey = KEYS[6]

            local requestId = ARGV[1]
            local sharedRunId = ARGV[2]
            local createdAtScore = tonumber(ARGV[3])
            local expireSeconds = tonumber(ARGV[4])
            local dedupExpireSeconds = tonumber(ARGV[5])
            local enableDeduplication = ARGV[6]
            local controlPlaneId = ARGV[7]

            if enableDeduplication == '1' then
                local existingRequestId = redis.call('GET', dedupKey)

                if existingRequestId ~= false and existingRequestId ~= nil and existingRequestId ~= '' then
                    return existingRequestId
                end
            end

            if redis.call('EXISTS', requestKey) == 1 then
                return requestId
            end

            for i = 8, #ARGV, 2 do
                redis.call('HSET', requestKey, ARGV[i], ARGV[i + 1])
            end

            redis.call('ZADD', allIndexKey, createdAtScore, requestId)
            redis.call('ZADD', pendingIndexKey, createdAtScore, requestId)
            redis.call('SET', sharedRunKey, requestId)
            redis.call('SET', requestControlPlaneKey, controlPlaneId)

            if expireSeconds ~= nil and expireSeconds > 0 then
                redis.call('EXPIRE', requestKey, expireSeconds)
                redis.call('EXPIRE', sharedRunKey, expireSeconds)
                redis.call('EXPIRE', requestControlPlaneKey, expireSeconds)
            end

            if enableDeduplication == '1' and dedupExpireSeconds ~= nil and dedupExpireSeconds > 0 then
                redis.call('SET', dedupKey, requestId, 'EX', dedupExpireSeconds)
            end

            return requestId
            """;

        /// <summary>
        /// Atomically transitions a non-terminal scale-out request to a new status.
        /// </summary>
        /// <remarks>
        /// Keys:
        /// - KEYS[1] = request hash key.
        /// - KEYS[2] = pending requests sorted-set index key.
        ///
        /// Arguments:
        /// - ARGV[1] = target status.
        /// - ARGV[2] = transition timestamp field name.
        /// - ARGV[3] = transition timestamp.
        /// - ARGV[4...] = additional hash field pairs.
        /// </remarks>
        public const string Transition = """
            local requestKey = KEYS[1]
            local pendingIndexKey = KEYS[2]

            local targetStatus = ARGV[1]
            local timestampField = ARGV[2]
            local timestampValue = ARGV[3]

            if redis.call('EXISTS', requestKey) == 0 then
                return 'missing'
            end

            local currentStatus = redis.call('HGET', requestKey, 'status')

            if currentStatus == 'Fulfilled' or currentStatus == 'Rejected' or currentStatus == 'Expired' or currentStatus == 'Cancelled' then
                return 'terminal'
            end

            if targetStatus == 'Observed' and currentStatus ~= 'Pending' then
                return 'invalid'
            end

            if targetStatus ~= 'Observed' and currentStatus ~= 'Pending' and currentStatus ~= 'Observed' then
                return 'invalid'
            end

            redis.call('HSET', requestKey, 'status', targetStatus)
            redis.call('HSET', requestKey, timestampField, timestampValue)

            for i = 4, #ARGV, 2 do
                redis.call('HSET', requestKey, ARGV[i], ARGV[i + 1])
            end

            if targetStatus ~= 'Pending' then
                local requestId = redis.call('HGET', requestKey, 'requestId')
                if requestId ~= false and requestId ~= nil and requestId ~= '' then
                    redis.call('ZREM', pendingIndexKey, requestId)
                end
            end

            return 'updated'
            """;
    }
}