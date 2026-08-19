using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.AI.Stores.Cache.Redis.Helpers;
using Multiplexed.AI.Stores.Cache.Redis.Serialization;
using StackExchange.Redis;
using System.Text.Json;

namespace Multiplexed.AI.Stores.Cache.Redis.Dag
{
    /// <summary>
    /// Handles Redis DAG execution state write operations.
    /// </summary>
    public sealed class RedisDagStoreStateWriter
    {
        private readonly IRedisDagStoreServices _services;

        public RedisDagStoreStateWriter(IRedisDagStoreServices services)
        {
            ArgumentNullException.ThrowIfNull(services);

            _services = services;
        }

        /// <summary>
        /// Creates a new execution in Redis.
        ///
        /// This will:
        /// - store the execution record
        /// - store the full execution state blob
        /// - create one Redis key per step
        /// - register each step name in the execution step index
        /// </summary>
        public async Task CreateAsync(
            AiExecutionRecord record,
            AiExecutionState state,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(state);

            if (!string.Equals(record.ExecutionId, state.ExecutionId, StringComparison.Ordinal))
                throw new ArgumentException("Record and state must share the same ExecutionId.");

            var recordKey = _services.KeyBuilder.GetExecutionRecordKey(record.ExecutionId);
            var stateKey = _services.Helper.GetStateBlobKey(record.ExecutionId);
            var stepIndexKey = _services.KeyBuilder.GetDagStepIdsKey(record.ExecutionId);

            await _services.Database.StringSetAsync(
                recordKey,
                JsonSerializer.Serialize(record, _services.JsonOptions));

            await _services.Database.StringSetAsync(
                stateKey,
                JsonSerializer.Serialize(state, _services.JsonOptions));

            foreach (var step in state.Steps.Values)
            {
                step.DependsOn ??= new List<string>();

                var stepKey = _services.KeyBuilder.GetDagStepKey(record.ExecutionId, step.StepName);

                await _services.Database.StringSetAsync(
                    stepKey,
                    JsonSerializer.Serialize(step, _services.JsonOptions));

                await _services.Database.SetAddAsync(stepIndexKey, step.StepName);
            }
        }

        /// <summary>
        /// Atomically creates one distributed DAG execution bundle when none of its durable keys exist.
        /// </summary>
        /// <param name="record">The execution record carrying the exact identifier.</param>
        /// <param name="state">The initial DAG state carrying the same exact identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><c>true</c> when this caller created the bundle; otherwise <c>false</c>.</returns>
        public async Task<bool> TryCreateIfAbsentAsync(
            AiExecutionRecord record,
            AiExecutionState state,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(state);

            if (!string.Equals(record.ExecutionId, state.ExecutionId, StringComparison.Ordinal))
            {
                throw new ArgumentException("Record and state must share the same ExecutionId.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            foreach (var step in state.Steps.Values)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(step.StepName);
                step.DependsOn ??= new List<string>();
            }

            var recordKey = _services.KeyBuilder.GetExecutionRecordKey(record.ExecutionId);
            var stateKey = _services.Helper.GetStateBlobKey(record.ExecutionId);
            var stepIndexKey = _services.KeyBuilder.GetDagStepIdsKey(record.ExecutionId);
            var transaction = _services.Database.CreateTransaction();

            transaction.AddCondition(Condition.KeyNotExists(recordKey));
            transaction.AddCondition(Condition.KeyNotExists(stateKey));
            transaction.AddCondition(Condition.KeyNotExists(stepIndexKey));

            foreach (var step in state.Steps.Values)
            {
                transaction.AddCondition(
                    Condition.KeyNotExists(
                        _services.KeyBuilder.GetDagStepKey(record.ExecutionId, step.StepName)));
            }

            _ = transaction.StringSetAsync(
                recordKey,
                JsonSerializer.Serialize(record, _services.JsonOptions));

            _ = transaction.StringSetAsync(
                stateKey,
                JsonSerializer.Serialize(state, _services.JsonOptions));

            foreach (var step in state.Steps.Values)
            {
                var stepKey = _services.KeyBuilder.GetDagStepKey(record.ExecutionId, step.StepName);

                _ = transaction.StringSetAsync(
                    stepKey,
                    JsonSerializer.Serialize(step, _services.JsonOptions));

                _ = transaction.SetAddAsync(stepIndexKey, step.StepName);
            }

            return await transaction.ExecuteAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Saves the execution record independently.
        ///
        /// This overwrites the current execution record value without modifying step keys.
        /// </summary>
        public async Task SaveRecordAsync(
            AiExecutionRecord record,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);

            await _services.Database.StringSetAsync(
                _services.KeyBuilder.GetExecutionRecordKey(record.ExecutionId),
                JsonSerializer.Serialize(record, _services.JsonOptions));
        }

        /// <summary>
        /// Saves the full distributed DAG state by overwriting the persisted state blob,
        /// indexed step entries, and rebuilding the step index for the execution.
        ///
        /// This method is intended for administrative persistence paths and recovery flows,
        /// not for normal concurrent step claim / complete / fail progression.
        /// </summary>
        public async Task SaveStateAsync(
            string executionId,
            AiExecutionState state,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));

            ArgumentNullException.ThrowIfNull(state);

            var stateKey = _services.Helper.GetStateBlobKey(executionId);
            var stepIndexKey = _services.KeyBuilder.GetDagStepIdsKey(executionId);
            var existingStepNames = await _services.Database.SetMembersAsync(stepIndexKey);

            var record = await _services.StateReader.GetRecordAsync(
                executionId,
                cancellationToken);

            var completedStepNames = record?.CompletedSteps is not null
                ? record.CompletedSteps.ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            var existingSteps = new Dictionary<string, AiStepState>(
                StringComparer.Ordinal);

            foreach (var stepNameValue in existingStepNames)
            {
                var stepName = (string?)stepNameValue;

                if (string.IsNullOrWhiteSpace(stepName))
                    continue;

                var stepKey = _services.KeyBuilder.GetDagStepKey(executionId, stepName);
                var rawStep = await _services.Database.StringGetAsync(stepKey);

                if (!rawStep.HasValue)
                    continue;

                var repairedJson = JsonSerializationHelpers.RepairStepJson((string)rawStep!);
                repairedJson = JsonSerializationHelpers.RepairRetryJson(repairedJson);

                var existingStep = JsonSerializer.Deserialize<AiStepState>(
                    repairedJson,
                    _services.JsonOptions);

                if (existingStep is not null)
                {
                    existingSteps[stepName] = existingStep;
                }
            }

            foreach (var step in state.Steps.Values.ToArray())
            {
                if (string.IsNullOrWhiteSpace(step.StepName))
                    continue;

                if (existingSteps.TryGetValue(step.StepName, out var existingStep) &&
                    RedisDagStoreHelper.IsTerminal(existingStep.Status) &&
                    RedisDagStoreHelper.IsNonTerminal(step.Status))
                {
                    state.Steps[step.StepName] = existingStep;
                    continue;
                }
            }

            await _services.Database.StringSetAsync(
                stateKey,
                JsonSerializer.Serialize(state, _services.JsonOptions));

            foreach (var stepNameValue in existingStepNames)
            {
                var stepName = (string?)stepNameValue;

                if (string.IsNullOrWhiteSpace(stepName))
                    continue;

                await _services.Database.KeyDeleteAsync(
                    _services.KeyBuilder.GetDagStepKey(executionId, stepName));
            }

            await _services.Database.KeyDeleteAsync(stepIndexKey);

            foreach (var step in state.Steps.Values)
            {
                if (string.IsNullOrWhiteSpace(step.StepName))
                    continue;

                step.DependsOn ??= new List<string>();

                var stepKey = _services.KeyBuilder.GetDagStepKey(executionId, step.StepName);

                await _services.Database.StringSetAsync(
                    stepKey,
                    JsonSerializer.Serialize(step, _services.JsonOptions));

                await _services.Database.SetAddAsync(stepIndexKey, step.StepName);
            }
        }

        /// <summary>
        /// Deletes the execution record.
        ///
        /// This operation is idempotent.
        /// </summary>
        public async Task DeleteRecordAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));

            await _services.Database.KeyDeleteAsync(_services.KeyBuilder.GetExecutionRecordKey(executionId));
        }

        /// <summary>
        /// Deletes the full persisted execution state for an execution.
        ///
        /// IMPORTANT:
        /// - In DAG mode, state is represented by:
        ///   - the global state blob
        ///   - step keys
        ///   - the step index
        /// - Deleting only step keys is not sufficient
        /// </summary>
        public async Task DeleteStateAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));

            await _services.Database.KeyDeleteAsync(_services.Helper.GetStateBlobKey(executionId));
            await DeleteStepsAsync(executionId, cancellationToken);
        }

        /// <summary>
        /// Deletes all distributed DAG step keys and the execution step index.
        ///
        /// This operation is idempotent.
        /// </summary>
        public async Task DeleteStepsAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));

            var stepIndexKey = _services.KeyBuilder.GetDagStepIdsKey(executionId);
            var stepNames = await _services.Database.SetMembersAsync(stepIndexKey);

            foreach (var stepNameValue in stepNames)
            {
                var stepName = (string?)stepNameValue;

                if (string.IsNullOrWhiteSpace(stepName))
                    continue;

                await _services.Database.KeyDeleteAsync(_services.KeyBuilder.GetDagStepKey(executionId, stepName));
            }

            await _services.Database.KeyDeleteAsync(stepIndexKey);
        }

        /// <summary>
        /// Deletes the full distributed DAG execution bundle owned by this store:
        /// the global execution record, the state blob, all indexed step keys, and the step index.
        ///
        /// This operation is idempotent and safe to call multiple times.
        /// </summary>
        public async Task DeleteExecutionBundleAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));

            await DeleteStateAsync(executionId, cancellationToken);
            await DeleteRecordAsync(executionId, cancellationToken);
        }

        /// <summary>
        /// Restores an execution record and distributed DAG state.
        ///
        /// PURPOSE:
        /// - Used by replay / recovery flows
        /// - Rebuilds the authoritative DAG record and step state
        /// - Restores the state blob so global bags survive
        /// - Restores the step index so distributed scanning can resume
        ///
        /// IMPORTANT:
        /// - In DAG mode, restoring only a generic "state blob" key is incorrect
        /// - The authoritative DAG state is composed of:
        ///   - the record key
        ///   - the state blob
        ///   - one key per step
        ///   - the step index set
        /// - This method therefore restores the full distributed DAG layout
        /// </summary>
        public async Task RestoreAsync(
            AiExecutionRecord record,
            AiExecutionState state,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(state);

            if (string.IsNullOrWhiteSpace(record.ExecutionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(record));

            if (string.IsNullOrWhiteSpace(state.ExecutionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(state));

            if (!string.Equals(record.ExecutionId, state.ExecutionId, StringComparison.Ordinal))
                throw new ArgumentException("Record and State must share the same ExecutionId.");

            var recordKey = _services.KeyBuilder.GetExecutionRecordKey(record.ExecutionId);
            var stateKey = _services.Helper.GetStateBlobKey(record.ExecutionId);
            var stepIndexKey = _services.KeyBuilder.GetDagStepIdsKey(record.ExecutionId);

            await DeleteStepsAsync(record.ExecutionId, cancellationToken);
            await _services.Database.KeyDeleteAsync(stateKey);

            var transaction = _services.Database.CreateTransaction();

            _ = transaction.StringSetAsync(
                recordKey,
                JsonSerializer.Serialize(record, _services.JsonOptions));

            _ = transaction.StringSetAsync(
                stateKey,
                JsonSerializer.Serialize(state, _services.JsonOptions)); 

            foreach (var step in state.Steps.Values)
            {
                step.DependsOn ??= new List<string>();

                var stepKey = _services.KeyBuilder.GetDagStepKey(record.ExecutionId, step.StepName);

                _ = transaction.StringSetAsync(
                    stepKey,
                    JsonSerializer.Serialize(step, _services.JsonOptions));
                _ = transaction.SetAddAsync(stepIndexKey, step.StepName);
            }

            var committed = await transaction.ExecuteAsync();

            if (!committed)
            {
                throw new InvalidOperationException(
                    $"Distributed DAG restore transaction failed for execution '{record.ExecutionId}'.");
            }
        }

        /// <summary>
        /// Deletes one hot DAG step from Redis and removes it from the execution step index.
        /// </summary>
        /// <remarks>
        /// PURPOSE:
        /// - Used by retention after a step has been safely archived externally.
        /// - Prevents evicted steps from being rehydrated back into hot state by <see cref="GetStateAsync"/>.
        ///
        /// IMPORTANT:
        /// - This method does not delete archived payloads.
        /// - This method only removes the hot Redis step key and its index entry.
        /// - It is idempotent and safe to call multiple times.
        /// </remarks>
        public async Task DeleteStepAsync(
            string executionId,
            string stepName,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
            {
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));
            }

            if (string.IsNullOrWhiteSpace(stepName))
            {
                throw new ArgumentException("Step name cannot be null or empty.", nameof(stepName));
            }

            var stepKey = _services.KeyBuilder.GetDagStepKey(executionId, stepName);
            var stepIndexKey = _services.KeyBuilder.GetDagStepIdsKey(executionId);

            await _services.Database.KeyDeleteAsync(stepKey);
            await _services.Database.SetRemoveAsync(stepIndexKey, stepName);
        }
    }
}