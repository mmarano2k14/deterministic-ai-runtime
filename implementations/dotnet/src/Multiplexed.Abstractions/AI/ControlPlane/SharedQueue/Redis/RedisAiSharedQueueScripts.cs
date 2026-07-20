namespace Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Redis
{
    /// <summary>
    /// Contains Lua scripts used by the Redis shared queue.
    /// </summary>
    public static class RedisAiSharedQueueScripts
    {
        /// <summary>
        /// Atomically enqueues a shared queue item if it does not already exist.
        /// </summary>
        /// <remarks>
        /// Expected keys:
        /// - KEYS[1]: queue item hash key.
        /// - KEYS[2]: global pending sorted-set index key.
        /// - KEYS[3]: global all-items sorted-set index key.
        /// - KEYS[4]: optional tenant pending sorted-set index key.
        /// - KEYS[5]: optional tenant all-items sorted-set index key.
        ///
        /// Expected arguments:
        /// - ARGV[1]: shared run id.
        /// - ARGV[2]: queue score.
        /// - ARGV[3]: expiration in seconds, or 0 when expiration is disabled.
        /// - ARGV[4..n]: Redis hash field/value pairs.
        ///
        /// Every item is added to the all-items indexes.
        /// Only items whose initial status is Pending are added to the pending indexes.
        /// </remarks>
        public const string Enqueue = """
            local itemKey = KEYS[1]
            local pendingIndexKey = KEYS[2]
            local allIndexKey = KEYS[3]
            local tenantPendingIndexKey = nil
            local tenantAllIndexKey = nil

            if #KEYS >= 4 then
                tenantPendingIndexKey = KEYS[4]
            end

            if #KEYS >= 5 then
                tenantAllIndexKey = KEYS[5]
            end

            local sharedRunId = ARGV[1]
            local score = tonumber(ARGV[2])
            local expireSeconds = tonumber(ARGV[3])

            if redis.call('EXISTS', itemKey) == 1 then
                return 'duplicate'
            end

            if ((#ARGV - 3) % 2) ~= 0 then
                return 'invalid-field-pairs'
            end

            local itemStatus = 'Pending'

            for i = 4, #ARGV, 2 do
                local fieldName = ARGV[i]
                local fieldValue = ARGV[i + 1]

                redis.call(
                    'HSET',
                    itemKey,
                    fieldName,
                    fieldValue)

                if fieldName == 'status' then
                    itemStatus = fieldValue
                end
            end

            -- Every queue item belongs to the all-items indexes.
            redis.call(
                'ZADD',
                allIndexKey,
                score,
                sharedRunId)

            if tenantAllIndexKey ~= nil and
               tenantAllIndexKey ~= '' then
                redis.call(
                    'ZADD',
                    tenantAllIndexKey,
                    score,
                    sharedRunId)
            end

            -- Only Pending items may be claimed by the shared queue pump.
            if itemStatus == 'Pending' then
                redis.call(
                    'ZADD',
                    pendingIndexKey,
                    score,
                    sharedRunId)

                if tenantPendingIndexKey ~= nil and
                   tenantPendingIndexKey ~= '' then
                    redis.call(
                        'ZADD',
                        tenantPendingIndexKey,
                        score,
                        sharedRunId)
                end
            else
                -- Defensive cleanup for non-pending ownership records.
                redis.call(
                    'ZREM',
                    pendingIndexKey,
                    sharedRunId)

                if tenantPendingIndexKey ~= nil and
                   tenantPendingIndexKey ~= '' then
                    redis.call(
                        'ZREM',
                        tenantPendingIndexKey,
                        sharedRunId)
                end
            end

            if expireSeconds ~= nil and
               expireSeconds > 0 then
                redis.call(
                    'EXPIRE',
                    itemKey,
                    expireSeconds)
            end

            return 'enqueued'
            """;

        /// <summary>
        /// Atomically claims the first pending shared queue item matching the optional tenant and pipeline filters.
        /// </summary>
        /// <remarks>
        /// Expected keys:
        /// - KEYS[1]: active pending sorted-set index key. This can be either tenant pending or global pending.
        /// - KEYS[2]: optional global pending sorted-set index key.
        /// - KEYS[3]: optional tenant pending sorted-set index key.
        ///
        /// Tenant filtering reads executionContextSnapshotJson and uses TenantId from the snapshot.
        /// The script accepts both PascalCase and camelCase JSON property names.
        /// </remarks>
        public const string ClaimNext = """
            local pendingIndexKey = KEYS[1]
            local globalPendingIndexKey = nil
            local tenantPendingIndexKey = nil

            if #KEYS >= 2 then
                globalPendingIndexKey = KEYS[2]
            end

            if #KEYS >= 3 then
                tenantPendingIndexKey = KEYS[3]
            end

            local runtimeInstanceId = ARGV[1]
            local workerId = ARGV[2]
            local claimToken = ARGV[3]
            local nowUtc = ARGV[4]
            local claimExpiresAtUtc = ARGV[5]
            local requestedTenantId = ARGV[6]
            local requestedPipelineKey = ARGV[7]
            local reason = ARGV[8]
            local keyPrefix = ARGV[9]
            local scanLimit = tonumber(ARGV[10])

            if scanLimit == nil or scanLimit <= 0 then
                scanLimit = 100
            end

            local function removeFromPendingIndexes(sharedRunId)
                redis.call('ZREM', pendingIndexKey, sharedRunId)

                if globalPendingIndexKey ~= nil and globalPendingIndexKey ~= '' then
                    redis.call('ZREM', globalPendingIndexKey, sharedRunId)
                end

                if tenantPendingIndexKey ~= nil and tenantPendingIndexKey ~= '' then
                    redis.call('ZREM', tenantPendingIndexKey, sharedRunId)
                end
            end

            local function removeFromActiveTenantIndexWhenForeign(sharedRunId)
                if requestedTenantId ~= '' and
                   tenantPendingIndexKey ~= nil and
                   tenantPendingIndexKey ~= '' and
                   pendingIndexKey == tenantPendingIndexKey then
                    redis.call('ZREM', pendingIndexKey, sharedRunId)
                end
            end

            local ids = redis.call('ZRANGE', pendingIndexKey, 0, scanLimit - 1)

            for i = 1, #ids do
                local sharedRunId = ids[i]
                local itemKey = keyPrefix .. ':item:' .. sharedRunId

                if redis.call('EXISTS', itemKey) == 1 then
                    local status = redis.call('HGET', itemKey, 'status')

                    if status ~= 'Pending' then
                        removeFromPendingIndexes(sharedRunId)
                    else
                        local itemPipelineKey = redis.call('HGET', itemKey, 'pipelineKey') or ''
                        local snapshotJson = redis.call('HGET', itemKey, 'executionContextSnapshotJson')

                        if snapshotJson == false or snapshotJson == nil or snapshotJson == '' then
                            removeFromPendingIndexes(sharedRunId)
                        else
                            local ok, snapshot = pcall(cjson.decode, snapshotJson)

                            if not ok or snapshot == nil then
                                removeFromPendingIndexes(sharedRunId)
                            else
                                local itemTenantId = snapshot['TenantId'] or snapshot['tenantId'] or ''

                                local tenantMatches =
                                    requestedTenantId == '' or itemTenantId == requestedTenantId

                                local pipelineMatches =
                                    requestedPipelineKey == '' or itemPipelineKey == requestedPipelineKey

                                if tenantMatches and pipelineMatches then
                                    removeFromPendingIndexes(sharedRunId)

                                    redis.call('HSET', itemKey, 'status', 'Claimed')
                                    redis.call('HSET', itemKey, 'claimedByRuntimeInstanceId', runtimeInstanceId)
                                    redis.call('HSET', itemKey, 'claimedByWorkerId', workerId)
                                    redis.call('HSET', itemKey, 'claimToken', claimToken)
                                    redis.call('HSET', itemKey, 'claimedAtUtc', nowUtc)
                                    redis.call('HSET', itemKey, 'claimExpiresAtUtc', claimExpiresAtUtc)
                                    redis.call('HSET', itemKey, 'updatedAtUtc', nowUtc)
                                    redis.call('HSET', itemKey, 'reason', reason)

                                    return sharedRunId
                                else
                                    removeFromActiveTenantIndexWhenForeign(sharedRunId)
                                end
                            end
                        end
                    end
                else
                    removeFromPendingIndexes(sharedRunId)
                end
            end

            return ''
            """;

        /// <summary>
        /// Atomically marks a claimed item as dispatched when the claim token matches.
        /// </summary>
        public const string MarkDispatched = """
            local itemKey = KEYS[1]

            local claimToken = ARGV[1]
            local nowUtc = ARGV[2]
            local reason = ARGV[3]

            if redis.call('EXISTS', itemKey) == 0 then
                return 'missing'
            end

            local status = redis.call('HGET', itemKey, 'status')
            local existingClaimToken = redis.call('HGET', itemKey, 'claimToken')

            if status ~= 'Claimed' or existingClaimToken ~= claimToken then
                return 'not-owner'
            end

            redis.call('HSET', itemKey, 'status', 'Dispatched')
            redis.call('HSET', itemKey, 'updatedAtUtc', nowUtc)
            redis.call('HSET', itemKey, 'reason', reason)

            return 'dispatched'
            """;

        /// <summary>
        /// Atomically requeues a claimed item when the claim token matches.
        /// </summary>
        /// <remarks>
        /// Expected keys:
        /// - KEYS[1]: queue item hash key.
        /// - KEYS[2]: global pending sorted-set index key.
        /// - KEYS[3]: optional tenant pending sorted-set index key.
        /// </remarks>
        public const string Requeue = """
            local itemKey = KEYS[1]
            local pendingIndexKey = KEYS[2]
            local tenantPendingIndexKey = nil

            if #KEYS >= 3 then
                tenantPendingIndexKey = KEYS[3]
            end

            local sharedRunId = ARGV[1]
            local claimToken = ARGV[2]
            local score = tonumber(ARGV[3])
            local nowUtc = ARGV[4]
            local reason = ARGV[5]

            if redis.call('EXISTS', itemKey) == 0 then
                return 'missing'
            end

            local status = redis.call('HGET', itemKey, 'status')
            local existingClaimToken = redis.call('HGET', itemKey, 'claimToken')

            if status ~= 'Claimed' or existingClaimToken ~= claimToken then
                return 'not-owner'
            end

            redis.call('HSET', itemKey, 'status', 'Pending')
            redis.call('HSET', itemKey, 'claimedByRuntimeInstanceId', '')
            redis.call('HSET', itemKey, 'claimedByWorkerId', '')
            redis.call('HSET', itemKey, 'claimToken', '')
            redis.call('HSET', itemKey, 'claimedAtUtc', '')
            redis.call('HSET', itemKey, 'claimExpiresAtUtc', '')
            redis.call('HSET', itemKey, 'updatedAtUtc', nowUtc)
            redis.call('HSET', itemKey, 'reason', reason)

            redis.call('ZADD', pendingIndexKey, score, sharedRunId)

            if tenantPendingIndexKey ~= nil and tenantPendingIndexKey ~= '' then
                redis.call('ZADD', tenantPendingIndexKey, score, sharedRunId)
            end

            return 'requeued'
            """;

        /// <summary>
        /// Atomically cancels a queue item when it is not already terminal.
        /// </summary>
        /// <remarks>
        /// Expected keys:
        /// - KEYS[1]: queue item hash key.
        /// - KEYS[2]: global pending sorted-set index key.
        /// - KEYS[3]: optional tenant pending sorted-set index key.
        /// </remarks>
        public const string Cancel = """
            local itemKey = KEYS[1]
            local pendingIndexKey = KEYS[2]
            local tenantPendingIndexKey = nil

            if #KEYS >= 3 then
                tenantPendingIndexKey = KEYS[3]
            end

            local sharedRunId = ARGV[1]
            local nowUtc = ARGV[2]
            local reason = ARGV[3]

            if redis.call('EXISTS', itemKey) == 0 then
                return 'missing'
            end

            local status = redis.call('HGET', itemKey, 'status')

            if status == 'Completed' or status == 'Failed' or status == 'Cancelled' or status == 'Dispatched' then
                return 'terminal'
            end

            redis.call('ZREM', pendingIndexKey, sharedRunId)

            if tenantPendingIndexKey ~= nil and tenantPendingIndexKey ~= '' then
                redis.call('ZREM', tenantPendingIndexKey, sharedRunId)
            end

            redis.call('HSET', itemKey, 'status', 'Cancelled')
            redis.call('HSET', itemKey, 'updatedAtUtc', nowUtc)
            redis.call('HSET', itemKey, 'reason', reason)

            return 'cancelled'
            """;

        /// <summary>
        /// Atomically claims one specific pending shared queue item.
        /// </summary>
        /// <remarks>
        /// Expected keys:
        /// - KEYS[1]: queue item hash key.
        /// - KEYS[2]: global pending sorted-set index key.
        /// - KEYS[3]: optional tenant pending sorted-set index key.
        ///
        /// Expected arguments:
        /// - ARGV[1]: expected shared run id.
        /// - ARGV[2]: runtime instance id.
        /// - ARGV[3]: worker id.
        /// - ARGV[4]: claim token.
        /// - ARGV[5]: claim UTC timestamp.
        /// - ARGV[6]: claim expiration UTC timestamp.
        /// - ARGV[7]: requested tenant id.
        /// - ARGV[8]: requested pipeline key.
        /// - ARGV[9]: reason.
        /// </remarks>
        public const string Claim = """
            local itemKey = KEYS[1]
            local globalPendingIndexKey = KEYS[2]
            local tenantPendingIndexKey = nil

            if #KEYS >= 3 then
                tenantPendingIndexKey = KEYS[3]
            end

            local expectedSharedRunId = ARGV[1]
            local runtimeInstanceId = ARGV[2]
            local workerId = ARGV[3]
            local claimToken = ARGV[4]
            local nowUtc = ARGV[5]
            local claimExpiresAtUtc = ARGV[6]
            local requestedTenantId = ARGV[7]
            local requestedPipelineKey = ARGV[8]
            local reason = ARGV[9]

            if redis.call('EXISTS', itemKey) == 0 then
                return 'missing'
            end

            local storedSharedRunId =
                redis.call('HGET', itemKey, 'sharedRunId') or ''

            if storedSharedRunId ~= expectedSharedRunId then
                return 'shared-run-mismatch'
            end

            local status =
                redis.call('HGET', itemKey, 'status')

            if status ~= 'Pending' then
                return 'not-pending'
            end

            local itemPipelineKey =
                redis.call('HGET', itemKey, 'pipelineKey') or ''

            if requestedPipelineKey ~= '' and
               itemPipelineKey ~= requestedPipelineKey then
                return 'pipeline-mismatch'
            end

            local snapshotJson =
                redis.call(
                    'HGET',
                    itemKey,
                    'executionContextSnapshotJson')

            if snapshotJson == false or
               snapshotJson == nil or
               snapshotJson == '' then
                return 'invalid-snapshot'
            end

            local ok, snapshot =
                pcall(
                    cjson.decode,
                    snapshotJson)

            if not ok or snapshot == nil then
                return 'invalid-snapshot'
            end

            local itemTenantId =
                snapshot['TenantId'] or
                snapshot['tenantId'] or
                ''

            if requestedTenantId ~= '' and
               itemTenantId ~= requestedTenantId then
                return 'tenant-mismatch'
            end

            redis.call(
                'ZREM',
                globalPendingIndexKey,
                expectedSharedRunId)

            if tenantPendingIndexKey ~= nil and
               tenantPendingIndexKey ~= '' then
                redis.call(
                    'ZREM',
                    tenantPendingIndexKey,
                    expectedSharedRunId)
            end

            redis.call(
                'HSET',
                itemKey,
                'status',
                'Claimed',
                'claimedByRuntimeInstanceId',
                runtimeInstanceId,
                'claimedByWorkerId',
                workerId,
                'claimToken',
                claimToken,
                'claimedAtUtc',
                nowUtc,
                'claimExpiresAtUtc',
                claimExpiresAtUtc,
                'updatedAtUtc',
                nowUtc,
                'reason',
                reason)

            return 'claimed'
            """;

        /// <summary>
        /// Atomically requeues a dispatched shared queue item during execution recovery.
        /// </summary>
        /// <remarks>
        /// Expected keys:
        /// - KEYS[1]: queue item hash key.
        /// - KEYS[2]: global pending sorted-set index key.
        /// - KEYS[3]: optional tenant pending sorted-set index key.
        /// - KEYS[4]: global all-items sorted-set index key.
        /// - KEYS[5]: optional tenant all-items sorted-set index key.
        ///
        /// Expected arguments:
        /// - ARGV[1]: shared run id.
        /// - ARGV[2]: expected claim token.
        /// - ARGV[3]: queue score.
        /// - ARGV[4]: updated UTC timestamp.
        /// - ARGV[5]: reason.
        /// - ARGV[6]: merged metadata JSON.
        /// - ARGV[7]: effective queue priority.
        ///
        /// Metadata is written before the item is made claimable again, so a queue
        /// pump can never reclaim a recovery item without the recovery metadata.
        /// </remarks>
        public const string RequeueDispatched = """
            local itemKey = KEYS[1]
            local pendingIndexKey = KEYS[2]
            local tenantPendingIndexKey = KEYS[3]
            local allIndexKey = KEYS[4]
            local tenantAllIndexKey = KEYS[5]

            local sharedRunId = ARGV[1]
            local expectedClaimToken = ARGV[2]
            local score = ARGV[3]
            local updatedAtUtc = ARGV[4]
            local reason = ARGV[5]
            local metadataJson = ARGV[6]
            local priority = ARGV[7]

            if redis.call('EXISTS', itemKey) == 0 then
                return 'missing'
            end

            local status = redis.call('HGET', itemKey, 'status')
            local claimToken = redis.call('HGET', itemKey, 'claimToken')

            if status ~= 'Dispatched' then
                return 'not-dispatched'
            end

            if claimToken ~= expectedClaimToken then
                return 'not-owner'
            end

            redis.call(
                'HMSET',
                itemKey,
                'status', 'Pending',
                'claimedByRuntimeInstanceId', '',
                'claimedByWorkerId', '',
                'claimToken', '',
                'claimedAtUtc', '',
                'claimExpiresAtUtc', '',
                'updatedAtUtc', updatedAtUtc,
                'reason', reason,
                'priority', priority)

            if metadataJson ~= nil and metadataJson ~= '' then
                redis.call('HSET', itemKey, 'metadataJson', metadataJson)
            end

            redis.call('ZADD', pendingIndexKey, score, sharedRunId)
            redis.call('ZADD', allIndexKey, score, sharedRunId)

            if tenantPendingIndexKey ~= nil and tenantPendingIndexKey ~= '' then
                redis.call('ZADD', tenantPendingIndexKey, score, sharedRunId)
            end

            if tenantAllIndexKey ~= nil and tenantAllIndexKey ~= '' then
                redis.call('ZADD', tenantAllIndexKey, score, sharedRunId)
            end

            return 'requeued-dispatched'
            """;
    }
}
