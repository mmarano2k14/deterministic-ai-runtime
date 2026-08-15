using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Text;

namespace Multiplexed.AI.Stores.Cache.Redis.Lua
{
    public static class RedisDagLuaScripts
    {
        // ---------------------------------------------------------------------
        // LUA SCRIPTS
        // ---------------------------------------------------------------------

        /// <summary>
        /// Claims the next eligible step atomically.
        ///
        /// ELIGIBILITY RULES:
        /// - step must be in Ready or None state
        /// - OR step must be in WaitingForRetry and its retry window must be open
        /// - all dependencies must already be Completed
        ///
        /// IMPORTANT:
        /// - Step names are sorted for deterministic ordering across workers
        /// - DependsOn is normalized so empty arrays remain arrays and not objects
        /// - When a retry-waiting step is claimed again, RetryState.NextRetryAtUtc is cleared
        ///   because the retry window has already been consumed
        /// - nowUnix is expressed in milliseconds
        /// - LeaseExpiresAtUtc is computed once at claim time and becomes the
        ///   authoritative recovery boundary for this running claim
        /// </summary>
        public static readonly LuaScript ClaimPreparedScript = LuaScript.Prepare(
            """
            local function normalize_array(value)
                if value == nil or value == cjson.null then
                    return cjson.decode('[]')
                end

                local count = 0
                for _, _ in ipairs(value) do
                    count = count + 1
                end

                if count == 0 then
                    return cjson.decode('[]')
                end

                return value
            end

            local function resolve_claim_timeout_seconds(step)
                local timeoutSeconds = tonumber(step.ClaimTimeoutSeconds)

                if timeoutSeconds ~= nil and timeoutSeconds > 0 then
                    return timeoutSeconds
                end

                if step.Config ~= nil and step.Config ~= cjson.null then
                    timeoutSeconds =
                        tonumber(step.Config.ClaimTimeoutSeconds)
                        or tonumber(step.Config.claimTimeoutSeconds)
                        or tonumber(step.Config.ClaimTimeout)
                        or tonumber(step.Config.claimTimeout)

                    if timeoutSeconds ~= nil and timeoutSeconds > 0 then
                        return timeoutSeconds
                    end
                end

                return 60
            end

            local function resolve_claim_timeout_seconds(step)
                local timeoutSeconds = tonumber(step.ClaimTimeoutSeconds)

                if timeoutSeconds ~= nil and timeoutSeconds > 0 then
                    return timeoutSeconds
                end

                if step.Config ~= nil and step.Config ~= cjson.null then
                    timeoutSeconds =
                        tonumber(step.Config.ClaimTimeoutSeconds)
                        or tonumber(step.Config.claimTimeoutSeconds)
                        or tonumber(step.Config.ClaimTimeout)
                        or tonumber(step.Config.claimTimeout)

                    if timeoutSeconds ~= nil and timeoutSeconds > 0 then
                        return timeoutSeconds
                    end
                end

                return 60
            end

            local stepNames = redis.call('SMEMBERS', @stepIndexKey)

            local workerId = @workerId
            local nowUnix = tonumber(@nowUnix)
            local claimToken = @claimToken
            local stepKeyPrefix = @stepKeyPrefix
            local executionId = @executionId

            table.sort(stepNames)

            for _, stepName in ipairs(stepNames) do
                local stepKey = stepKeyPrefix .. stepName
                local raw = redis.call('GET', stepKey)

                if raw then
                    local step = cjson.decode(raw)

                    local canRun = false

                    if step.Status == "Ready" or step.Status == "None" then
                        canRun = true
                    end

                    if step.Status == "WaitingForRetry" then
                        local retryState = step.RetryState

                        if retryState == nil or retryState == cjson.null then
                            canRun = false
                        elseif retryState.NextRetryAtUtc == nil or retryState.NextRetryAtUtc == cjson.null then
                            canRun = true
                        else
                            local nextRetryAtUtc = tonumber(retryState.NextRetryAtUtc)

                            if nextRetryAtUtc ~= nil and nextRetryAtUtc <= nowUnix then
                                canRun = true
                            end
                        end
                    end

                    if canRun then
                        local deps = normalize_array(step.DependsOn)

                        for _, depName in ipairs(deps) do
                            local depKey = stepKeyPrefix .. depName
                            local depRaw = redis.call('GET', depKey)

                            if not depRaw then
                                canRun = false
                                break
                            end

                            local depStep = cjson.decode(depRaw)

                            if depStep.Status ~= "Completed" then
                                canRun = false
                                break
                            end
                        end
                    end

                    if canRun then
                        step.Status = "Running"
                        step.ClaimedBy = workerId
                        step.ClaimToken = claimToken
                        step.ClaimedAtUtc = nowUnix

                        if step.RetryState ~= nil and step.RetryState ~= cjson.null then
                            step.RetryState.NextRetryAtUtc = cjson.null
                        end

                        local timeoutSeconds = resolve_claim_timeout_seconds(step)
                        step.ClaimTimeoutSeconds = timeoutSeconds
                        step.LeaseExpiresAtUtc = nowUnix + (timeoutSeconds * 1000)

                        if step.StartedAtUtc == nil or step.StartedAtUtc == cjson.null then
                            step.StartedAtUtc = nowUnix
                        end

                        step.UpdatedAtUtc = nowUnix
                        step.Version = (step.Version or 0) + 1
                        step.DependsOn = normalize_array(step.DependsOn)

                        redis.call('SET', stepKey, cjson.encode(step))

                        return cjson.encode({
                            ExecutionId = executionId,
                            StepName = step.StepName,
                            ClaimToken = claimToken
                        })
                    end
                end
            end

            return nil
            """);


        /// <summary>
        /// Completes a claimed step atomically.
        ///
        /// RULES:
        /// - step must exist
        /// - step must be Running
        /// - claim token must match current ownership
        ///
        /// IMPORTANT:
        /// - nowUnix is expressed in milliseconds
        /// - LeaseExpiresAtUtc is cleared because the running claim is terminated
        /// </summary>
        public static readonly LuaScript CompletePreparedScript = LuaScript.Prepare(
            """
            local function normalize_array(value)
                if value == nil or value == cjson.null then
                    return cjson.decode('[]')
                end

                local count = 0
                for _, _ in ipairs(value) do
                    count = count + 1
                end

                if count == 0 then
                    return cjson.decode('[]')
                end

                return value
            end

            local inlinePayloadSizeBytes = tonumber(@inlinePayloadSizeBytes)

            local raw = redis.call('GET', @stepKey)
            if not raw then
                return 0
            end

            local step = cjson.decode(raw)
            if not step then
                return 0
            end

            if step.Status ~= "Running" then
                return 0
            end

            if step.ClaimToken ~= @claimToken then
                return 0
            end

            step.Status = "Completed"
            step.Result = cjson.decode(@resultJson)
            step.Error = cjson.null
            step.CompletedAtUtc = tonumber(@nowUnix)
            step.UpdatedAtUtc = tonumber(@nowUnix)
            step.ClaimedBy = cjson.null
            step.ClaimToken = cjson.null
            step.ClaimedAtUtc = cjson.null
            step.LeaseExpiresAtUtc = cjson.null
            step.InlinePayloadSizeBytes = inlinePayloadSizeBytes
            step.IsCompacted = false
            step.CompactedAtUtc = cjson.null
            step.CompactedAtUnixMs = cjson.null
            step.ArchivePayloadId = cjson.null
            step.CompactionReason = cjson.null

            if step.RetryState ~= nil and step.RetryState ~= cjson.null then
                step.RetryState.NextRetryAtUtc = cjson.null
            end

            step.Version = (step.Version or 0) + 1
            step.DependsOn = normalize_array(step.DependsOn)

            redis.call('SET', @stepKey, cjson.encode(step))
            return 1
            """);

        /// <summary>
        /// Parks a claimed step atomically while it waits for an external durable condition.
        /// </summary>
        /// <remarks>
        /// The script requires matching running claim ownership, transitions the step to
        /// WaitingForExternal, and clears the claim. It intentionally leaves retry state,
        /// recovery count, completion timestamps, result, and failure data unchanged.
        /// </remarks>
        public static readonly LuaScript ParkPreparedScript = LuaScript.Prepare(
            """
            local function normalize_array(value)
                if value == nil or value == cjson.null then
                    return cjson.decode('[]')
                end

                local count = 0
                for _, _ in ipairs(value) do
                    count = count + 1
                end

                if count == 0 then
                    return cjson.decode('[]')
                end

                return value
            end

            local raw = redis.call('GET', @stepKey)
            if not raw then
                return 0
            end

            local step = cjson.decode(raw)
            if not step then
                return 0
            end

            if step.Status ~= "Running" then
                return 0
            end

            if step.ClaimToken ~= @claimToken then
                return 0
            end

            step.Status = "WaitingForExternal"
            step.UpdatedAtUtc = tonumber(@nowUnix)
            step.ClaimedBy = cjson.null
            step.ClaimToken = cjson.null
            step.ClaimedAtUtc = cjson.null
            step.LeaseExpiresAtUtc = cjson.null
            step.Version = (step.Version or 0) + 1
            step.DependsOn = normalize_array(step.DependsOn)

            redis.call('SET', @stepKey, cjson.encode(step))
            return 1
            """);

        /// <summary>
        /// Reactivates one externally waiting DAG step atomically.
        /// </summary>
        /// <remarks>
        /// The script accepts only WaitingForExternal and moves the step to Ready. It clears any stale claim fields
        /// defensively and deliberately leaves retry count, recovery count, result, failure, and terminal data intact.
        /// </remarks>
        public static readonly LuaScript ResumeExternalWaitPreparedScript = LuaScript.Prepare(
            """
            local function normalize_array(value)
                if value == nil or value == cjson.null then
                    return cjson.decode('[]')
                end

                local count = 0
                for _, _ in ipairs(value) do
                    count = count + 1
                end

                if count == 0 then
                    return cjson.decode('[]')
                end

                return value
            end

            local raw = redis.call('GET', @stepKey)
            if not raw then
                return 0
            end

            local step = cjson.decode(raw)
            if not step or step.Status ~= "WaitingForExternal" then
                return 0
            end

            step.Status = "Ready"
            step.UpdatedAtUtc = tonumber(@nowUnix)
            step.ClaimedBy = cjson.null
            step.ClaimToken = cjson.null
            step.ClaimedAtUtc = cjson.null
            step.LeaseExpiresAtUtc = cjson.null
            step.Version = (step.Version or 0) + 1
            step.DependsOn = normalize_array(step.DependsOn)

            redis.call('SET', @stepKey, cjson.encode(step))
            return 1
            """);

        /// <summary>
        /// Fails a claimed step atomically.
        ///
        /// RULES:
        /// - step must exist
        /// - step must be Running
        /// - claim token must match current ownership
        ///
        /// RETRY BEHAVIOR:
        /// - retry configuration is read from step.Retry
        /// - mutable retry execution state is stored in step.RetryState
        /// - if retry budget remains:
        ///   -> increment RetryState.RetryCount
        ///   -> move to WaitingForRetry
        ///   -> schedule RetryState.NextRetryAtUtc
        ///   -> mirror NextRetryAtUtc on the step for claim eligibility
        /// - otherwise:
        ///   -> move to terminal Failed
        ///
        /// IMPORTANT:
        /// - nowUnix is expressed in milliseconds
        /// - LeaseExpiresAtUtc is cleared because the running claim is terminated
        ///   regardless of whether the step becomes WaitingForRetry or Failed
        /// - obsolete direct retry fields must not be used here
        /// </summary>
        public static readonly LuaScript FailPreparedScript = LuaScript.Prepare(
            """
            local function normalize_array(value)
                if value == nil or value == cjson.null then
                    return cjson.decode('[]')
                end

                local count = 0
                for _, _ in ipairs(value) do
                    count = count + 1
                end

                if count == 0 then
                    return cjson.decode('[]')
                end

                return value
            end

            local raw = redis.call('GET', @stepKey)
            if not raw then
                return 0
            end

            local step = cjson.decode(raw)
            if not step then
                return 0
            end

            if step.Status ~= "Running" then
                return 0
            end

            if step.ClaimToken ~= @claimToken then
                return 0
            end

            local nowUnix = tonumber(@nowUnix)

            local retryState = step.RetryState
            if retryState == nil or retryState == cjson.null then
                retryState = {}
            end

            local retryDefinition = step.Retry

            if retryDefinition == nil or retryDefinition == cjson.null then
                if step.Config ~= nil and step.Config ~= cjson.null then
                    retryDefinition = step.Config.retry or step.Config.Retry
                end
            end

            if retryDefinition == nil or retryDefinition == cjson.null then
                retryDefinition = {}
            end

            local retryCount = tonumber(retryState.RetryCount) or 0
            local maxRetries = tonumber(retryDefinition.MaxRetries or retryDefinition.maxRetries) or 3
            local baseDelayMs = tonumber(retryDefinition.BaseDelayMs or retryDefinition.baseDelayMs) or 500
            local maxDelayMs = tonumber(retryDefinition.MaxDelayMs or retryDefinition.maxDelayMs)
            local strategy = retryDefinition.Strategy or retryDefinition.strategy or "Fixed"

            local retryDelayMs = baseDelayMs

            if strategy == "Exponential" or strategy == "exponential" then
                retryDelayMs = baseDelayMs * (2 ^ retryCount)
            end

            if maxDelayMs ~= nil and retryDelayMs > maxDelayMs then
                retryDelayMs = maxDelayMs
            end

            if retryDelayMs < 0 then
                retryDelayMs = 0
            end

            step.Error = @error
            step.UpdatedAtUtc = nowUnix
            step.ClaimedBy = cjson.null
            step.ClaimToken = cjson.null
            step.ClaimedAtUtc = cjson.null
            step.LeaseExpiresAtUtc = cjson.null

            if retryCount < maxRetries then
                retryCount = retryCount + 1

                local nextRetryAtUtc = nowUnix + retryDelayMs

                retryState.RetryCount = retryCount
                retryState.RetryReason = @error
                retryState.LastRetryAtUtc = nowUnix
                retryState.NextRetryAtUtc = nextRetryAtUtc

                step.RetryState = retryState
                step.Status = "WaitingForRetry"
                step.CompletedAtUtc = cjson.null
                step.Version = (step.Version or 0) + 1
                step.DependsOn = normalize_array(step.DependsOn)

                redis.call('SET', @stepKey, cjson.encode(step))
                return 1
            end

            step.Status = "Failed"
            step.CompletedAtUtc = nowUnix

            retryState.NextRetryAtUtc = cjson.null
            step.RetryState = retryState

            step.Version = (step.Version or 0) + 1
            step.DependsOn = normalize_array(step.DependsOn)

            redis.call('SET', @stepKey, cjson.encode(step))
            return 1
            """);

        /// <summary>
        /// Recovers timed-out running steps.
        ///
        /// RULES:
        /// - only Running steps are considered
        /// - LeaseExpiresAtUtc must be in the past
        /// - recovered steps transition back to Ready
        /// - claim ownership is cleared
        /// - RecoveryCount is incremented
        ///
        /// IMPORTANT:
        /// - Infrastructure recovery must not consume business RetryCount
        /// - LeaseExpiresAtUtc is the authoritative recovery boundary
        /// - Recovery no longer recomputes expiration from ClaimedAtUtc and ClaimTimeoutSeconds
        /// </summary>
        public static readonly LuaScript RecoverPreparedScript = LuaScript.Prepare(
            """
            local function normalize_array(value)
                if value == nil or value == cjson.null then
                    return cjson.decode('[]')
                end

                local count = 0
                for _, _ in ipairs(value) do
                    count = count + 1
                end

                if count == 0 then
                    return cjson.decode('[]')
                end

                return value
            end

            local stepNames = redis.call('SMEMBERS', @stepIndexKey)
            local nowUnix = tonumber(@nowUnix)
            local stepKeyPrefix = @stepKeyPrefix
            local recovered = 0

            table.sort(stepNames)

            for _, stepName in ipairs(stepNames) do
                local stepKey = stepKeyPrefix .. stepName
                local raw = redis.call('GET', stepKey)

                if raw then
                    local step = cjson.decode(raw)

                    if step.Status == "Running" or step.Status == 2 then
                        local leaseExpiresAt = step.LeaseExpiresAtUtc
                        local shouldRecover = false

                        if leaseExpiresAt ~= nil and leaseExpiresAt ~= cjson.null then
                            shouldRecover = tonumber(leaseExpiresAt) <= nowUnix
                        else
                            local claimedAt = tonumber(step.ClaimedAtUtc)
                            local timeoutSeconds = tonumber(step.ClaimTimeoutSeconds)

                            if timeoutSeconds == nil or timeoutSeconds <= 0 then
                                timeoutSeconds = 60
                            end

                            if claimedAt ~= nil and claimedAt + (timeoutSeconds * 1000) <= nowUnix then
                                shouldRecover = true
                            end
                        end

                        if shouldRecover then
                            step.Status = "Ready"
                            step.ClaimedBy = cjson.null
                            step.ClaimToken = cjson.null
                            step.ClaimedAtUtc = cjson.null
                            step.LeaseExpiresAtUtc = cjson.null
                            step.UpdatedAtUtc = nowUnix
                            step.RecoveryCount = (step.RecoveryCount or 0) + 1
                            step.Version = (step.Version or 0) + 1
                            step.DependsOn = normalize_array(step.DependsOn)

                            redis.call('SET', stepKey, cjson.encode(step))
                            recovered = recovered + 1
                        end
                    end
                end
            end

            return recovered
            """);

        /// <summary>
        /// Recovers all currently running steps for an explicit runtime recovery.
        ///
        /// RULES:
        /// - only Running steps are considered
        /// - lease expiration is deliberately ignored
        /// - recovered steps transition back to Ready
        /// - claim ownership is cleared
        /// - RecoveryCount is incremented
        ///
        /// IMPORTANT:
        /// - this operation must run only after recovery pause ownership has
        ///   been acquired for the execution
        /// - infrastructure recovery does not consume business RetryCount
        /// - repeated execution is idempotent
        /// </summary>
        public static readonly LuaScript RecoverRunningForRecoveryPreparedScript =
            LuaScript.Prepare(
                """
                local function normalize_array(value)
                    if value == nil or value == cjson.null then
                        return cjson.decode('[]')
                    end

                    local count = 0

                    for _, _ in ipairs(value) do
                        count = count + 1
                    end

                    if count == 0 then
                        return cjson.decode('[]')
                    end

                    return value
                end

                local stepNames =
                    redis.call(
                        'SMEMBERS',
                        @stepIndexKey)

                local nowUnix =
                    tonumber(@nowUnix)

                local stepKeyPrefix =
                    @stepKeyPrefix

                local recovered =
                    0

                table.sort(stepNames)

                for _, stepName in ipairs(stepNames) do
                    local stepKey =
                        stepKeyPrefix .. stepName

                    local raw =
                        redis.call(
                            'GET',
                            stepKey)

                    if raw then
                        local step =
                            cjson.decode(raw)

                        if step and
                           (step.Status == 'Running' or step.Status == 2) then
                            step.Status =
                                'Ready'

                            step.ClaimedBy =
                                cjson.null

                            step.ClaimToken =
                                cjson.null

                            step.ClaimedAtUtc =
                                cjson.null

                            step.LeaseExpiresAtUtc =
                                cjson.null

                            step.UpdatedAtUtc =
                                nowUnix

                            step.RecoveryCount =
                                (tonumber(step.RecoveryCount) or 0) + 1

                            step.Version =
                                (tonumber(step.Version) or 0) + 1

                            step.DependsOn =
                                normalize_array(step.DependsOn)

                            redis.call(
                                'SET',
                                stepKey,
                                cjson.encode(step))

                            recovered =
                                recovered + 1
                        end
                    end
                end

                return recovered
                """);

        /// <summary>
        /// Atomically finalizes the global execution record.
        ///
        /// GUARANTEES:
        /// - validates ExecutionStepKey
        /// - refuses to finalize an already terminal record
        /// - persists terminal status and completion metadata atomically
        /// - terminal states are monotonic
        /// </summary>
        public static readonly LuaScript FinalizeScript = LuaScript.Prepare(
            """
            local raw = redis.call('GET', @recordKey)
            if not raw then
                return 0
            end

            local record = cjson.decode(raw)
            if not record then
                return 0
            end

            if record.ExecutionStepKey ~= @expectedExecutionStepKey then
                return 0
            end

            if record.Status == 'Completed'
                or record.Status == 'Failed'
                or record.Status == 'Cancelled' then
                return 0
            end

            record.Status = @status
            record.CompletedAtUtc = tonumber(@completedAtUtc)
            record.CompletedSteps = cjson.decode(@completedStepsJson)
            record.CurrentStep = ''
            record.UpdatedAtUtc = tonumber(@completedAtUtc)
            record.Version = (record.Version or 0) + 1
            record.ExecutionStepKey = @newExecutionStepKey

            redis.call('SET', @recordKey, cjson.encode(record))
            return 1
            """);

        /// <summary>
        /// Claims multiple eligible DAG steps atomically for bounded parallel execution.
        ///
        /// ELIGIBILITY RULES:
        /// - step must be in Ready or None state
        /// - OR step must be in WaitingForRetry and its retry window must be open
        /// - all dependencies must already be Completed
        ///
        /// IMPORTANT:
        /// - Existing single-step claim behavior remains unchanged.
        /// - Step names are sorted for deterministic ordering.
        /// - At most maxSteps are claimed.
        /// - Each claimed step receives its own claim token provided by the caller.
        /// - Claim tokens must be unique per claimed step.
        /// </summary>
        public static readonly LuaScript ClaimBatchPreparedScript = LuaScript.Prepare(
            """
            local function normalize_array(value)
                if value == nil or value == cjson.null then
                    return cjson.decode('[]')
                end

                local count = 0
                for _, _ in ipairs(value) do
                    count = count + 1
                end

                if count == 0 then
                    return cjson.decode('[]')
                end

                return value
            end

            local stepNames = redis.call('SMEMBERS', @stepIndexKey)

            local workerId = @workerId
            local nowUnix = tonumber(@nowUnix)
            local stepKeyPrefix = @stepKeyPrefix
            local executionId = @executionId
            local maxSteps = tonumber(@maxSteps)
            local claimTokens = cjson.decode(@claimTokensJson)

            local claimed = {}

            table.sort(stepNames)

            for _, stepName in ipairs(stepNames) do
                if #claimed >= maxSteps then
                    break
                end

                local stepKey = stepKeyPrefix .. stepName
                local raw = redis.call('GET', stepKey)

                if raw then
                    local step = cjson.decode(raw)
                    local canRun = false

                    if step.Status == "Ready" or step.Status == "None" then
                        canRun = true
                    end

                    if step.Status == "WaitingForRetry" then
                        local retryState = step.RetryState

                        if retryState == nil or retryState == cjson.null then
                            canRun = false
                        elseif retryState.NextRetryAtUtc == nil or retryState.NextRetryAtUtc == cjson.null then
                            canRun = true
                        else
                            local nextRetryAtUtc = tonumber(retryState.NextRetryAtUtc)

                            if nextRetryAtUtc ~= nil and nextRetryAtUtc <= nowUnix then
                                canRun = true
                            end
                        end
                    end

                    if canRun then
                        local deps = normalize_array(step.DependsOn)

                        for _, depName in ipairs(deps) do
                            local depKey = stepKeyPrefix .. depName
                            local depRaw = redis.call('GET', depKey)

                            if not depRaw then
                                canRun = false
                                break
                            end

                            local depStep = cjson.decode(depRaw)

                            if depStep.Status ~= "Completed" then
                                canRun = false
                                break
                            end
                        end
                    end

                    if canRun then
                        local claimToken = claimTokens[#claimed + 1]

                        if claimToken == nil or claimToken == cjson.null or claimToken == "" then
                            break
                        end

                        step.Status = "Running"
                        step.ClaimedBy = workerId
                        step.ClaimToken = claimToken
                        step.ClaimedAtUtc = nowUnix

                        if step.RetryState ~= nil and step.RetryState ~= cjson.null then
                            step.RetryState.NextRetryAtUtc = cjson.null
                        end

                        local timeoutSeconds = resolve_claim_timeout_seconds(step)
                        step.ClaimTimeoutSeconds = timeoutSeconds
                        step.LeaseExpiresAtUtc = nowUnix + (timeoutSeconds * 1000)

                        if step.StartedAtUtc == nil or step.StartedAtUtc == cjson.null then
                            step.StartedAtUtc = nowUnix
                        end

                        step.UpdatedAtUtc = nowUnix
                        step.Version = (step.Version or 0) + 1
                        step.DependsOn = normalize_array(step.DependsOn)

                        redis.call('SET', stepKey, cjson.encode(step))

                        table.insert(claimed, {
                            ExecutionId = executionId,
                            StepName = step.StepName,
                            ClaimToken = claimToken
                        })
                    end
                end
            end

            if #claimed == 0 then
                return '[]'
            end

            return cjson.encode(claimed)
            """);

        /// <summary>
        /// Claims a specific eligible DAG step atomically.
        ///
        /// ELIGIBILITY RULES:
        /// - selected step must exist
        /// - selected step must be in Ready or None state
        /// - OR selected step must be in WaitingForRetry and its retry window must be open
        /// - all dependencies must already be Completed
        ///
        /// IMPORTANT:
        /// - This script does not scan or choose a step.
        /// - It only validates and claims the provided step name.
        /// - nowUnix is expressed in milliseconds.
        /// - LeaseExpiresAtUtc is computed once at claim time.
        /// </summary>
        public static readonly LuaScript ClaimSpecificPreparedScript = LuaScript.Prepare(
            """
            local function normalize_array(value)
                if value == nil or value == cjson.null then
                    return cjson.decode('[]')
                end

                local count = 0
                for _, _ in ipairs(value) do
                    count = count + 1
                end

                if count == 0 then
                    return cjson.decode('[]')
                end

                return value
            end

            local function resolve_claim_timeout_seconds(step)
                local timeoutSeconds = tonumber(step.ClaimTimeoutSeconds)

                if timeoutSeconds ~= nil and timeoutSeconds > 0 then
                    return timeoutSeconds
                end

                if step.Config ~= nil and step.Config ~= cjson.null then
                    timeoutSeconds =
                        tonumber(step.Config.ClaimTimeoutSeconds)
                        or tonumber(step.Config.claimTimeoutSeconds)
                        or tonumber(step.Config.ClaimTimeout)
                        or tonumber(step.Config.claimTimeout)

                    if timeoutSeconds ~= nil and timeoutSeconds > 0 then
                        return timeoutSeconds
                    end
                end

                return 60
            end

            local stepKey = @stepKey
            local stepKeyPrefix = @stepKeyPrefix
            local workerId = @workerId
            local nowUnix = tonumber(@nowUnix)
            local claimToken = @claimToken

            local raw = redis.call('GET', stepKey)
            if not raw then
                return 0
            end

            local step = cjson.decode(raw)
            if not step then
                return 0
            end

            local canRun = false

            if step.Status == "Ready" or step.Status == "None" then
                canRun = true
            end

            if step.Status == "WaitingForRetry" then
                local retryState = step.RetryState

                if retryState == nil or retryState == cjson.null then
                    canRun = false
                elseif retryState.NextRetryAtUtc == nil or retryState.NextRetryAtUtc == cjson.null then
                    canRun = true
                else
                    local nextRetryAtUtc = tonumber(retryState.NextRetryAtUtc)

                    if nextRetryAtUtc ~= nil and nextRetryAtUtc <= nowUnix then
                        canRun = true
                    end
                end
            end

            if not canRun then
                return 0
            end

            local deps = normalize_array(step.DependsOn)

            for _, depName in ipairs(deps) do
                local depKey = stepKeyPrefix .. depName
                local depRaw = redis.call('GET', depKey)

                if not depRaw then
                    return 0
                end

                local depStep = cjson.decode(depRaw)

                if depStep.Status ~= "Completed" then
                    return 0
                end
            end

            step.Status = "Running"
            step.ClaimedBy = workerId
            step.ClaimToken = claimToken
            step.ClaimedAtUtc = nowUnix

            if step.RetryState ~= nil and step.RetryState ~= cjson.null then
                step.RetryState.NextRetryAtUtc = cjson.null
            end

            local timeoutSeconds = resolve_claim_timeout_seconds(step)
            step.ClaimTimeoutSeconds = timeoutSeconds
            step.LeaseExpiresAtUtc = nowUnix + (timeoutSeconds * 1000)

            if step.StartedAtUtc == nil or step.StartedAtUtc == cjson.null then
                step.StartedAtUtc = nowUnix
            end

            step.UpdatedAtUtc = nowUnix
            step.Version = (step.Version or 0) + 1
            step.DependsOn = normalize_array(step.DependsOn)

            redis.call('SET', stepKey, cjson.encode(step))

            return 1
            """);

        /// <summary>
        /// Applies atomic retention patches to selected DAG step keys.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This script is used for active distributed retention. It never overwrites the full
        /// execution state. Each candidate is checked against the current stored step before
        /// being compacted or logically evicted.
        /// </para>
        ///
        /// <para>
        /// Active eviction is logical: the step shell remains in Redis, while heavy payload/result
        /// data is cleared and replaced by archive metadata.
        /// </para>
        ///
        /// <para>
        /// This script intentionally does not delete step keys and does not remove step ids from
        /// the DAG step id set during active execution. Physical deletion is reserved for terminal
        /// winner-only retention after finalization.
        /// </para>
        /// </remarks>
        public static readonly LuaScript RetentionPatchPreparedScript = LuaScript.Prepare(
            """
            local candidates = cjson.decode(@candidatesJson)
            local stepKeyPrefix = @stepKeyPrefix
            local stepIdsKey = @stepIdsKey
            local nowUnix = tonumber(@nowUnix)

            local compacted = {}
            local evicted = {}
            local skipped = {}

            local function is_null_or_empty(value)
                return value == nil or value == cjson.null or value == ''
            end

            local function add_skipped(stepName)
                if not is_null_or_empty(stepName) then
                    table.insert(skipped, stepName)
                end
            end

            local function normalize_action(action)
                if action == 0 or action == '0' or action == 'Compact' then
                    return 'Compact'
                end

                if action == 1 or action == '1' or action == 'Evict' then
                    return 'Evict'
                end

                return ''
            end

            local function is_terminal(status)
                return status == 'Completed' or status == 'Failed'
            end

            local function increment_version(step)
                if step.Version ~= nil and step.Version ~= cjson.null then
                    step.Version = tonumber(step.Version) + 1
                else
                    step.Version = 1
                end
            end

            local function clear_heavy_payload(step)
                if step.Result ~= nil and step.Result ~= cjson.null then
                    step.Result = cjson.null
                end

                if step.Output ~= nil and step.Output ~= cjson.null then
                    step.Output = cjson.null
                end

                if step.Payload ~= nil and step.Payload ~= cjson.null then
                    step.Payload = cjson.null
                end

                if step.Data ~= nil and step.Data ~= cjson.null then
                    step.Data = cjson.null
                end

                step.InlinePayloadSizeBytes = 0
                step.inlinePayloadSizeBytes = 0
            end

            for _, candidate in ipairs(candidates) do
                local stepName = candidate.StepName
                local action = normalize_action(candidate.Action)

                if is_null_or_empty(stepName) or is_null_or_empty(action) then
                    add_skipped(stepName)
                else
                    local stepKey = stepKeyPrefix .. stepName
                    local raw = redis.call('GET', stepKey)

                    if not raw then
                        add_skipped(stepName)
                    else
                        local step = cjson.decode(raw)

                        if not step then
                            add_skipped(stepName)
                        else
                            local safe = true

                            local expectedExecutionId = candidate.ExpectedExecutionId
                            local expectedStatus = candidate.ExpectedStatus

                            local currentExecutionId = step.ExecutionId
                            local currentStatus = step.Status

                            if not is_null_or_empty(expectedExecutionId) then
                                if not is_null_or_empty(currentExecutionId) and currentExecutionId ~= expectedExecutionId then
                                    safe = false
                                end
                            end

                            if not is_null_or_empty(expectedStatus) then
                                if currentStatus ~= expectedStatus then
                                    safe = false
                                end
                            end

                            if not is_terminal(currentStatus) then
                                safe = false
                            end

                            if currentStatus == 'Running' then
                                safe = false
                            end

                            if action == 'Evict' and step.IsEvictedFromHotState == true then
                                safe = false
                            end

                            if action == 'Compact' and step.IsCompacted == true then
                                safe = false
                            end

                            if action == 'Compact' and step.IsEvictedFromHotState == true then
                                safe = false
                            end

                            if not safe then
                                add_skipped(stepName)
                            else
                                if action == 'Evict' then
                                    step.ArchivePayloadId = candidate.ArchivePayloadId
                                    step.RetentionReason = candidate.Reason
                                    step.IsEvictedFromHotState = true
                                    step.EvictedAtUnixMs = nowUnix
                                    step.UpdatedAtUtc = nowUnix
                                    step.UpdatedAtUnixMs = nowUnix

                                    clear_heavy_payload(step)
                                    increment_version(step)

                                    redis.call('SET', stepKey, cjson.encode(step))
                                    table.insert(evicted, stepName)
                                elseif action == 'Compact' then
                                    step.ArchivePayloadId = candidate.ArchivePayloadId
                                    step.RetentionReason = candidate.Reason
                                    step.CompactionReason = candidate.Reason
                                    step.IsCompacted = true
                                    step.CompactedAtUtc = nowUnix
                                    step.CompactedAtUnixMs = nowUnix
                                    step.UpdatedAtUtc = nowUnix
                                    step.UpdatedAtUnixMs = nowUnix

                                    clear_heavy_payload(step)
                                    increment_version(step)

                                    redis.call('SET', stepKey, cjson.encode(step))
                                    table.insert(compacted, stepName)
                                else
                                    add_skipped(stepName)
                                end
                            end
                        end
                    end
                end
            end

            return cjson.encode({
                CompactedSteps = compacted,
                EvictedSteps = evicted,
                SkippedSteps = skipped
            })
            """);

    }
}
