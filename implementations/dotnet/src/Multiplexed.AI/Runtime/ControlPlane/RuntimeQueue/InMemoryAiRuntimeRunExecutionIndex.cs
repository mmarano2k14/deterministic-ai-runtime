using System.Collections.Concurrent;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.Core.ExecutionContext;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue
{
    /// <summary>
    /// In-memory implementation of <see cref="IAiRuntimeRunExecutionIndex"/>.
    /// </summary>
    /// <remarks>
    /// This implementation keeps the local runtime <c>RunId</c> to DAG <c>ExecutionId</c>
    /// relationship in memory for tests, local execution, and lightweight control-plane hosts.
    ///
    /// Tenant isolation is defensive:
    /// - when an <see cref="IExecutionContextSnapshotProvider"/> is available, reads are filtered
    ///   by <see cref="ExecutionContextSnapshot.TenantId"/>;
    /// - entries without a tenant snapshot are hidden from tenant-scoped reads;
    /// - when no execution context provider is configured, the index behaves as the legacy
    ///   in-memory implementation for compatibility.
    ///
    /// The durable tenant boundary is <see cref="ExecutionContextSnapshot.TenantId"/>.
    /// <see cref="ExecutionContextSnapshot.ContextKey"/> is volatile and must not be used as a
    /// durable index key.
    /// </remarks>
    public sealed class InMemoryAiRuntimeRunExecutionIndex : IAiRuntimeRunExecutionIndex
    {
        private const string StatusQueued = "queued";
        private const string StatusRunning = "running";
        private const string StatusCompleted = "completed";
        private const string StatusFailed = "failed";
        private const string StatusCancelled = "cancelled";
        private const string StatusRequeuedForRecovery = "requeued-for-recovery";

        private readonly ConcurrentDictionary<string, AiRuntimeRunExecutionIndexEntry> _entries =
            new(StringComparer.Ordinal);

        private readonly IExecutionContextSnapshotProvider? _executionContextSnapshotProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="InMemoryAiRuntimeRunExecutionIndex"/> class.
        /// </summary>
        public InMemoryAiRuntimeRunExecutionIndex()
        {
        }

        /// <summary>
        /// Initializes a new tenant-aware instance of the <see cref="InMemoryAiRuntimeRunExecutionIndex"/> class.
        /// </summary>
        /// <param name="executionContextSnapshotProvider">
        /// The execution context snapshot provider used to filter reads by tenant.
        /// </param>
        public InMemoryAiRuntimeRunExecutionIndex(
            IExecutionContextSnapshotProvider? executionContextSnapshotProvider)
        {
            _executionContextSnapshotProvider = executionContextSnapshotProvider;
        }

        /// <inheritdoc />
        public Task RegisterQueuedAsync(
            AiRuntimeRunExecutionIndexEntry entry,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.RunId);

            cancellationToken.ThrowIfCancellationRequested();

            var now = DateTimeOffset.UtcNow;

            _entries[entry.RunId] = new AiRuntimeRunExecutionIndexEntry
            {
                RunId = entry.RunId,
                ExecutionId = entry.ExecutionId,
                RuntimeInstanceId = entry.RuntimeInstanceId,
                Status = string.IsNullOrWhiteSpace(entry.Status) ? StatusQueued : entry.Status,
                FailureReason = entry.FailureReason,
                CreatedAtUtc = entry.CreatedAtUtc == default ? now : entry.CreatedAtUtc,
                StartedAtUtc = entry.StartedAtUtc,
                CompletedAtUtc = entry.CompletedAtUtc,
                ExecutionContextSnapshot =
                    entry.ExecutionContextSnapshot ??
                    TryResolveSnapshot(),
                Metadata = entry.Metadata
            };

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task MarkStartedAsync(
            string runId,
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            cancellationToken.ThrowIfCancellationRequested();

            var now = DateTimeOffset.UtcNow;

            _entries.AddOrUpdate(
                runId,
                _ => new AiRuntimeRunExecutionIndexEntry
                {
                    RunId = runId,
                    ExecutionId = executionId,
                    Status = StatusRunning,
                    CreatedAtUtc = now,
                    StartedAtUtc = now,
                    ExecutionContextSnapshot = TryResolveSnapshot()
                },
                (_, existing) => new AiRuntimeRunExecutionIndexEntry
                {
                    RunId = existing.RunId,
                    ExecutionId = executionId,
                    RuntimeInstanceId = existing.RuntimeInstanceId,
                    Status = StatusRunning,
                    FailureReason = null,
                    CreatedAtUtc = existing.CreatedAtUtc,
                    StartedAtUtc = existing.StartedAtUtc ?? now,
                    CompletedAtUtc = null,
                    ExecutionContextSnapshot =
                        existing.ExecutionContextSnapshot ??
                        TryResolveSnapshot(),
                    Metadata = existing.Metadata
                });

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task MarkCompletedAsync(
            string runId,
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            cancellationToken.ThrowIfCancellationRequested();

            var now = DateTimeOffset.UtcNow;

            _entries.AddOrUpdate(
                runId,
                _ => new AiRuntimeRunExecutionIndexEntry
                {
                    RunId = runId,
                    ExecutionId = executionId,
                    Status = StatusCompleted,
                    CreatedAtUtc = now,
                    StartedAtUtc = now,
                    CompletedAtUtc = now,
                    ExecutionContextSnapshot = TryResolveSnapshot()
                },
                (_, existing) => new AiRuntimeRunExecutionIndexEntry
                {
                    RunId = existing.RunId,
                    ExecutionId = executionId,
                    RuntimeInstanceId = existing.RuntimeInstanceId,
                    Status = StatusCompleted,
                    FailureReason = null,
                    CreatedAtUtc = existing.CreatedAtUtc,
                    StartedAtUtc = existing.StartedAtUtc ?? now,
                    CompletedAtUtc = now,
                    ExecutionContextSnapshot =
                        existing.ExecutionContextSnapshot ??
                        TryResolveSnapshot(),
                    Metadata = existing.Metadata
                });

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task MarkFailedAsync(
            string runId,
            string? executionId,
            string failureReason,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

            cancellationToken.ThrowIfCancellationRequested();

            var now = DateTimeOffset.UtcNow;

            _entries.AddOrUpdate(
                runId,
                _ => new AiRuntimeRunExecutionIndexEntry
                {
                    RunId = runId,
                    ExecutionId = executionId,
                    Status = StatusFailed,
                    FailureReason = failureReason,
                    CreatedAtUtc = now,
                    CompletedAtUtc = now,
                    ExecutionContextSnapshot = TryResolveSnapshot()
                },
                (_, existing) => new AiRuntimeRunExecutionIndexEntry
                {
                    RunId = existing.RunId,
                    ExecutionId = executionId ?? existing.ExecutionId,
                    RuntimeInstanceId = existing.RuntimeInstanceId,
                    Status = StatusFailed,
                    FailureReason = failureReason,
                    CreatedAtUtc = existing.CreatedAtUtc,
                    StartedAtUtc = existing.StartedAtUtc,
                    CompletedAtUtc = now,
                    ExecutionContextSnapshot =
                        existing.ExecutionContextSnapshot ??
                        TryResolveSnapshot(),
                    Metadata = existing.Metadata
                });

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task MarkCancelledAsync(
            string runId,
            string? executionId,
            string? reason,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);

            cancellationToken.ThrowIfCancellationRequested();

            var now = DateTimeOffset.UtcNow;

            _entries.AddOrUpdate(
                runId,
                _ => new AiRuntimeRunExecutionIndexEntry
                {
                    RunId = runId,
                    ExecutionId = executionId,
                    Status = StatusCancelled,
                    FailureReason = reason,
                    CreatedAtUtc = now,
                    CompletedAtUtc = now,
                    ExecutionContextSnapshot = TryResolveSnapshot()
                },
                (_, existing) => new AiRuntimeRunExecutionIndexEntry
                {
                    RunId = existing.RunId,
                    ExecutionId = executionId ?? existing.ExecutionId,
                    RuntimeInstanceId = existing.RuntimeInstanceId,
                    Status = StatusCancelled,
                    FailureReason = reason,
                    CreatedAtUtc = existing.CreatedAtUtc,
                    StartedAtUtc = existing.StartedAtUtc,
                    CompletedAtUtc = now,
                    ExecutionContextSnapshot =
                        existing.ExecutionContextSnapshot ??
                        TryResolveSnapshot(),
                    Metadata = existing.Metadata
                });

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<bool> MarkRequeuedForRecoveryAsync(
            string runId,
            string executionId,
            string reason,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);

            cancellationToken.ThrowIfCancellationRequested();

            while (true)
            {
                if (!_entries.TryGetValue(runId, out var existing))
                {
                    return Task.FromResult(false);
                }

                if (!BelongsToCurrentTenant(existing) ||
                    !CanTransitionToRequeuedForRecovery(existing, executionId))
                {
                    return Task.FromResult(false);
                }

                var updated = new AiRuntimeRunExecutionIndexEntry
                {
                    RunId = existing.RunId,
                    ExecutionId = executionId,
                    RuntimeInstanceId = existing.RuntimeInstanceId,
                    Status = StatusRequeuedForRecovery,
                    FailureReason = reason,
                    CreatedAtUtc = existing.CreatedAtUtc,
                    StartedAtUtc = existing.StartedAtUtc,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    ExecutionContextSnapshot =
                        existing.ExecutionContextSnapshot ??
                        TryResolveSnapshot(),
                    Metadata = existing.Metadata
                };

                if (_entries.TryUpdate(runId, updated, existing))
                {
                    return Task.FromResult(true);
                }
            }
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>> ListUnfinishedByRuntimeInstanceAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            var entries = _entries
                .Values
                .Where(entry =>
                    string.Equals(
                        entry.RuntimeInstanceId,
                        runtimeInstanceId,
                        StringComparison.Ordinal) &&
                    IsUnfinished(entry) &&
                    BelongsToCurrentTenant(entry))
                .OrderBy(entry => entry.CreatedAtUtc)
                .ThenBy(entry => entry.RunId, StringComparer.Ordinal)
                .ToArray();

            return Task.FromResult<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>(entries);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>> ListUnfinishedAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entries = _entries
                .Values
                .Where(entry =>
                    IsUnfinished(entry) &&
                    BelongsToCurrentTenant(entry))
                .OrderBy(entry => entry.CreatedAtUtc)
                .ThenBy(entry => entry.RunId, StringComparer.Ordinal)
                .ToArray();

            return Task.FromResult<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>(entries);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>> ListRecoverableByRuntimeInstanceAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            var entries = _entries
                .Values
                .Where(entry =>
                    string.Equals(
                        entry.RuntimeInstanceId,
                        runtimeInstanceId,
                        StringComparison.Ordinal) &&
                    IsRecoverable(entry) &&
                    BelongsToCurrentTenant(entry))
                .OrderBy(entry => entry.CreatedAtUtc)
                .ThenBy(entry => entry.RunId, StringComparer.Ordinal)
                .ToArray();

            return Task.FromResult<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>(entries);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>> ListRecoverableAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entries = _entries
                .Values
                .Where(entry =>
                    IsRecoverable(entry) &&
                    BelongsToCurrentTenant(entry))
                .OrderBy(entry => entry.CreatedAtUtc)
                .ThenBy(entry => entry.RunId, StringComparer.Ordinal)
                .ToArray();

            return Task.FromResult<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>(entries);
        }

        /// <inheritdoc />
        public Task<AiRuntimeRunExecutionIndexEntry?> GetAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);

            cancellationToken.ThrowIfCancellationRequested();

            if (!_entries.TryGetValue(
                    runId,
                    out var entry))
            {
                return Task.FromResult<AiRuntimeRunExecutionIndexEntry?>(null);
            }

            return Task.FromResult(
                BelongsToCurrentTenant(entry)
                    ? entry
                    : null);
        }

        /// <summary>
        /// Determines whether an entry can transition to requeued-for-recovery.
        /// </summary>
        /// <param name="existing">The existing runtime run execution index entry.</param>
        /// <param name="executionId">The execution identifier requested by recovery.</param>
        /// <returns>True when the transition is allowed; otherwise false.</returns>
        private static bool CanTransitionToRequeuedForRecovery(
            AiRuntimeRunExecutionIndexEntry existing,
            string executionId)
        {
            if (string.Equals(existing.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(existing.Status, StatusCancelled, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(existing.Status, StatusRequeuedForRecovery, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(existing.ExecutionId))
            {
                return true;
            }

            return string.Equals(
                existing.ExecutionId,
                executionId,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Determines whether an index entry has not reached a terminal runtime-run state.
        /// </summary>
        /// <param name="entry">The runtime run index entry.</param>
        /// <returns>True when the entry is unfinished; otherwise false.</returns>
        private static bool IsUnfinished(
            AiRuntimeRunExecutionIndexEntry entry)
        {
            return !IsTerminal(entry);
        }

        /// <summary>
        /// Determines whether an index entry is recoverable by runtime crash recovery.
        /// </summary>
        /// <param name="entry">The runtime run index entry.</param>
        /// <returns>True when the entry can be considered by crash recovery; otherwise false.</returns>
        private static bool IsRecoverable(
            AiRuntimeRunExecutionIndexEntry entry)
        {
            return !string.Equals(entry.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(entry.Status, StatusCancelled, StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(entry.Status, StatusRequeuedForRecovery, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines whether an index entry has reached a terminal runtime-run state.
        /// </summary>
        /// <param name="entry">The runtime run index entry.</param>
        /// <returns>True when the entry is terminal; otherwise false.</returns>
        private static bool IsTerminal(
            AiRuntimeRunExecutionIndexEntry entry)
        {
            return string.Equals(entry.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(entry.Status, StatusFailed, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(entry.Status, StatusCancelled, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(entry.Status, StatusRequeuedForRecovery, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Attempts to resolve the currently active execution context snapshot.
        /// </summary>
        /// <returns>The active snapshot, or null when no execution context is active.</returns>
        private ExecutionContextSnapshot? TryResolveSnapshot()
        {
            if (_executionContextSnapshotProvider is null)
            {
                return null;
            }

            try
            {
                return _executionContextSnapshotProvider.MapToSnapshot();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>
        /// Determines whether an entry is visible from the currently active tenant context.
        /// </summary>
        /// <param name="entry">The runtime run index entry.</param>
        /// <returns>True when the entry is visible; otherwise false.</returns>
        private bool BelongsToCurrentTenant(
            AiRuntimeRunExecutionIndexEntry entry)
        {
            var currentSnapshot =
                TryResolveSnapshot();

            if (currentSnapshot is null ||
                string.IsNullOrWhiteSpace(currentSnapshot.TenantId))
            {
                return true;
            }

            var entryTenantId =
                entry.ExecutionContextSnapshot?.TenantId;

            if (string.IsNullOrWhiteSpace(entryTenantId))
            {
                return false;
            }

            return string.Equals(
                NormalizeTenantId(entryTenantId),
                NormalizeTenantId(currentSnapshot.TenantId),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Normalizes a tenant identifier for defensive in-memory comparisons.
        /// </summary>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <returns>The normalized tenant identifier.</returns>
        private static string NormalizeTenantId(
            string tenantId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

            return tenantId
                .Trim()
                .Replace(" ", "-", StringComparison.Ordinal)
                .Replace("\\", "/", StringComparison.Ordinal);
        }
    }
}