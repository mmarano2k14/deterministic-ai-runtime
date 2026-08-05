using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.Core.ExecutionContext;
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
    ///
    /// When an execution-context snapshot provider is available, read operations are
    /// tenant-filtered using ExecutionContextSnapshot.TenantId.
    /// </remarks>
    public sealed class InMemoryAiSharedRunStore : IAiSharedRunStore
    {
        private readonly ConcurrentDictionary<string, AiSharedRunRecord> _runs =
            new(StringComparer.Ordinal);

        private readonly IExecutionContextSnapshotProvider? _executionContextSnapshotProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="InMemoryAiSharedRunStore"/> class.
        /// </summary>
        public InMemoryAiSharedRunStore()
        {
        }

        /// <summary>
        /// Initializes a new tenant-aware instance of the <see cref="InMemoryAiSharedRunStore"/> class.
        /// </summary>
        /// <param name="executionContextSnapshotProvider">The execution context snapshot provider.</param>
        public InMemoryAiSharedRunStore(
            IExecutionContextSnapshotProvider executionContextSnapshotProvider)
        {
            _executionContextSnapshotProvider =
                executionContextSnapshotProvider ??
                throw new ArgumentNullException(nameof(executionContextSnapshotProvider));
        }

        /// <inheritdoc />
        public Task<AiSharedRunRecord> CreateAsync(
            AiSharedRunRecord record,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentException.ThrowIfNullOrWhiteSpace(record.SharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(record.ExecutionContextSnapshot.TenantId);

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

            if (record is null)
            {
                return Task.FromResult<AiSharedRunRecord?>(null);
            }

            var tenantId =
                TryResolveTenantId();

            if (!BelongsToTenant(
                    record,
                    tenantId))
            {
                return Task.FromResult<AiSharedRunRecord?>(null);
            }

            return Task.FromResult<AiSharedRunRecord?>(record);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiSharedRunRecord>> ListAsync(
            bool includeCancelled = false,
            bool includeCompleted = false,
            bool includeFailed = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tenantId =
                TryResolveTenantId();

            var records = _runs.Values
                .Where(run => BelongsToTenant(run, tenantId))
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

            var tenantId =
                TryResolveTenantId();

            while (true)
            {
                if (!_runs.TryGetValue(sharedRunId, out var existing))
                {
                    return Task.FromResult<AiSharedRunRecord?>(null);
                }

                if (!BelongsToTenant(
                        existing,
                        tenantId))
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

                if (IsTerminal(existing.Status) ||
                    HasDurableDispatchOwnership(existing))
                {
                    return Task.FromResult<AiSharedRunRecord?>(existing);
                }

                var updated = new AiSharedRunRecord
                {
                    SharedRunId = existing.SharedRunId,
                    ControlPlaneId = existing.ControlPlaneId,
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

        /// <inheritdoc />
        public Task<AiSharedRunRecord?> MarkDispatchFailedAsync(
            string sharedRunId,
            string runtimeInstanceId,
            string? failureReason,
            string? message,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            var tenantId =
                TryResolveTenantId();

            while (true)
            {
                if (!_runs.TryGetValue(sharedRunId, out var existing))
                {
                    return Task.FromResult<AiSharedRunRecord?>(null);
                }

                if (!BelongsToTenant(
                        existing,
                        tenantId))
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
                    ControlPlaneId = existing.ControlPlaneId,
                    Status = existing.Status,
                    RunRequest = existing.RunRequest,
                    LocalRunId = existing.LocalRunId,
                    ExecutionId = existing.ExecutionId,
                    AssignedRuntimeInstanceId = runtimeInstanceId,
                    AdmissionDecision = existing.AdmissionDecision,
                    ExecutionContextSnapshot = existing.ExecutionContextSnapshot,
                    PipelineKey = existing.PipelineKey,
                    CorrelationId = existing.CorrelationId,
                    RequestedBy = existing.RequestedBy,
                    Source = existing.Source,
                    Reason = message ?? existing.Reason,
                    FailureReason = failureReason,
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

        /// <inheritdoc />
        public Task<AiSharedRunRecord?> MarkRequeuedAfterScaleOutAsync(
            string sharedRunId,
            string? reason = null,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            return MarkRequeuedAfterScaleOutIfCurrentAsync(
                sharedRunId,
                expectedAssignedRuntimeInstanceId: null,
                expectedLocalRunId: null,
                reason,
                metadata,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiSharedRunRecord?> MarkRequeuedAfterScaleOutIfCurrentAsync(
            string sharedRunId,
            string? expectedAssignedRuntimeInstanceId,
            string? expectedLocalRunId,
            string? reason = null,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);

            cancellationToken.ThrowIfCancellationRequested();

            ValidateExpectedOwnership(
                expectedAssignedRuntimeInstanceId,
                expectedLocalRunId);

            var tenantId =
                TryResolveTenantId();

            while (true)
            {
                if (!_runs.TryGetValue(sharedRunId, out var existing))
                {
                    return Task.FromResult<AiSharedRunRecord?>(null);
                }

                if (!BelongsToTenant(
                        existing,
                        tenantId))
                {
                    return Task.FromResult<AiSharedRunRecord?>(null);
                }

                if (!CanRequeueAfterScaleOut(
                        existing,
                        expectedAssignedRuntimeInstanceId,
                        expectedLocalRunId))
                {
                    return Task.FromResult<AiSharedRunRecord?>(existing);
                }

                var now =
                    DateTimeOffset.UtcNow;

                var mergedMetadata =
                    new Dictionary<string, string>(
                        existing.Metadata,
                        StringComparer.OrdinalIgnoreCase);

                if (metadata is not null)
                {
                    foreach (var item in metadata)
                    {
                        mergedMetadata[item.Key] = item.Value;
                    }
                }

                mergedMetadata["scaleOutRequeued"] = "true";
                mergedMetadata["scaleOutRequeuedAtUtc"] = now.ToString("O");

                var updated = new AiSharedRunRecord
                {
                    SharedRunId = existing.SharedRunId,
                    ControlPlaneId = existing.ControlPlaneId,
                    Status = AiSharedRunStatus.QueuedGlobally,
                    RunRequest = existing.RunRequest,
                    LocalRunId = null,
                    ExecutionId = existing.ExecutionId,
                    AssignedRuntimeInstanceId = null,
                    AdmissionDecision = existing.AdmissionDecision,
                    ExecutionContextSnapshot = existing.ExecutionContextSnapshot,
                    PipelineKey = existing.PipelineKey,
                    CorrelationId = existing.CorrelationId,
                    RequestedBy = existing.RequestedBy,
                    Source = existing.Source,
                    Reason = string.IsNullOrWhiteSpace(reason)
                        ? "Scale-out fulfilled; shared run requeued for dispatch."
                        : reason,
                    FailureReason = string.Empty,
                    SubmittedAtUtc = existing.SubmittedAtUtc,
                    UpdatedAtUtc = now,
                    Metadata = mergedMetadata
                };

                if (_runs.TryUpdate(sharedRunId, updated, existing))
                {
                    return Task.FromResult<AiSharedRunRecord?>(updated);
                }
            }
        }

        /// <summary>
        /// Determines whether the current in-memory record still satisfies the
        /// scale-out or recovery ownership compare-and-set.
        /// </summary>
        /// <param name="existing">The current shared run record.</param>
        /// <param name="expectedAssignedRuntimeInstanceId">The expected failed runtime id.</param>
        /// <param name="expectedLocalRunId">The expected failed local run id.</param>
        /// <returns><c>true</c> when the record may be requeued.</returns>
        private static bool CanRequeueAfterScaleOut(
            AiSharedRunRecord existing,
            string? expectedAssignedRuntimeInstanceId,
            string? expectedLocalRunId)
        {
            if (IsTerminal(existing.Status))
            {
                return false;
            }

            if (HasExpectedOwnership(
                    expectedAssignedRuntimeInstanceId,
                    expectedLocalRunId))
            {
                return string.Equals(
                        existing.AssignedRuntimeInstanceId,
                        expectedAssignedRuntimeInstanceId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        existing.LocalRunId,
                        expectedLocalRunId,
                        StringComparison.Ordinal);
            }

            return existing.Status == AiSharedRunStatus.ScaleOutRequested &&
                string.IsNullOrWhiteSpace(existing.AssignedRuntimeInstanceId) &&
                string.IsNullOrWhiteSpace(existing.LocalRunId);
        }

        /// <summary>
        /// Validates that expected failed ownership is supplied as a complete pair.
        /// </summary>
        /// <param name="expectedAssignedRuntimeInstanceId">The expected failed runtime id.</param>
        /// <param name="expectedLocalRunId">The expected failed local run id.</param>
        private static void ValidateExpectedOwnership(
            string? expectedAssignedRuntimeInstanceId,
            string? expectedLocalRunId)
        {
            var hasExpectedRuntime =
                !string.IsNullOrWhiteSpace(
                    expectedAssignedRuntimeInstanceId);

            var hasExpectedLocalRun =
                !string.IsNullOrWhiteSpace(
                    expectedLocalRunId);

            if (hasExpectedRuntime != hasExpectedLocalRun)
            {
                throw new ArgumentException(
                    "Expected failed runtime instance id and local run id must be supplied together.");
            }
        }

        /// <summary>
        /// Determines whether complete expected failed ownership is available.
        /// </summary>
        /// <param name="expectedAssignedRuntimeInstanceId">The expected failed runtime id.</param>
        /// <param name="expectedLocalRunId">The expected failed local run id.</param>
        /// <returns><c>true</c> when both ownership identifiers are present.</returns>
        private static bool HasExpectedOwnership(
            string? expectedAssignedRuntimeInstanceId,
            string? expectedLocalRunId)
        {
            return !string.IsNullOrWhiteSpace(
                    expectedAssignedRuntimeInstanceId) &&
                !string.IsNullOrWhiteSpace(
                    expectedLocalRunId);
        }

        /// <summary>
        /// Attempts to resolve the current tenant id from the execution context snapshot provider.
        /// </summary>
        /// <returns>The current tenant id, or <c>null</c> when no active context is available.</returns>
        private string? TryResolveTenantId()
        {
            if (_executionContextSnapshotProvider is null)
            {
                return null;
            }

            try
            {
                var snapshot =
                    _executionContextSnapshotProvider
                        .MapToSnapshot();

                return string.IsNullOrWhiteSpace(snapshot.TenantId)
                    ? null
                    : snapshot.TenantId;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>
        /// Determines whether a record belongs to the current tenant.
        /// </summary>
        /// <param name="record">The shared run record.</param>
        /// <param name="expectedTenantId">The expected tenant id.</param>
        /// <returns><c>true</c> when the record belongs to the expected tenant; otherwise, <c>false</c>.</returns>
        private static bool BelongsToTenant(
            AiSharedRunRecord record,
            string? expectedTenantId)
        {
            if (string.IsNullOrWhiteSpace(expectedTenantId))
            {
                return true;
            }

            var recordTenantId =
                record.ExecutionContextSnapshot.TenantId;

            if (string.IsNullOrWhiteSpace(recordTenantId))
            {
                return false;
            }

            return string.Equals(
                NormalizeKeySegment(recordTenantId),
                NormalizeKeySegment(expectedTenantId),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Determines whether a shared run status is terminal.
        /// </summary>
        /// <param name="status">The shared run status.</param>
        /// <returns><c>true</c> when the status is terminal; otherwise, <c>false</c>.</returns>
        private static bool HasDurableDispatchOwnership(
            AiSharedRunRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);

            return record.Status == AiSharedRunStatus.Dispatched &&
                !string.IsNullOrWhiteSpace(
                    record.AssignedRuntimeInstanceId) &&
                !string.IsNullOrWhiteSpace(
                    record.LocalRunId);
        }

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
                ControlPlaneId = existing.ControlPlaneId,
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

        /// <summary>
        /// Normalizes a value so it can be compared like a Redis key segment.
        /// </summary>
        /// <param name="value">The value to normalize.</param>
        /// <returns>The normalized key segment.</returns>
        private static string NormalizeKeySegment(
            string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);

            return value
                .Trim()
                .Replace(" ", "-", StringComparison.Ordinal)
                .Replace("\\", "/", StringComparison.Ordinal);
        }
    }
}