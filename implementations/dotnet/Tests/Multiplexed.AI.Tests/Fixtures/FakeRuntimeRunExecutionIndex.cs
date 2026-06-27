using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Fake runtime run execution index used by transition service and reconciler tests.
    /// </summary>
    public sealed class FakeRuntimeRunExecutionIndex : IAiRuntimeRunExecutionIndex
    {
        /// <summary>
        /// Gets or sets a value indicating whether requeue-for-recovery should be accepted.
        /// </summary>
        public bool MarkRequeuedForRecoveryResult { get; set; } = true;

        /// <summary>
        /// Gets the number of recovery requeue index transitions.
        /// </summary>
        public int MarkRequeuedForRecoveryCalls { get; private set; }

        /// <summary>
        /// Gets the last requeued local run identifier.
        /// </summary>
        public string? LastRequeuedRunId { get; private set; }

        /// <summary>
        /// Gets the last requeued durable execution identifier.
        /// </summary>
        public string? LastRequeuedExecutionId { get; private set; }

        /// <summary>
        /// Gets the last requeue recovery reason.
        /// </summary>
        public string? LastRequeuedReason { get; private set; }

        /// <summary>
        /// Gets the registered queued entries.
        /// </summary>
        public List<AiRuntimeRunExecutionIndexEntry> RegisteredEntries { get; } = [];

        /// <summary>
        /// Gets unfinished runs returned by runtime-instance recovery scans.
        /// </summary>
        public List<AiRuntimeRunExecutionIndexEntry> UnfinishedRuns { get; } = [];

        /// <inheritdoc />
        public Task RegisterQueuedAsync(
            AiRuntimeRunExecutionIndexEntry entry,
            CancellationToken cancellationToken = default)
        {
            RegisteredEntries.Add(entry);

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task MarkStartedAsync(
            string runId,
            string executionId,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task MarkCompletedAsync(
            string runId,
            string executionId,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task MarkFailedAsync(
            string runId,
            string? executionId,
            string failureReason,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task MarkCancelledAsync(
            string runId,
            string? executionId,
            string? reason,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<bool> MarkRequeuedForRecoveryAsync(
            string runId,
            string executionId,
            string reason,
            CancellationToken cancellationToken = default)
        {
            MarkRequeuedForRecoveryCalls++;
            LastRequeuedRunId = runId;
            LastRequeuedExecutionId = executionId;
            LastRequeuedReason = reason;

            return Task.FromResult(MarkRequeuedForRecoveryResult);
        }

        /// <inheritdoc />
        public Task<AiRuntimeRunExecutionIndexEntry?> GetAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            var entry = RegisteredEntries
                .Concat(UnfinishedRuns)
                .FirstOrDefault(x => string.Equals(
                    x.RunId,
                    runId,
                    StringComparison.OrdinalIgnoreCase));

            return Task.FromResult<AiRuntimeRunExecutionIndexEntry?>(entry);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>> ListUnfinishedByRuntimeInstanceAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            var matches = UnfinishedRuns
                .Where(x => string.Equals(
                    x.RuntimeInstanceId,
                    runtimeInstanceId,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            return Task.FromResult<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>(matches);
        }
    }
}