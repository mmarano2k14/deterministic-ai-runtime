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

            var (record, stateBlob) = await LoadRecordAndStateBlobAsync(
                    executionId,
                    stateKey,
                    cancellationToken)
                .ConfigureAwait(false);

            var completedStepNames = record?.CompletedSteps is not null
                ? record.CompletedSteps.ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            AiExecutionState? state = null;

            if (stateBlob.HasValue)
            {
                state = JsonSerializer.Deserialize<AiExecutionState>(
                    (string)stateBlob!,
                    _services.JsonOptions);
            }

            var stepNames = await _services.Database.SetMembersAsync(stepIndexKey);
            Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics.Record(
                _services.Database,
                Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.DagStepIndexLoad,
                "SMEMBERS",
                stepNames);

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

            var indexedStepNames = stepNames
                .Select(stepNameValue => (string?)stepNameValue)
                .Where(stepName => !string.IsNullOrWhiteSpace(stepName))
                .Select(stepName => stepName!)
                .ToArray();

            if (indexedStepNames.Length > 0)
            {
                var stepKeys = new RedisKey[indexedStepNames.Length];

                for (var index = 0; index < indexedStepNames.Length; index++)
                {
                    stepKeys[index] = _services.KeyBuilder.GetDagStepKey(
                        executionId,
                        indexedStepNames[index]);
                }

                var rawSteps = await LoadIndexedStepValuesAsync(stepKeys);

                for (var index = 0; index < indexedStepNames.Length; index++)
                {
                    var raw = rawSteps[index];

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
        /// Loads the execution record and state blob with one multi-key read on non-clustered Redis.
        /// Redis Cluster preserves the existing one-key-per-command path because the current keys
        /// do not share a hash tag and may belong to different slots.
        /// </summary>
        private async Task<(AiExecutionRecord? Record, RedisValue StateBlob)> LoadRecordAndStateBlobAsync(
            string executionId,
            RedisKey stateKey,
            CancellationToken cancellationToken)
        {
            if (!UsesRedisCluster())
            {
                var values = await _services.Database
                    .StringGetAsync(
                        new RedisKey[]
                        {
                            _services.KeyBuilder.GetExecutionRecordKey(executionId),
                            stateKey
                        })
                    .ConfigureAwait(false);

                Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics.Record(
                    _services.Database,
                    Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.DagRecordStateLoadMany,
                    "MGET",
                    values);

                var recordValue = values.Length > 0
                    ? values[0]
                    : RedisValue.Null;
                var stateBlob = values.Length > 1
                    ? values[1]
                    : RedisValue.Null;

                return (DeserializeRecord(recordValue), stateBlob);
            }

            var record = await GetRecordAsync(
                    executionId,
                    cancellationToken)
                .ConfigureAwait(false);
            var clusterStateBlob = await _services.Database
                .StringGetAsync(stateKey)
                .ConfigureAwait(false);

            Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics.Record(
                _services.Database,
                Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.DagStateBlobLoad,
                "GET",
                clusterStateBlob);

            return (record, clusterStateBlob);
        }

        /// <summary>
        /// Loads indexed step payloads with one multi-key read when Redis is not clustered.
        /// Redis Cluster keeps the existing one-key-per-command behavior because the current
        /// DAG keys do not yet share an execution hash tag and may belong to different slots.
        /// </summary>
        private async Task<RedisValue[]> LoadIndexedStepValuesAsync(
            RedisKey[] stepKeys)
        {
            ArgumentNullException.ThrowIfNull(stepKeys);

            if (stepKeys.Length == 0)
            {
                return Array.Empty<RedisValue>();
            }

            if (!UsesRedisCluster())
            {
                var nonClusterValues = await _services.Database
                    .StringGetAsync(stepKeys)
                    .ConfigureAwait(false);

                Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics.Record(
                    _services.Database,
                    "Dag.Step.LoadMany",
                    "MGET",
                    nonClusterValues);

                return nonClusterValues;
            }

            var values = new RedisValue[stepKeys.Length];

            for (var index = 0; index < stepKeys.Length; index++)
            {
                values[index] = await _services.Database
                    .StringGetAsync(stepKeys[index])
                    .ConfigureAwait(false);

                Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics.Record(
                    _services.Database,
                    "Dag.Step.Load.Cluster",
                    "GET",
                    values[index]);
            }

            return values;
        }

        /// <summary>
        /// Determines whether the configured Redis topology contains a cluster server.
        /// </summary>
        private bool UsesRedisCluster()
        {
            foreach (var endpoint in _services.Multiplexer.GetEndPoints())
            {
                if (_services.Multiplexer.GetServer(endpoint).ServerType == ServerType.Cluster)
                {
                    return true;
                }
            }

            return false;
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
            Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionDiagnostics.Record(
                _services.Database,
                Multiplexed.AI.Runtime.Observability.Performance.AiRedisReadAttributionOperations.DagExecutionRecordLoad,
                "GET",
                value);

            if (!value.HasValue)
                return null;

            return DeserializeRecord(value);
        }

        private AiExecutionRecord? DeserializeRecord(
            RedisValue value)
        {
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
