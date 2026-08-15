using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Execution.Engine.Models;
using Multiplexed.AI.Runtime.Execution.Retention.Models;
using Multiplexed.AI.Stores.Cache.Redis.Helpers;
using Multiplexed.AI.Stores.Cache.Redis.Lua;
using Multiplexed.AI.Stores.Cache.Redis.Serialization;
using StackExchange.Redis;
using System.Text.Json;

namespace Multiplexed.AI.Stores.Cache.Redis.Dag
{
    /// <summary>
    /// Handles Redis DAG step transition operations such as completion, parking, failure, and execution finalization.
    /// </summary>
    /// <remarks>
    /// This service owns transition-related Redis Lua execution paths.
    /// It does not perform step claiming, timeout recovery, or state persistence.
    /// </remarks>
    public sealed class RedisDagStoreTransitionService
    {
        private readonly IRedisDagStoreServices _services;
        private LoadedLuaScript _completeLoadedScript;
        private LoadedLuaScript _parkLoadedScript;
        private LoadedLuaScript _failLoadedScript;
        private LoadedLuaScript _finalizeLoadedScript;
        private LoadedLuaScript _retentionPatchLoadedScript;

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisDagStoreTransitionService"/> class.
        /// </summary>
        /// <param name="services">The shared Redis DAG store services.</param>
        public RedisDagStoreTransitionService(IRedisDagStoreServices services)
        {
            ArgumentNullException.ThrowIfNull(services);

            _services = services;

            _completeLoadedScript = _services.Helper.LoadScript(RedisDagLuaScripts.CompletePreparedScript);
            _parkLoadedScript = _services.Helper.LoadScript(RedisDagLuaScripts.ParkPreparedScript);
            _failLoadedScript = _services.Helper.LoadScript(RedisDagLuaScripts.FailPreparedScript);
            _finalizeLoadedScript = _services.Helper.LoadScript(RedisDagLuaScripts.FinalizeScript);
            _retentionPatchLoadedScript = _services.Helper.LoadScript(RedisDagLuaScripts.RetentionPatchPreparedScript);
        }

        /// <summary>
        /// Completes a claimed step atomically.
        ///
        /// Completion is accepted only when:
        /// - the step exists
        /// - the step is Running
        /// - the claim token matches the current owner
        ///
        /// IMPORTANT:
        /// - nowUnix is expressed in milliseconds
        /// - the running lease is cleared atomically with completion
        /// </summary>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <param name="stepName">The step name to complete.</param>
        /// <param name="claimToken">The claim token that owns the running step.</param>
        /// <param name="result">The step result to persist.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><c>true</c> when the step was completed; otherwise <c>false</c>.</returns>
        public async Task<bool> TryCompleteStepAsync(
            string executionId,
            string stepName,
            string claimToken,
            AiStepResult result,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));

            if (string.IsNullOrWhiteSpace(stepName))
                throw new ArgumentException("Step name cannot be null or empty.", nameof(stepName));

            if (string.IsNullOrWhiteSpace(claimToken))
                throw new ArgumentException("Claim token cannot be null or empty.", nameof(claimToken));

            ArgumentNullException.ThrowIfNull(result);

            var stepKey = _services.KeyBuilder.GetDagStepKey(executionId, stepName);
            var nowUnix = RedisDagStoreHelper.NowMs();
            var resultJson = JsonSerializer.Serialize(result, _services.JsonOptions);
            var inlinePayloadSizeBytes = System.Text.Encoding.UTF8.GetByteCount(resultJson);

            try
            {
                return await ExecuteCompleteAsync(
                        stepKey,
                        claimToken,
                        nowUnix,
                        resultJson,
                        inlinePayloadSizeBytes)
                    .ConfigureAwait(false);
            }
            catch (RedisServerException ex) when (ex.Message.Contains("NOSCRIPT", StringComparison.OrdinalIgnoreCase))
            {
                _completeLoadedScript = _services.Helper.LoadScript(RedisDagLuaScripts.CompletePreparedScript);

                return await ExecuteCompleteAsync(
                        stepKey,
                        claimToken,
                        nowUnix,
                        resultJson,
                        inlinePayloadSizeBytes)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Parks a claimed step atomically while preserving normal retry and recovery accounting.
        /// </summary>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <param name="stepName">The step name to park.</param>
        /// <param name="claimToken">The claim token that owns the running step.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><c>true</c> when the park transition was accepted; otherwise <c>false</c>.</returns>
        public async Task<bool> TryParkStepAsync(
            string executionId,
            string stepName,
            string claimToken,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
            ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);

            cancellationToken.ThrowIfCancellationRequested();

            var stepKey = _services.KeyBuilder.GetDagStepKey(executionId, stepName);
            var nowUnix = RedisDagStoreHelper.NowMs();

            try
            {
                return await ExecuteParkAsync(stepKey, claimToken, nowUnix).ConfigureAwait(false);
            }
            catch (RedisServerException ex) when (ex.Message.Contains("NOSCRIPT", StringComparison.OrdinalIgnoreCase))
            {
                _parkLoadedScript = _services.Helper.LoadScript(RedisDagLuaScripts.ParkPreparedScript);
                return await ExecuteParkAsync(stepKey, claimToken, nowUnix).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Fails a claimed step atomically.
        ///
        /// Failure is accepted only when:
        /// - the step exists
        /// - the step is Running
        /// - the claim token matches the current owner
        ///
        /// IMPORTANT:
        /// This method is retry-aware. A successful failure mutation may result in:
        /// - WaitingForRetry
        /// - Failed
        /// depending on RetryState.RetryCount / Retry.MaxRetries.
        ///
        /// - nowUnix is expressed in milliseconds
        /// - the running lease is cleared atomically with failure handling
        /// </summary>
        /// <param name="executionId">The unique execution identifier.</param>
        /// <param name="stepName">The step name to fail.</param>
        /// <param name="claimToken">The claim token that owns the running step.</param>
        /// <param name="error">The failure message to persist.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><c>true</c> when the failure transition was accepted; otherwise <c>false</c>.</returns>
        public async Task<bool> TryFailStepAsync(
            string executionId,
            string stepName,
            string claimToken,
            string? error,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));

            if (string.IsNullOrWhiteSpace(stepName))
                throw new ArgumentException("Step name cannot be null or empty.", nameof(stepName));

            if (string.IsNullOrWhiteSpace(claimToken))
                throw new ArgumentException("Claim token cannot be null or empty.", nameof(claimToken));

            var stepKey = _services.KeyBuilder.GetDagStepKey(executionId, stepName);
            var nowUnix = RedisDagStoreHelper.NowMs();

            try
            {
                return await ExecuteFailAsync(
                    stepKey,
                    claimToken,
                    nowUnix,
                    error ?? string.Empty);
            }
            catch (RedisServerException ex) when (ex.Message.Contains("NOSCRIPT", StringComparison.OrdinalIgnoreCase))
            {
                _failLoadedScript = _services.Helper.LoadScript(RedisDagLuaScripts.FailPreparedScript);

                return await ExecuteFailAsync(
                    stepKey,
                    claimToken,
                    nowUnix,
                    error ?? string.Empty);
            }
        }

        /// <summary>
        /// Attempts to atomically finalize the global execution record using Redis Lua.
        ///
        /// BEHAVIOR:
        /// - validates optimistic execution key
        /// - refuses to finalize if the record is already terminal
        /// - persists terminal status and completion metadata atomically
        ///
        /// IMPORTANT:
        /// This method is terminal-only. Non-terminal statuses must never be passed here.
        /// </summary>
        /// <param name="request">The execution finalization request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><c>true</c> when finalization succeeded; otherwise <c>false</c>.</returns>
        public async Task<bool> TryFinalizeExecutionAsync(
            AiDagExecutionFinalizationRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (string.IsNullOrWhiteSpace(request.ExecutionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(request));

            if (string.IsNullOrWhiteSpace(request.ExpectedExecutionStepKey))
                throw new ArgumentException("Expected execution step key cannot be null or empty.", nameof(request));

            if (request.Status is not AiExecutionStatus.Completed
                and not AiExecutionStatus.Failed
                and not AiExecutionStatus.Cancelled)
            {
                throw new ArgumentException(
                    "Only terminal execution statuses are allowed for finalization.",
                    nameof(request));
            }

            var key = _services.KeyBuilder.GetExecutionRecordKey(request.ExecutionId);

            var completedAtUtc = request.CompletedAtUtc == default
                ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                : new DateTimeOffset(request.CompletedAtUtc).ToUnixTimeMilliseconds();

            var completedStepsJson = JsonSerializer.Serialize(
                request.CompletedSteps ?? Array.Empty<string>(),
                _services.JsonOptions);

            _services.Metrics.Execution.RecordFinalizeAttempt(request.ExecutionId);

            _services.Logger.Engine.LogInformation(
                $"[AI DAG STORE] Finalization attempt. ExecutionId='{request.ExecutionId}', Status='{request.Status}', WorkerId='{request.WorkerId}'.");

            try
            {
                var result = await _finalizeLoadedScript.EvaluateAsync(
                    _services.Database,
                    new
                    {
                        recordKey = (RedisKey)key,
                        expectedExecutionStepKey = (RedisValue)request.ExpectedExecutionStepKey,
                        status = (RedisValue)request.Status.ToString(),
                        completedAtUtc = (RedisValue)completedAtUtc,
                        completedStepsJson = (RedisValue)completedStepsJson,
                        newExecutionStepKey = (RedisValue)Guid.NewGuid().ToString("N")
                    });

                var success = (int)result! == 1;

                if (success)
                {
                    _services.Metrics.Execution.RecordFinalizeSuccess(request.ExecutionId);

                    _services.Logger.Engine.LogInformation(
                        $"[AI DAG STORE] Finalization succeeded. ExecutionId='{request.ExecutionId}', Status='{request.Status}', WorkerId='{request.WorkerId}'.");
                }
                else
                {
                    _services.Metrics.Execution.RecordFinalizeConflict(request.ExecutionId);

                    _services.Logger.Engine.LogInformation(
                        $"[AI DAG STORE] Finalization skipped or race lost. ExecutionId='{request.ExecutionId}', Status='{request.Status}', WorkerId='{request.WorkerId}'.");
                }

                return success;
            }
            catch (RedisServerException ex) when (ex.Message.Contains("NOSCRIPT", StringComparison.OrdinalIgnoreCase))
            {
                _finalizeLoadedScript = _services.Helper.LoadScript(RedisDagLuaScripts.FinalizeScript);

                var result = await _finalizeLoadedScript.EvaluateAsync(
                    _services.Database,
                    new
                    {
                        recordKey = (RedisKey)key,
                        expectedExecutionStepKey = (RedisValue)request.ExpectedExecutionStepKey,
                        status = (RedisValue)request.Status.ToString(),
                        completedAtUtc = (RedisValue)completedAtUtc,
                        completedStepsJson = (RedisValue)completedStepsJson,
                        newExecutionStepKey = (RedisValue)Guid.NewGuid().ToString("N")
                    });

                var success = (int)result! == 1;

                if (success)
                {
                    _services.Metrics.Execution.RecordFinalizeSuccess(request.ExecutionId);

                    _services.Logger.Engine.LogInformation(
                        $"[AI DAG STORE] Finalization succeeded after NOSCRIPT reload. ExecutionId='{request.ExecutionId}', Status='{request.Status}', WorkerId='{request.WorkerId}'.");
                }
                else
                {
                    _services.Metrics.Execution.RecordFinalizeConflict(request.ExecutionId);

                    _services.Logger.Engine.LogInformation(
                        $"[AI DAG STORE] Finalization skipped or race lost after NOSCRIPT reload. ExecutionId='{request.ExecutionId}', Status='{request.Status}', WorkerId='{request.WorkerId}'.");
                }

                return success;
            }
        }

        /// <summary>
        /// Applies atomic retention patches to hot DAG step state.
        /// </summary>
        /// <param name="executionId">
        /// The execution identifier.
        /// </param>
        /// <param name="candidates">
        /// The retention patch candidates to apply.
        /// </param>
        /// <param name="cancellationToken">
        /// A token used to cancel the operation.
        /// </param>
        /// <returns>
        /// The result of the retention patch operation.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method is safe for active distributed execution because it does not save the
        /// full execution state. Each candidate is verified and patched atomically by Redis Lua.
        /// </para>
        ///
        /// <para>
        /// Steps that changed after the retention decision was made are skipped rather than
        /// overwritten.
        /// </para>
        /// </remarks>
        public async Task<AiRetentionPatchResult> TryApplyRetentionPatchAsync(
            string executionId,
            IReadOnlyCollection<AiRetentionPatchCandidate> candidates,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
            {
                throw new ArgumentException(
                    "Execution id cannot be null or empty.",
                    nameof(executionId));
            }

            ArgumentNullException.ThrowIfNull(candidates);

            if (candidates.Count == 0)
            {
                return new AiRetentionPatchResult();
            }

            cancellationToken.ThrowIfCancellationRequested();

            var stepKeyPrefix = _services.KeyBuilder.GetDagStepKeyPrefix(executionId);
            var stepIdsKey = _services.KeyBuilder.GetDagStepIdsKey(executionId);
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var candidatesJson = JsonSerializer.Serialize(
                candidates,
                _services.JsonOptions);

            try
            {
                return await ExecuteRetentionPatchAsync(
                        stepKeyPrefix,
                        stepIdsKey,
                        nowUnix,
                        candidatesJson)
                    .ConfigureAwait(false);
            }
            catch (RedisServerException exception)
                when (exception.Message.Contains("NOSCRIPT", StringComparison.OrdinalIgnoreCase))
            {
                _retentionPatchLoadedScript = _services.Helper.LoadScript(
                    RedisDagLuaScripts.RetentionPatchPreparedScript);

                return await ExecuteRetentionPatchAsync(
                        stepKeyPrefix,
                        stepIdsKey,
                        nowUnix,
                        candidatesJson)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Executes the prepared step completion Lua script.
        /// </summary>
        /// <param name="stepKey">The Redis key of the step to complete.</param>
        /// <param name="claimToken">The claim token that owns the running step.</param>
        /// <param name="nowUnix">The current UTC timestamp expressed in Unix milliseconds.</param>
        /// <param name="resultJson">The serialized step result.</param>
        /// <param name="inlinePayloadSizeBytes">The estimated serialized inline payload size in bytes.</param>
        /// <returns><c>true</c> when the completion mutation succeeded; otherwise <c>false</c>.</returns>
        private async Task<bool> ExecuteCompleteAsync(
           string stepKey,
           string claimToken,
           long nowUnix,
           string resultJson,
           long inlinePayloadSizeBytes)
        {
            var result = await _completeLoadedScript.EvaluateAsync(
                _services.Database,
                new
                {
                    stepKey = (RedisKey)stepKey,
                    claimToken = (RedisValue)claimToken,
                    nowUnix = (RedisValue)nowUnix,
                    resultJson = (RedisValue)resultJson,
                    inlinePayloadSizeBytes = (RedisValue)inlinePayloadSizeBytes
                });

            return (int)result! == 1;
        }

        /// <summary>
        /// Executes the prepared step park Lua script.
        /// </summary>
        /// <param name="stepKey">The Redis key of the step to park.</param>
        /// <param name="claimToken">The claim token that owns the running step.</param>
        /// <param name="nowUnix">The current UTC timestamp expressed in Unix milliseconds.</param>
        /// <returns><c>true</c> when the park mutation succeeded; otherwise <c>false</c>.</returns>
        private async Task<bool> ExecuteParkAsync(
            string stepKey,
            string claimToken,
            long nowUnix)
        {
            var result = await _parkLoadedScript.EvaluateAsync(
                _services.Database,
                new
                {
                    stepKey = (RedisKey)stepKey,
                    claimToken = (RedisValue)claimToken,
                    nowUnix = (RedisValue)nowUnix
                });

            return (int)result! == 1;
        }

        /// <summary>
        /// Executes the prepared step failure Lua script.
        /// </summary>
        /// <param name="stepKey">The Redis key of the step to fail.</param>
        /// <param name="claimToken">The claim token that owns the running step.</param>
        /// <param name="nowUnix">The current UTC timestamp expressed in Unix milliseconds.</param>
        /// <param name="error">The failure message.</param>
        /// <returns><c>true</c> when the failure mutation succeeded; otherwise <c>false</c>.</returns>
        private async Task<bool> ExecuteFailAsync(
            string stepKey,
            string claimToken,
            long nowUnix,
            string error)
        {
            var result = await _failLoadedScript.EvaluateAsync(
                _services.Database,
                new
                {
                    stepKey = (RedisKey)stepKey,
                    claimToken = (RedisValue)claimToken,
                    nowUnix = (RedisValue)nowUnix,
                    error = (RedisValue)error
                });

            return (int)result! == 1;
        }


        /// <summary>
        /// Executes the prepared retention patch Lua script.
        /// </summary>
        /// <param name="stepKeyPrefix">
        /// The Redis key prefix for DAG step keys.
        /// </param>
        /// <param name="stepIdsKey">
        /// The Redis key containing indexed DAG step ids.
        /// </param>
        /// <param name="nowUnix">
        /// The current UTC timestamp expressed in Unix milliseconds.
        /// </param>
        /// <param name="candidatesJson">
        /// The serialized retention patch candidates.
        /// </param>
        /// <returns>
        /// The retention patch result returned by Redis.
        /// </returns>
        private async Task<AiRetentionPatchResult> ExecuteRetentionPatchAsync(
            string stepKeyPrefix,
            string stepIdsKey,
            long nowUnix,
            string candidatesJson)
        {
            var result = await _retentionPatchLoadedScript.EvaluateAsync(
                    _services.Database,
                    new
                    {
                        stepKeyPrefix = (RedisKey)stepKeyPrefix,
                        stepIdsKey = (RedisKey)stepIdsKey,
                        nowUnix = (RedisValue)nowUnix,
                        candidatesJson = (RedisValue)candidatesJson
                    })
                .ConfigureAwait(false);

            if (result.IsNull)
            {
                return new AiRetentionPatchResult();
            }

            return JsonSerializationHelpers.DeserializeRetentionPatchResult(result.ToString());
        }

    }
}