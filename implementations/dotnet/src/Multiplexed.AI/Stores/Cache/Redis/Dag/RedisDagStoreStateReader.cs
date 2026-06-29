using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.AI.Stores.Cache.Redis.Helpers;
using Multiplexed.AI.Stores.Cache.Redis.Serialization;
using StackExchange.Redis;
using System.Text.Json;

namespace Multiplexed.AI.Stores.Cache.Redis.Dag
{
    /// <summary>
    /// Handles Redis DAG execution state read operations.
    /// </summary>
    public sealed class RedisDagStoreStateReader
    {
        private readonly IRedisDagStoreServices _services;

        public RedisDagStoreStateReader(IRedisDagStoreServices services)
        {
            ArgumentNullException.ThrowIfNull(services);

            _services = services;
        }

        /// <summary>
        /// Reconstructs execution state by loading the persisted state blob
        /// and then overlaying all indexed step keys.
        ///
        /// IMPORTANT:
        /// - In distributed DAG mode, step keys + step index are the authoritative state
        ///   for step lifecycle
        /// - The state blob preserves global bags such as Data and Metadata
        /// - This method combines both representations
        ///
        /// RETURN SEMANTICS:
        /// - Returns <c>null</c> when no state blob and no distributed DAG state exist
        /// - Returns a populated <see cref="AiExecutionState"/> when either the blob
        ///   or at least one step payload exists
        /// </summary>
        public async Task<AiExecutionState?> GetStateAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));

            var stateKey = _services.Helper.GetStateBlobKey(executionId);
            var stepIndexKey = _services.KeyBuilder.GetDagStepIdsKey(executionId);

            var record = await GetRecordAsync(
                executionId,
                cancellationToken);

            var completedStepNames = record?.CompletedSteps is not null
                ? record.CompletedSteps.ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            AiExecutionState? state = null;

            var stateBlob = await _services.Database.StringGetAsync(stateKey);
            if (stateBlob.HasValue)
            {
                state = JsonSerializer.Deserialize<AiExecutionState>(
                    (string)stateBlob!,
                    _services.JsonOptions);
            }

            var stepNames = await _services.Database.SetMembersAsync(stepIndexKey);

            if (stepNames.Length == 0)
            {
                if (state is not null)
                {
                    state.ExecutionId = executionId;

                    RedisDagStoreHelper.RemoveStaleCompletedNoneSteps(
                        state,
                        completedStepNames);
                }

                return state;
            }

            state ??= new AiExecutionState
            {
                ExecutionId = executionId
            };

            state.ExecutionId = executionId;

            foreach (var stepNameValue in stepNames)
            {
                var stepName = (string?)stepNameValue;

                if (string.IsNullOrWhiteSpace(stepName))
                    continue;

                var stepKey = _services.KeyBuilder.GetDagStepKey(executionId, stepName);
                var raw = await _services.Database.StringGetAsync(stepKey);

                if (!raw.HasValue)
                    continue;

                var repairedJson = JsonSerializationHelpers.RepairStepJson((string)raw!);
                repairedJson = JsonSerializationHelpers.RepairRetryJson(repairedJson);

                var step = JsonSerializer.Deserialize<AiStepState>(
                    repairedJson,
                    _services.JsonOptions);

                if (step is not null)
                {
                    step.DependsOn ??= new List<string>();

                    if (state.Steps.TryGetValue(step.StepName, out var blobStep) &&
                        RedisDagStoreHelper.IsTerminal(blobStep.Status) &&
                        RedisDagStoreHelper.IsNonTerminal(step.Status))
                    {
                        state.Steps[step.StepName] = blobStep;
                        continue;
                    }

                    state.Steps[step.StepName] = step;
                }
            }

            RedisDagStoreHelper.RemoveStaleCompletedNoneSteps(
                state,
                completedStepNames);

            if (state.Steps.Count == 0)
            {
                return stateBlob.HasValue ? state : null;
            }

            var beforeNormalizeMissingResults = CountInvalidCompletedMissingResults(state);
            var beforeNormalizeCompacted = CountCompacted(state);
            var beforeNormalizeEvicted = CountEvicted(state);

            // CRITICAL: normalize AFTER full state reconstruction
            _services.StepResultNormalizerPipeline.Normalize(state);

            var afterNormalizeMissingResults = CountInvalidCompletedMissingResults(state);
            var afterNormalizeCompacted = CountCompacted(state);
            var afterNormalizeEvicted = CountEvicted(state);

            if (beforeNormalizeMissingResults != afterNormalizeMissingResults ||
                beforeNormalizeCompacted != afterNormalizeCompacted ||
                beforeNormalizeEvicted != afterNormalizeEvicted)
            {
                _services.Logger.Engine.LogWarning(
                    $"[AI DAG STORE] Step result normalization diagnostics. " +
                    $"ExecutionId='{executionId}', " +
                    $"BeforeMissingResults='{beforeNormalizeMissingResults}', " +
                    $"AfterMissingResults='{afterNormalizeMissingResults}', " +
                    $"BeforeCompacted='{beforeNormalizeCompacted}', " +
                    $"AfterCompacted='{afterNormalizeCompacted}', " +
                    $"BeforeEvicted='{beforeNormalizeEvicted}', " +
                    $"AfterEvicted='{afterNormalizeEvicted}'.");
            }

            return state;
        }

        /// <summary>
        /// Retrieves the execution record.
        /// </summary>
        public async Task<AiExecutionRecord?> GetRecordAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));

            var value = await _services.Database.StringGetAsync(_services.KeyBuilder.GetExecutionRecordKey(executionId));

            if (!value.HasValue)
                return null;

            var repairedJson = JsonSerializationHelpers.RepairRecordJson((string)value!);
            return JsonSerializer.Deserialize<AiExecutionRecord>(repairedJson, _services.JsonOptions);
        }

        /// <summary>
        /// Counts completed steps that have no result and no durable retention marker explaining it.
        /// </summary>
        /// <param name="state">The execution state.</param>
        /// <returns>The invalid completed missing result count.</returns>
        private static int CountInvalidCompletedMissingResults(
            AiExecutionState state)
        {
            return state.Steps.Values.Count(step =>
                step.Status == AiStepExecutionStatus.Completed &&
                step.Result is null &&
                !step.IsEvictedFromHotState &&
                !step.IsCompacted);
        }

        /// <summary>
        /// Counts compacted steps.
        /// </summary>
        /// <param name="state">The execution state.</param>
        /// <returns>The compacted step count.</returns>
        private static int CountCompacted(
            AiExecutionState state)
        {
            return state.Steps.Values.Count(step => step.IsCompacted);
        }

        /// <summary>
        /// Counts steps evicted from hot state.
        /// </summary>
        /// <param name="state">The execution state.</param>
        /// <returns>The evicted step count.</returns>
        private static int CountEvicted(
            AiExecutionState state)
        {
            return state.Steps.Values.Count(step => step.IsEvictedFromHotState);
        }
    }
}