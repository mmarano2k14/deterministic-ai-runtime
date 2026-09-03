using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Cleanup;
using StackExchange.Redis;

namespace Multiplexed.AI.Runtime.Execution.Cleanup
{
    /// <summary>
    /// Deletes distributed DAG step persistence and claim-related artifacts from Redis.
    /// </summary>
    public sealed class AiDagDistributedStateCleanup : IAiDagDistributedStateCleanup
    {
        private readonly IDatabase _redis;
        private readonly IAiExecutionKeyBuilder _keyBuilder;
        private readonly ILogger<AiDagDistributedStateCleanup> _logger;

        public AiDagDistributedStateCleanup(
            IConnectionMultiplexer connectionMultiplexer,
            IAiExecutionKeyBuilder keyBuilder,
            ILogger<AiDagDistributedStateCleanup> logger)
        {
            ArgumentNullException.ThrowIfNull(connectionMultiplexer);
            _keyBuilder = keyBuilder ?? throw new ArgumentNullException(nameof(keyBuilder));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _redis = connectionMultiplexer.GetDatabase();
        }

        public async Task<AiDagDistributedCleanupResult> DeleteDistributedStateAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
            {
                throw new ArgumentException("ExecutionId cannot be null or whitespace.", nameof(executionId));
            }

            var warnings = new List<string>();
            var errors = new List<string>();

            int stepCountDiscovered = 0;
            int stepCountDeleted = 0;
            bool claimsFound = false;
            bool claimsDeleted = true;
            bool inFlightFound = false;
            bool inFlightDeleted = true;

            try
            {
                var stepIdsKey = _keyBuilder.GetDagStepIdsKey(executionId);
                var stepIds = await _redis.SetMembersAsync(stepIdsKey);
                Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics.Record(
                    _redis,
                    Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.DagDistributedCleanupStepIndexLoad,
                    "SMEMBERS",
                    stepIds);

                stepCountDiscovered = stepIds.Length;

                var batch = _redis.CreateBatch();
                var deleteTasks = new List<Task<bool>>();

                foreach (var stepIdValue in stepIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var stepId = stepIdValue.ToString();
                    if (string.IsNullOrWhiteSpace(stepId))
                    {
                        continue;
                    }

                    var stepKey = _keyBuilder.GetDagStepKey(executionId, stepId);
                    var claimKey = _keyBuilder.GetDagClaimKey(executionId, stepId);
                    var leaseKey = _keyBuilder.GetDagLeaseKey(executionId, stepId);

                    deleteTasks.Add(batch.KeyDeleteAsync(stepKey));
                    deleteTasks.Add(batch.KeyDeleteAsync(claimKey));
                    deleteTasks.Add(batch.KeyDeleteAsync(leaseKey));
                }

                deleteTasks.Add(batch.KeyDeleteAsync(stepIdsKey));
                deleteTasks.Add(batch.KeyDeleteAsync(_keyBuilder.GetDagMetaKey(executionId)));

                var inFlightKey = _keyBuilder.GetDagInFlightKey(executionId);
                inFlightFound = await _redis.KeyExistsAsync(inFlightKey);
                deleteTasks.Add(batch.KeyDeleteAsync(inFlightKey));

                batch.Execute();
                var deleted = await Task.WhenAll(deleteTasks);

                stepCountDeleted = stepCountDiscovered;

                claimsFound = stepCountDiscovered > 0;
                claimsDeleted = true;
                inFlightDeleted = !inFlightFound || deleted.Any();
            }
            catch (Exception ex)
            {
                errors.Add($"Redis distributed DAG cleanup failed: {ex.Message}");

                _logger.LogError(
                    ex,
                    "AiDagDistributedCleanupFailed ExecutionId={ExecutionId}",
                    executionId);
            }

            return new AiDagDistributedCleanupResult
            {
                StepCountDiscovered = stepCountDiscovered,
                StepCountDeleted = stepCountDeleted,
                ClaimsFound = claimsFound,
                ClaimsDeleted = claimsDeleted,
                InFlightFound = inFlightFound,
                InFlightDeleted = inFlightDeleted,
                Warnings = warnings,
                Errors = errors
            };
        }
    }
}