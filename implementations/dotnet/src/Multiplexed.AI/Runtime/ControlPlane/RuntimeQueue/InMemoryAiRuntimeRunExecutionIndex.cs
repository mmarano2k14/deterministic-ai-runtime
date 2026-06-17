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
                Status = string.IsNullOrWhiteSpace(entry.Status) ? "queued" : entry.Status,
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
                    Status = "running",
                    CreatedAtUtc = now,
                    StartedAtUtc = now,
                    ExecutionContextSnapshot = TryResolveSnapshot()
                },
                (_, existing) => new AiRuntimeRunExecutionIndexEntry
                {
                    RunId = existing.RunId,
                    ExecutionId = executionId,
                    RuntimeInstanceId = existing.RuntimeInstanceId,
                    Status = "running",
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
                    Status = "completed",
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
                    Status = "completed",
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
                    Status = "failed",
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
                    Status = "failed",
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
                    Status = "cancelled",
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
                    Status = "cancelled",
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
