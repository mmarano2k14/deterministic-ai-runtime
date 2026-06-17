using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using System.Collections.Concurrent;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Store
{
    /// <summary>
    /// In-memory implementation of the shared runtime controller run store.
    /// </summary>
    /// <remarks>
    /// This implementation is intended for unit tests, local demos, and single-process
    /// development scenarios.
    ///
    /// Distributed deployments should use a Redis-backed implementation with atomic
    /// create and cancel transitions.
    /// </remarks>
    public sealed class InMemoryAiSharedRunStore : IAiSharedRunStore
    {
        private readonly ConcurrentDictionary<string, AiSharedRunRecord> _runs =
            new(StringComparer.Ordinal);

        /// <inheritdoc />
        public Task<AiSharedRunRecord> CreateAsync(
            AiSharedRunRecord record,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentException.ThrowIfNullOrWhiteSpace(record.SharedRunId);

            cancellationToken.ThrowIfCancellationRequested();

            if (!_runs.TryAdd(record.SharedRunId, record))
            {
                throw new InvalidOperationException(
                    $"Shared run '{record.SharedRunId}' already exists.");
            }

            return Task.FromResult(record);
        }

        /// <inheritdoc />
        public Task<AiSharedRunRecord?> GetAsync(
            string sharedRunId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);

            cancellationToken.ThrowIfCancellationRequested();

            _runs.TryGetValue(sharedRunId, out var record);

            return Task.FromResult(record);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiSharedRunRecord>> ListAsync(
            bool includeCancelled = false,
            bool includeCompleted = false,
            bool includeFailed = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var records = _runs.Values
                .Where(run => includeCancelled || run.Status != AiSharedRunStatus.Cancelled)
                .Where(run => includeCompleted || run.Status != AiSharedRunStatus.Completed)
                .Where(run => includeFailed || run.Status != AiSharedRunStatus.Failed)
                .OrderBy(run => run.SubmittedAtUtc)
                .ThenBy(run => run.SharedRunId, StringComparer.Ordinal)
                .ToArray();

            return Task.FromResult<IReadOnlyList<AiSharedRunRecord>>(records);
        }

        /// <inheritdoc />
        public Task<AiSharedRunRecord?> CancelAsync(
            string sharedRunId,
            string? reason = null,
            string? requestedBy = null,
            string? source = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);

            cancellationToken.ThrowIfCancellationRequested();

            while (true)
            {
                if (!_runs.TryGetValue(sharedRunId, out var existing))
                {
                    return Task.FromResult<AiSharedRunRecord?>(null);
                }

                if (IsTerminal(existing.Status))
                {
                    return Task.FromResult<AiSharedRunRecord?>(existing);
                }

                var updated = CreateCancelledRecord(
                    existing,
                    reason,
                    requestedBy,
                    source);

                if (_runs.TryUpdate(sharedRunId, updated, existing))
                {
                    return Task.FromResult<AiSharedRunRecord?>(updated);
                }
            }
        }

        /// <inheritdoc />
        public Task<AiSharedRunRecord?> MarkDispatchedAsync(
            string sharedRunId,
            string runtimeInstanceId,
            string? localRunId = null,
            string? executionId = null,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            while (true)
            {
                if (!_runs.TryGetValue(sharedRunId, out var existing))
                {
                    return Task.FromResult<AiSharedRunRecord?>(null);
                }

                if (IsTerminal(existing.Status))
                {
                    return Task.FromResult<AiSharedRunRecord?>(existing);
                }

                var updated = new AiSharedRunRecord
                {
                    SharedRunId = existing.SharedRunId,
                    Status = AiSharedRunStatus.Dispatched,
                    RunRequest = existing.RunRequest,
                    LocalRunId = localRunId ?? existing.LocalRunId,
                    ExecutionId = executionId ?? existing.ExecutionId,
                    AssignedRuntimeInstanceId = runtimeInstanceId,
                    AdmissionDecision = existing.AdmissionDecision,
                    ExecutionContextSnapshot = existing.ExecutionContextSnapshot,
                    PipelineKey = existing.PipelineKey,
                    CorrelationId = existing.CorrelationId,
                    RequestedBy = existing.RequestedBy,
                    Source = existing.Source,
                    Reason = reason ?? existing.Reason,
                    FailureReason = existing.FailureReason,
                    SubmittedAtUtc = existing.SubmittedAtUtc,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Metadata = existing.Metadata
                };

                if (_runs.TryUpdate(sharedRunId, updated, existing))
                {
                    return Task.FromResult<AiSharedRunRecord?>(updated);
                }
            }
        }

        /// <summary>
        /// Determines whether a shared run status is terminal.
        /// </summary>
        /// <param name="status">The shared run status.</param>
        /// <returns><c>true</c> when the status is terminal; otherwise, <c>false</c>.</returns>
        private static bool IsTerminal(
            AiSharedRunStatus status)
        {
            return status is
                AiSharedRunStatus.Completed or
                AiSharedRunStatus.Failed or
                AiSharedRunStatus.Cancelled;
        }

        /// <summary>
        /// Creates a cancelled copy of an existing shared run record.
        /// </summary>
        /// <param name="existing">The existing shared run record.</param>
        /// <param name="reason">The optional cancellation reason.</param>
        /// <param name="requestedBy">The optional identity requesting cancellation.</param>
        /// <param name="source">The optional source adapter requesting cancellation.</param>
        /// <returns>The cancelled shared run record.</returns>
        private static AiSharedRunRecord CreateCancelledRecord(
            AiSharedRunRecord existing,
            string? reason,
            string? requestedBy,
            string? source)
        {
            return new AiSharedRunRecord
            {
                SharedRunId = existing.SharedRunId,
                Status = AiSharedRunStatus.Cancelled,
                RunRequest = existing.RunRequest,
                LocalRunId = existing.LocalRunId,
                ExecutionId = existing.ExecutionId,
                AssignedRuntimeInstanceId = existing.AssignedRuntimeInstanceId,
                AdmissionDecision = existing.AdmissionDecision,
                ExecutionContextSnapshot = existing.ExecutionContextSnapshot,
                PipelineKey = existing.PipelineKey,
                CorrelationId = existing.CorrelationId,
                RequestedBy = requestedBy ?? existing.RequestedBy,
                Source = source ?? existing.Source,
                Reason = reason ?? existing.Reason,
                FailureReason = reason ?? "Shared run cancelled.",
                SubmittedAtUtc = existing.SubmittedAtUtc,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Metadata = existing.Metadata
            };
        }
    }
}