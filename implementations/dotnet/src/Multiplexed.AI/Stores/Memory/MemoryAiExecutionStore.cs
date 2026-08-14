using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Abstractions.AI.Retry;
using Multiplexed.AI.Runtime.Execution;
using Multiplexed.AI.Stores.Creation;
using System.Collections.Concurrent;

namespace Multiplexed.AI.Stores.Memory
{
    /// <summary>
    /// In-memory fallback store for AI execution records and states.
    /// 
    /// This store is intended for resilience and temporary fallback scenarios.
    /// It is not a durable persistence mechanism.
    /// </summary>
    public sealed class MemoryAiExecutionStore : IAiExecutionStore, IAiExecutionCreateIfAbsentStore
    {
        private readonly ConcurrentDictionary<string, AiExecutionRecord> _records = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, AiExecutionState> _states = new(StringComparer.Ordinal);
        private readonly object _createIfAbsentSync = new();

        /// <summary>
        /// Creates a new execution record and state in memory.
        /// </summary>
        public Task CreateAsync(
            AiExecutionRecord record,
            AiExecutionState state,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(state);

            _records[record.ExecutionId] = CloneRecord(record);
            _states[state.ExecutionId] = CloneState(state);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Attempts to create one exact execution record/state pair without replacing an existing execution.
        /// </summary>
        /// <param name="record">The execution record carrying the exact identifier.</param>
        /// <param name="state">The execution state carrying the same exact identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><c>true</c> when the pair was created; otherwise <c>false</c>.</returns>
        public Task<bool> TryCreateIfAbsentAsync(
            AiExecutionRecord record,
            AiExecutionState state,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(state);

            if (!string.Equals(record.ExecutionId, state.ExecutionId, StringComparison.Ordinal))
            {
                throw new ArgumentException("Record and State must share the same ExecutionId.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            lock (_createIfAbsentSync)
            {
                if (_records.ContainsKey(record.ExecutionId) || _states.ContainsKey(record.ExecutionId))
                {
                    return Task.FromResult(false);
                }

                _records[record.ExecutionId] = CloneRecord(record);
                _states[state.ExecutionId] = CloneState(state);
                return Task.FromResult(true);
            }
        }

        /// <summary>
        /// Retrieves an execution record from memory.
        /// </summary>
        public Task<AiExecutionRecord?> GetRecordAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));

            _records.TryGetValue(executionId, out var record);

            return Task.FromResult(record is null ? null : CloneRecord(record));
        }

        /// <summary>
        /// Retrieves an execution state from memory.
        /// </summary>
        public Task<AiExecutionState?> GetStateAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));

            _states.TryGetValue(executionId, out var state);

            return Task.FromResult(state is null ? null : CloneState(state));
        }

        /// <summary>
        /// Attempts to update an execution record and state using optimistic concurrency.
        /// </summary>
        public Task<bool> TryUpdateAsync(
            string executionId,
            string expectedStepKey,
            AiExecutionRecord record,
            AiExecutionState state,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));

            if (string.IsNullOrWhiteSpace(expectedStepKey))
                throw new ArgumentException("Expected step key cannot be null or empty.", nameof(expectedStepKey));

            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(state);

            if (!_records.TryGetValue(executionId, out var currentRecord))
                return Task.FromResult(false);

            if (!string.Equals(currentRecord.ExecutionStepKey, expectedStepKey, StringComparison.Ordinal))
                return Task.FromResult(false);

            _records[executionId] = CloneRecord(record);
            _states[executionId] = CloneState(state);

            return Task.FromResult(true);
        }

        /// <summary>
        /// Saves an execution record independently in memory.
        /// 
        /// If an entry already exists, it is replaced with a defensive copy.
        /// </summary>
        public Task SaveRecordAsync(
            AiExecutionRecord record,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);

            _records[record.ExecutionId] = CloneRecord(record);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Saves an execution state independently in memory.
        /// 
        /// If an entry already exists, it is replaced with a defensive copy.
        /// </summary>
        public Task SaveStateAsync(
            string executionId,
            AiExecutionState state,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));

            ArgumentNullException.ThrowIfNull(state);

            _states[executionId] = CloneState(state);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Restores an execution record and state in memory without concurrency checks.
        ///
        /// This method is intended for replay and recovery flows.
        /// Existing values are overwritten with defensive copies.
        /// </summary>
        public Task RestoreAsync(
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

            _records[record.ExecutionId] = CloneRecord(record);
            _states[state.ExecutionId] = CloneState(state);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Deletes an execution record from memory.
        /// 
        /// This operation is idempotent. If the record does not exist,
        /// the method completes successfully without throwing.
        /// </summary>
        public Task DeleteRecordAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));

            _records.TryRemove(executionId, out _);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Deletes an execution state from memory.
        /// 
        /// This operation is idempotent. If the state does not exist,
        /// the method completes successfully without throwing.
        /// </summary>
        public Task DeleteStateAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));

            _states.TryRemove(executionId, out _);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Creates a defensive copy of the execution record.
        /// </summary>
        private static AiExecutionRecord CloneRecord(AiExecutionRecord source)
        {
            return new AiExecutionRecord
            {
                ExecutionId = source.ExecutionId,
                PipelineName = source.PipelineName,
                PipelineDefinitionSnapshot = CloneStoredPayload(source.PipelineDefinitionSnapshot),
                ExecutionMode = source.ExecutionMode,
                ContextKey = source.ContextKey,
                CurrentStepIndex = source.CurrentStepIndex,
                Steps = new List<string>(source.Steps),
                CompletedSteps = new List<string>(source.CompletedSteps),
                ExecutionContextSnapshot = source.ExecutionContextSnapshot,
                Status = source.Status,
                Version = source.Version,
                CreatedAtUtc = source.CreatedAtUtc,
                UpdatedAtUtc = source.UpdatedAtUtc,
                CurrentStep = source.CurrentStep,
                ExecutionStepKey = source.ExecutionStepKey
            };
        }

        /// <summary>
        /// Creates a defensive copy of an optional stored payload descriptor.
        /// </summary>
        /// <param name="source">The source stored payload descriptor.</param>
        /// <returns>The copied descriptor, or <c>null</c> when no payload is present.</returns>
        private static AiStoredPayload? CloneStoredPayload(AiStoredPayload? source)
        {
            if (source is null)
            {
                return null;
            }

            return new AiStoredPayload
            {
                IsInline = source.IsInline,
                InlineValue = source.InlineValue,
                ArtifactId = source.ArtifactId,
                ContentHash = source.ContentHash,
                SizeBytes = source.SizeBytes,
                ContentType = source.ContentType
            };
        }

        /// <summary>
        /// Creates a defensive copy of the execution state.
        /// </summary>
        private static AiExecutionState CloneState(AiExecutionState source)
        {
            return new AiExecutionState
            {
                ExecutionId = source.ExecutionId,
                PipelineName = source.PipelineName,

                Data = new Dictionary<string, object?>(
                    source.Data,
                    StringComparer.Ordinal),

                Metadata = new Dictionary<string, object?>(
                    source.Metadata,
                    StringComparer.Ordinal),

                Steps = source.Steps.ToDictionary(
                    kvp => kvp.Key,
                    kvp => CloneStepState(kvp.Value),
                    StringComparer.Ordinal),

                PipelineConfig = new Dictionary<string, object?>(
                    source.PipelineConfig,
                    StringComparer.Ordinal),

                DataPayloads = source.DataPayloads != null
                    ? new Dictionary<string, AiStoredPayload>(source.DataPayloads, StringComparer.Ordinal)
                    : new Dictionary<string, AiStoredPayload>(StringComparer.Ordinal),

                                MetadataPayloads = source.MetadataPayloads != null
                    ? new Dictionary<string, AiStoredPayload>(source.MetadataPayloads, StringComparer.Ordinal)
                    : new Dictionary<string, AiStoredPayload>(StringComparer.Ordinal),

                CreatedAtUtc = source.CreatedAtUtc,
                UpdatedAtUtc = source.UpdatedAtUtc
            };
        }

        /// <summary>
        /// Creates a defensive copy of a step state.
        /// </summary>
        private static AiStepState CloneStepState(AiStepState source)
        {
            return new AiStepState
            {
                StepName = source.StepName,
                Status = source.Status,
                DependsOn = new List<string>(source.DependsOn),

                ClaimedBy = source.ClaimedBy,
                ClaimToken = source.ClaimToken,
                ClaimedAtUtc = source.ClaimedAtUtc,
                LeaseExpiresAtUtc = source.LeaseExpiresAtUtc,

                StartedAtUtc = source.StartedAtUtc,
                UpdatedAtUtc = source.UpdatedAtUtc,
                CompletedAtUtc = source.CompletedAtUtc,
                Duration = source.Duration,

                Error = source.Error,
                RecoveryCount = source.RecoveryCount,

                Retry = source.Retry,
                RetryState = source.RetryState is null
                    ? null
                    : new AiStepRetryState
                    {
                        RetryCount = source.RetryState.RetryCount,
                        RetryReason = source.RetryState.RetryReason,
                        LastRetryAtUtc = source.RetryState.LastRetryAtUtc,
                        NextRetryAtUtc = source.RetryState.NextRetryAtUtc,
                        LastRetryPolicyKey = source.RetryState.LastRetryPolicyKey
                    },

                ClaimTimeoutSeconds = source.ClaimTimeoutSeconds,

                Inputs = new Dictionary<string, object?>(
                    source.Inputs ?? new Dictionary<string, object?>(),
                    StringComparer.Ordinal),

                Config = new Dictionary<string, object?>(
                    source.Config ?? new Dictionary<string, object?>(),
                    StringComparer.Ordinal),

                InputPayloads = source.InputPayloads is null
                    ? null
                    : new Dictionary<string, AiStoredPayload>(
                        source.InputPayloads,
                        StringComparer.Ordinal),

                ConfigPayloads = source.ConfigPayloads is null
                    ? null
                    : new Dictionary<string, AiStoredPayload>(
                        source.ConfigPayloads,
                        StringComparer.Ordinal),

                Result = CloneStepResult(source.Result),
                Version = source.Version
            };
        }

        /// <summary>
        /// Creates a defensive copy of a step result.
        /// </summary>
        private static AiStepResult? CloneStepResult(AiStepResult? source)
        {
            if (source is null)
                return null;

            return new AiStepResult
            {
                Success = source.Success,
                Error = source.Error,
                Output = source.Output,
                Data = new Dictionary<string, object?>(
                        source.Data ?? new Dictionary<string, object?>(),
                        StringComparer.Ordinal)
            };
        }
    }
}