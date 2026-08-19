using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.AI.Stores.Cache.Redis.Helpers;
using Multiplexed.AI.Stores.Cache.Redis.Lua;
using StackExchange.Redis;
using System.Text.Json;

namespace Multiplexed.AI.Stores.Cache.Redis.Dag
{
    /// <summary>
    /// Handles Redis DAG step claim operations.
    /// </summary>
    /// <remarks>
    /// This service owns all claim-related Redis Lua execution paths for DAG steps:
    /// single-step claim, batch claim, specific-step claim, and ready-step discovery.
    /// </remarks>
    public sealed class RedisDagStoreClaimService
    {
        private readonly IRedisDagStoreServices _services;
        private LuaScript _claimScript;
        private LuaScript _claimBatchScript;
        private LuaScript _claimSpecificScript;

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisDagStoreClaimService"/> class.
        /// </summary>
        /// <param name="services">The shared Redis DAG store services.</param>
        public RedisDagStoreClaimService(IRedisDagStoreServices services)
        {
            ArgumentNullException.ThrowIfNull(services);
            _services = services;

            _claimScript = RedisDagLuaScripts.ClaimPreparedScript;
            _claimBatchScript = RedisDagLuaScripts.ClaimBatchPreparedScript;
            _claimSpecificScript = RedisDagLuaScripts.ClaimSpecificPreparedScript;
        }

        /// <summary>
        /// Attempts to claim the next eligible step atomically.
        ///
        /// CLAIM RULES:
        /// - step must be Ready or None
        /// - OR WaitingForRetry with retry window open
        /// - all dependencies must already be Completed
        /// - step transitions to Running atomically
        ///
        /// IMPORTANT:
        /// - nowUnix is expressed in milliseconds
        /// - a new claim token is generated per claim attempt
        /// - lease expiration is persisted atomically inside Lua
        /// </summary>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <param name="workerId">The worker identifier requesting the claim.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The claimed step when a step was claimed; otherwise <c>null</c>.</returns>
        public async Task<AiClaimedStep?> TryClaimNextReadyStepAsync(
            string executionId,
            string workerId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));

            if (string.IsNullOrWhiteSpace(workerId))
                throw new ArgumentException("Worker id cannot be null or empty.", nameof(workerId));

            var nowUnix = RedisDagStoreHelper.NowMs();

            var claimToken = Guid.NewGuid().ToString("N");
            var stepIndexKey = _services.KeyBuilder.GetDagStepIdsKey(executionId);
            var stepKeyPrefix = _services.KeyBuilder.GetDagStepKeyPrefix(executionId);

            try
            {
                var claimed = await ExecuteClaimAsync(
                    stepIndexKey,
                    workerId,
                    nowUnix,
                    claimToken,
                    stepKeyPrefix,
                    executionId);

                if (claimed is not null)
                {
                    _services.Metrics.Execution.RecordStepClaimed(
                        executionId,
                        claimed.StepName);

                    _services.Logger.Engine.LogInformation(
                        $"[AI DAG STORE] Step claimed. ExecutionId='{executionId}', StepName='{claimed.StepName}', WorkerId='{workerId}', ClaimToken='{claimed.ClaimToken}'.");
                }
                else
                {
                    _services.Metrics.Execution.RecordStepClaimMiss(executionId);
                }

                return claimed;
            }
            catch (RedisServerException ex) when (ex.Message.Contains("NOSCRIPT", StringComparison.OrdinalIgnoreCase))
            {
                _claimScript = RedisDagLuaScripts.ClaimPreparedScript;

                var claimed = await ExecuteClaimAsync(
                    stepIndexKey,
                    workerId,
                    nowUnix,
                    claimToken,
                    stepKeyPrefix,
                    executionId);

                if (claimed is not null)
                {
                    _services.Metrics.Execution.RecordStepClaimed(
                        executionId,
                        claimed.StepName);

                    _services.Logger.Engine.LogInformation(
                        $"[AI DAG STORE] Step claimed after NOSCRIPT retry. ExecutionId='{executionId}', StepName='{claimed.StepName}', WorkerId='{workerId}', ClaimToken='{claimed.ClaimToken}'.");
                }
                else
                {
                    _services.Metrics.Execution.RecordStepClaimMiss(executionId);
                }

                return claimed;
            }
        }

        /// <summary>
        /// Attempts to atomically claim multiple eligible DAG steps for bounded parallel execution.
        ///
        /// CLAIM RULES:
        /// - each step must be Ready or None
        /// - OR WaitingForRetry with retry window open
        /// - all dependencies must already be Completed
        /// - each claimed step transitions to Running atomically
        ///
        /// IMPORTANT:
        /// - Existing single-step claim behavior remains unchanged.
        /// - nowUnix is expressed in milliseconds.
        /// - one unique claim token is generated per requested claim slot.
        /// - at most <paramref name="maxSteps"/> steps are claimed.
        /// </summary>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <param name="workerId">The worker identifier requesting the claims.</param>
        /// <param name="maxSteps">The maximum number of steps to claim.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The collection of successfully claimed DAG steps.</returns>
        public async Task<IReadOnlyList<AiClaimedStep>> TryClaimReadyStepsAsync(
            string executionId,
            string workerId,
            int maxSteps,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));

            if (string.IsNullOrWhiteSpace(workerId))
                throw new ArgumentException("Worker id cannot be null or empty.", nameof(workerId));

            ArgumentOutOfRangeException.ThrowIfLessThan(maxSteps, 1);

            var nowUnix = RedisDagStoreHelper.NowMs();
            var stepIndexKey = _services.KeyBuilder.GetDagStepIdsKey(executionId);
            var stepKeyPrefix = _services.KeyBuilder.GetDagStepKeyPrefix(executionId);

            var claimTokens = Enumerable
                .Range(0, maxSteps)
                .Select(_ => Guid.NewGuid().ToString("N"))
                .ToArray();

            var claimTokensJson = JsonSerializer.Serialize(claimTokens, _services.JsonOptions);

            try
            {
                var claimed = await ExecuteClaimBatchAsync(
                    stepIndexKey,
                    workerId,
                    nowUnix,
                    stepKeyPrefix,
                    executionId,
                    maxSteps,
                    claimTokensJson);

                if (claimed.Count > 0)
                {
                    foreach (var step in claimed)
                    {
                        _services.Metrics.Execution.RecordStepClaimed(
                            executionId,
                            step.StepName);

                        _services.Logger.Engine.LogInformation(
                            $"[AI DAG STORE] Step batch-claimed. ExecutionId='{executionId}', StepName='{step.StepName}', WorkerId='{workerId}', ClaimToken='{step.ClaimToken}'.");
                    }
                }
                else
                {
                    _services.Metrics.Execution.RecordStepClaimMiss(executionId);
                }

                return claimed;
            }
            catch (RedisServerException ex) when (ex.Message.Contains("NOSCRIPT", StringComparison.OrdinalIgnoreCase))
            {
                _claimBatchScript = RedisDagLuaScripts.ClaimBatchPreparedScript;

                var claimed = await ExecuteClaimBatchAsync(
                    stepIndexKey,
                    workerId,
                    nowUnix,
                    stepKeyPrefix,
                    executionId,
                    maxSteps,
                    claimTokensJson);

                if (claimed.Count > 0)
                {
                    foreach (var step in claimed)
                    {
                        _services.Metrics.Execution.RecordStepClaimed(
                            executionId,
                            step.StepName);

                        _services.Logger.Engine.LogInformation(
                            $"[AI DAG STORE] Step batch-claimed after NOSCRIPT retry. ExecutionId='{executionId}', StepName='{step.StepName}', WorkerId='{workerId}', ClaimToken='{step.ClaimToken}'.");
                    }
                }
                else
                {
                    _services.Metrics.Execution.RecordStepClaimMiss(executionId);
                }

                return claimed;
            }
        }

        /// <summary>
        /// Attempts to atomically claim one specific DAG step.
        /// </summary>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <param name="stepName">The specific step name to claim.</param>
        /// <param name="workerId">The worker identifier requesting the claim.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The claimed step when the claim succeeds; otherwise <c>null</c>.</returns>
        public async Task<AiClaimedStep?> TryClaimStepAsync(
            string executionId,
            string stepName,
            string workerId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
            ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

            var claimToken = Guid.NewGuid().ToString("N");

            try
            {
                var claimed = await ExecuteTryClaimStepScriptAsync(
                    executionId,
                    stepName,
                    workerId,
                    claimToken).ConfigureAwait(false);

                if (!claimed)
                {
                    _services.Metrics.Execution.RecordStepClaimMiss(executionId);
                    return null;
                }

                _services.Metrics.Execution.RecordStepClaimed(executionId, stepName);

                _services.Logger.Engine.LogInformation(
                    $"[AI DAG STORE] Specific step claimed. ExecutionId='{executionId}', StepName='{stepName}', WorkerId='{workerId}', ClaimToken='{claimToken}'.");

                return new AiClaimedStep
                {
                    ExecutionId = executionId,
                    StepName = stepName,
                    ClaimToken = claimToken
                };
            }
            catch (RedisServerException ex) when (ex.Message.Contains("NOSCRIPT", StringComparison.OrdinalIgnoreCase))
            {
                _claimSpecificScript = RedisDagLuaScripts.ClaimSpecificPreparedScript;

                var claimed = await ExecuteTryClaimStepScriptAsync(
                    executionId,
                    stepName,
                    workerId,
                    claimToken).ConfigureAwait(false);

                if (!claimed)
                {
                    _services.Metrics.Execution.RecordStepClaimMiss(executionId);
                    return null;
                }

                _services.Metrics.Execution.RecordStepClaimed(executionId, stepName);

                _services.Logger.Engine.LogInformation(
                    $"[AI DAG STORE] Specific step claimed after NOSCRIPT retry. ExecutionId='{executionId}', StepName='{stepName}', WorkerId='{workerId}', ClaimToken='{claimToken}'.");

                return new AiClaimedStep
                {
                    ExecutionId = executionId,
                    StepName = stepName,
                    ClaimToken = claimToken
                };
            }
        }

        // ---------------------------------------------------------------------
        // SCRIPT EXECUTION HELPERS
        // ---------------------------------------------------------------------

        /// <summary>
        /// Executes the prepared single-step claim Lua script.
        /// </summary>
        /// <param name="stepIndexKey">The Redis key containing the execution step index.</param>
        /// <param name="workerId">The worker identifier requesting the claim.</param>
        /// <param name="nowUnix">The current UTC timestamp expressed in Unix milliseconds.</param>
        /// <param name="claimToken">The generated claim token.</param>
        /// <param name="stepKeyPrefix">The Redis key prefix used to address execution step keys.</param>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <returns>The claimed step when a step was claimed; otherwise <c>null</c>.</returns>
        private async Task<AiClaimedStep?> ExecuteClaimAsync(
            string stepIndexKey,
            string workerId,
            long nowUnix,
            string claimToken,
            string stepKeyPrefix,
            string executionId)
        {
            var result = await _claimScript.EvaluateAsync(
                _services.Database,
                new
                {
                    stepIndexKey = (RedisKey)stepIndexKey,
                    workerId = (RedisValue)workerId,
                    nowUnix = (RedisValue)nowUnix,
                    claimToken = (RedisValue)claimToken,
                    stepKeyPrefix = (RedisValue)stepKeyPrefix,
                    executionId = (RedisValue)executionId
                });

            if (result.IsNull)
                return null;

            return JsonSerializer.Deserialize<AiClaimedStep>((string)result!, _services.JsonOptions);
        }

        /// <summary>
        /// Gets the currently ready DAG steps without claiming them.
        /// </summary>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <param name="maxSteps">The maximum number of ready steps to return.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The collection of ready steps ordered deterministically.</returns>
        public async Task<IReadOnlyList<AiClaimedStep>> GetReadyStepsAsync(
            string executionId,
            int maxSteps,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxSteps, 1);

            var state = await _services.StateReader.GetStateAsync(executionId, cancellationToken)
                .ConfigureAwait(false);

            if (state is null || state.Steps.Count == 0)
            {
                return Array.Empty<AiClaimedStep>();
            }

            var nowUtc = DateTime.UtcNow;

            var completedSteps = state.Steps
                .Where(step => step.Value.Status == AiStepExecutionStatus.Completed)
                .Select(step => step.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return state.Steps
                .Where(step => IsClaimCandidate(step.Value, nowUtc))
                .Where(step =>
                    step.Value.DependsOn is null ||
                    step.Value.DependsOn.Count == 0 ||
                    step.Value.DependsOn.All(completedSteps.Contains))
                .OrderBy(step => step.Key, StringComparer.OrdinalIgnoreCase)
                .Take(maxSteps)
                .Select(step => new AiClaimedStep
                {
                    ExecutionId = executionId,
                    StepName = step.Key,
                    ClaimToken = string.Empty
                })
                .ToList();
        }

        /// <summary>
        /// Determines whether a DAG step is eligible for pre-claim concurrency evaluation.
        /// </summary>
        /// <param name="step">The step state.</param>
        /// <param name="nowUtc">The current UTC timestamp.</param>
        /// <returns><c>true</c> when the step may be considered for claim; otherwise <c>false</c>.</returns>
        private static bool IsClaimCandidate(
            AiStepState step,
            DateTime nowUtc)
        {
            if (step.Status is AiStepExecutionStatus.Ready or AiStepExecutionStatus.None)
            {
                return true;
            }

            if (step.Status != AiStepExecutionStatus.WaitingForRetry)
            {
                return false;
            }

            var retryState = step.RetryState;

            if (retryState is null)
            {
                return false;
            }

            if (!retryState.NextRetryAtUtc.HasValue)
            {
                return true;
            }

            return retryState.NextRetryAtUtc.Value <= nowUtc;
        }

        /// <summary>
        /// Executes the prepared batch claim Lua script.
        /// </summary>
        /// <param name="stepIndexKey">The Redis key containing the execution step index.</param>
        /// <param name="workerId">The worker identifier requesting the claims.</param>
        /// <param name="nowUnix">The current UTC timestamp expressed in Unix milliseconds.</param>
        /// <param name="stepKeyPrefix">The Redis key prefix used to address execution step keys.</param>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <param name="maxSteps">The maximum number of ready steps to claim.</param>
        /// <param name="claimTokensJson">The JSON array of pre-generated claim tokens.</param>
        /// <returns>The collection of successfully claimed DAG steps.</returns>
        private async Task<IReadOnlyList<AiClaimedStep>> ExecuteClaimBatchAsync(
            string stepIndexKey,
            string workerId,
            long nowUnix,
            string stepKeyPrefix,
            string executionId,
            int maxSteps,
            string claimTokensJson)
        {
            var result = await _claimBatchScript.EvaluateAsync(
                _services.Database,
                new
                {
                    stepIndexKey = (RedisKey)stepIndexKey,
                    workerId = (RedisValue)workerId,
                    nowUnix = (RedisValue)nowUnix,
                    stepKeyPrefix = (RedisValue)stepKeyPrefix,
                    executionId = (RedisValue)executionId,
                    maxSteps = (RedisValue)maxSteps,
                    claimTokensJson = (RedisValue)claimTokensJson
                });

            if (result.IsNull)
            {
                return Array.Empty<AiClaimedStep>();
            }

            var json = (string)result!;

            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<AiClaimedStep>();
            }

            List<AiClaimedStep>? claimed = JsonSerializer.Deserialize<List<AiClaimedStep>>(
                json,
                _services.JsonOptions);

            return claimed is not null
                ? claimed
                : Array.Empty<AiClaimedStep>();
        }

        /// <summary>
        /// Executes the prepared specific-step claim Lua script.
        /// </summary>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="stepName">The specific step name to claim.</param>
        /// <param name="workerId">The worker identifier requesting the claim.</param>
        /// <param name="claimToken">The generated claim token.</param>
        /// <returns>
        /// <c>true</c> when the specific step was claimed; otherwise <c>false</c>.
        /// </returns>
        private async Task<bool> ExecuteTryClaimStepScriptAsync(
            string executionId,
            string stepName,
            string workerId,
            string claimToken)
        {
            var stepKey = _services.KeyBuilder.GetDagStepKey(executionId, stepName);
            var stepKeyPrefix = _services.KeyBuilder.GetDagStepKeyPrefix(executionId);
            var nowUnix = RedisDagStoreHelper.NowMs();

            var result = await _claimSpecificScript.EvaluateAsync(
                _services.Database,
                new
                {
                    stepKey = (RedisKey)stepKey,
                    stepKeyPrefix = (RedisValue)stepKeyPrefix,
                    workerId = (RedisValue)workerId,
                    nowUnix = (RedisValue)nowUnix,
                    claimToken = (RedisValue)claimToken
                });

            return (int)result! == 1;
        }
    }
}