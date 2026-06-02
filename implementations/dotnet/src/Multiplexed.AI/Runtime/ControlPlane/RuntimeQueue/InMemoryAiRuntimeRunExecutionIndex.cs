using System.Collections.Concurrent;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue
{
    /// <summary>
    /// In-memory runtime run execution index.
    /// </summary>
    public sealed class InMemoryAiRuntimeRunExecutionIndex : IAiRuntimeRunExecutionIndex
    {
        private readonly ConcurrentDictionary<string, AiRuntimeRunExecutionIndexEntry> _entries =
            new(StringComparer.Ordinal);

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
                Metadata = entry.Metadata
            };

            return Task.CompletedTask;
        }

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
                    StartedAtUtc = now
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
                    Metadata = existing.Metadata
                });

            return Task.CompletedTask;
        }

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
                    CompletedAtUtc = now
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
                    Metadata = existing.Metadata
                });

            return Task.CompletedTask;
        }

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
                    CompletedAtUtc = now
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
                    Metadata = existing.Metadata
                });

            return Task.CompletedTask;
        }

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
                    CompletedAtUtc = now
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
                    Metadata = existing.Metadata
                });

            return Task.CompletedTask;
        }

        public Task<AiRuntimeRunExecutionIndexEntry?> GetAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);

            cancellationToken.ThrowIfCancellationRequested();

            _entries.TryGetValue(
                runId,
                out var entry);

            return Task.FromResult(entry);
        }
    }
}