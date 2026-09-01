using Multiplexed.AI.Stores.Cache.Redis.Helpers;
using Multiplexed.AI.Stores.Cache.Redis.Lua;
using StackExchange.Redis;

namespace Multiplexed.AI.Stores.Cache.Redis.Dag
{
    /// <summary>
    /// Handles Redis DAG step recovery operations.
    /// </summary>
    public sealed class RedisDagStoreRecoveryService
    {
        private readonly IRedisDagStoreServices _services;

        private LuaScript _recoverTimedOutScript;

        private LuaScript _recoverRunningForRecoveryScript;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="RedisDagStoreRecoveryService"/> class.
        /// </summary>
        /// <param name="services">The shared Redis DAG store services.</param>
        public RedisDagStoreRecoveryService(
            IRedisDagStoreServices services)
        {
            ArgumentNullException.ThrowIfNull(services);

            _services =
                services;

            _recoverTimedOutScript =
                RedisDagLuaScripts.RecoverPreparedScript;

            _recoverRunningForRecoveryScript =
                RedisDagLuaScripts
                    .RecoverRunningForRecoveryPreparedScript;
        }

        /// <summary>
        /// Recovers timed-out running steps.
        /// </summary>
        /// <remarks>
        /// Only running steps whose persisted lease has expired are recovered.
        /// Infrastructure recovery increments <c>RecoveryCount</c> without
        /// consuming business retry attempts.
        /// </remarks>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The number of recovered steps.</returns>
        public async Task<int> RecoverTimedOutStepsAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ValidateExecutionId(
                executionId);

            cancellationToken.ThrowIfCancellationRequested();

            var nowUnix =
                RedisDagStoreHelper.NowMs();

            var stepIndexKey =
                _services.KeyBuilder
                    .GetDagStepIdsKey(
                        executionId);

            var stepKeyPrefix =
                _services.KeyBuilder
                    .GetDagStepKeyPrefix(
                        executionId);

            try
            {
                var recovered =
                    await ExecuteRecoverTimedOutAsync(
                            stepIndexKey,
                            stepKeyPrefix,
                            nowUnix,
                            cancellationToken)
                        .ConfigureAwait(false);

                RecordTimedOutRecovery(
                    executionId,
                    recovered,
                    reloadedAfterNoScript: false);

                return recovered;
            }
            catch (RedisServerException exception)
                when (IsNoScriptException(exception))
            {
                _recoverTimedOutScript =
                    RedisDagLuaScripts.RecoverPreparedScript;

                var recovered =
                    await ExecuteRecoverTimedOutAsync(
                            stepIndexKey,
                            stepKeyPrefix,
                            nowUnix,
                            cancellationToken)
                        .ConfigureAwait(false);

                RecordTimedOutRecovery(
                    executionId,
                    recovered,
                    reloadedAfterNoScript: true);

                return recovered;
            }
        }

        /// <summary>
        /// Recovers all currently running steps for an explicit runtime
        /// execution recovery transition.
        /// </summary>
        /// <remarks>
        /// This operation does not wait for claim leases to expire. It must be
        /// called only after durable recovery pause ownership has been acquired
        /// for the execution.
        /// </remarks>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The number of running steps recovered.</returns>
        public async Task<int> RecoverRunningStepsForRecoveryAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ValidateExecutionId(
                executionId);

            cancellationToken.ThrowIfCancellationRequested();

            var nowUnix =
                RedisDagStoreHelper.NowMs();

            var stepIndexKey =
                _services.KeyBuilder
                    .GetDagStepIdsKey(
                        executionId);

            var stepKeyPrefix =
                _services.KeyBuilder
                    .GetDagStepKeyPrefix(
                        executionId);

            try
            {
                var recovered =
                    await ExecuteRecoverRunningForRecoveryAsync(
                            stepIndexKey,
                            stepKeyPrefix,
                            nowUnix,
                            cancellationToken)
                        .ConfigureAwait(false);

                RecordExplicitRecovery(
                    executionId,
                    recovered,
                    reloadedAfterNoScript: false);

                return recovered;
            }
            catch (RedisServerException exception)
                when (IsNoScriptException(exception))
            {
                _recoverRunningForRecoveryScript =
                    RedisDagLuaScripts
                        .RecoverRunningForRecoveryPreparedScript;

                var recovered =
                    await ExecuteRecoverRunningForRecoveryAsync(
                            stepIndexKey,
                            stepKeyPrefix,
                            nowUnix,
                            cancellationToken)
                        .ConfigureAwait(false);

                RecordExplicitRecovery(
                    executionId,
                    recovered,
                    reloadedAfterNoScript: true);

                return recovered;
            }
        }

        private async Task<int> ExecuteRecoverTimedOutAsync(
            string stepIndexKey,
            string stepKeyPrefix,
            long nowUnix,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result =
                await _recoverTimedOutScript
                    .EvaluateAsync(
                        _services.Database,
                        new
                        {
                            stepIndexKey =
                                (RedisKey)stepIndexKey,

                            stepKeyPrefix =
                                (RedisValue)stepKeyPrefix,

                            nowUnix =
                                (RedisValue)nowUnix
                        })
                    .ConfigureAwait(false);
            Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics.RecordInvocation(
                _services.Database,
                Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.LuaDag);

            return (int)result!;
        }

        private async Task<int> ExecuteRecoverRunningForRecoveryAsync(
            string stepIndexKey,
            string stepKeyPrefix,
            long nowUnix,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result =
                await _recoverRunningForRecoveryScript
                    .EvaluateAsync(
                        _services.Database,
                        new
                        {
                            stepIndexKey =
                                (RedisKey)stepIndexKey,

                            stepKeyPrefix =
                                (RedisValue)stepKeyPrefix,

                            nowUnix =
                                (RedisValue)nowUnix
                        })
                    .ConfigureAwait(false);
            Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics.RecordInvocation(
                _services.Database,
                Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.LuaDag);

            return (int)result!;
        }

        private void RecordTimedOutRecovery(
            string executionId,
            int recovered,
            bool reloadedAfterNoScript)
        {
            if (recovered <= 0)
            {
                return;
            }

            _services.Metrics.Execution
                .RecordStepsRecovered(
                    executionId,
                    recovered);

            _services.Logger.Engine.LogInformation(
                reloadedAfterNoScript
                    ? $"[AI DAG STORE] Timed-out steps recovered after NOSCRIPT retry. ExecutionId='{executionId}', RecoveredCount='{recovered}'."
                    : $"[AI DAG STORE] Timed-out steps recovered. ExecutionId='{executionId}', RecoveredCount='{recovered}'.");
        }

        private void RecordExplicitRecovery(
            string executionId,
            int recovered,
            bool reloadedAfterNoScript)
        {
            if (recovered <= 0)
            {
                return;
            }

            _services.Metrics.Execution
                .RecordStepsRecovered(
                    executionId,
                    recovered);

            _services.Logger.Engine.LogWarning(
                 reloadedAfterNoScript
                     ? $"[AI DAG STORE] Running steps recovered explicitly after NOSCRIPT retry. ExecutionId='{executionId}', RecoveredCount='{recovered}'."
                     : $"[AI DAG STORE] Running steps recovered explicitly for runtime recovery. ExecutionId='{executionId}', RecoveredCount='{recovered}'.");
        
        }

        private static bool IsNoScriptException(
            RedisServerException exception)
        {
            return exception.Message.Contains(
                "NOSCRIPT",
                StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateExecutionId(
            string executionId)
        {
            if (string.IsNullOrWhiteSpace(executionId))
            {
                throw new ArgumentException(
                    "Execution id cannot be null, empty, or whitespace.",
                    nameof(executionId));
            }
        }
    }
}