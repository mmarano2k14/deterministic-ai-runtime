using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Fake runtime run execution index used by transition service and reconciler tests.
    /// </summary>
    public sealed class FakeRuntimeRunExecutionIndex : RuntimeRunExecutionIndexTestFixture
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

        /// <summary>
        /// Gets recoverable runs returned by runtime-instance recovery scans.
        /// </summary>
        public List<AiRuntimeRunExecutionIndexEntry> RecoverableRuns { get; } = [];

        /// <inheritdoc />
        public override Task RegisterQueuedAsync(
            AiRuntimeRunExecutionIndexEntry entry,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);

            cancellationToken.ThrowIfCancellationRequested();

            this.RegisteredEntries.Add(entry);

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override Task<bool> TryRegisterQueuedAsync(
            AiRuntimeRunExecutionIndexEntry entry,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);
            cancellationToken.ThrowIfCancellationRequested();

            if (this.RegisteredEntries.Any(existing =>
                    string.Equals(
                        existing.RunId,
                        entry.RunId,
                        StringComparison.Ordinal)))
            {
                return Task.FromResult(false);
            }

            this.RegisteredEntries.Add(entry);
            return Task.FromResult(true);
        }

        /// <inheritdoc />
        public override Task MarkStartedAsync(
            string runId,
            string executionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override Task MarkWaitingAsync(
            string runId,
            string executionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override Task MarkCompletedAsync(
            string runId,
            string executionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override Task MarkFailedAsync(
            string runId,
            string? executionId,
            string failureReason,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override Task MarkCancelledAsync(
            string runId,
            string? executionId,
            string? reason,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override Task<bool> MarkRequeuedForRecoveryAsync(
            string runId,
            string executionId,
            string reason,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            this.MarkRequeuedForRecoveryCalls++;
            this.LastRequeuedRunId = runId;
            this.LastRequeuedExecutionId = executionId;
            this.LastRequeuedReason = reason;

            return Task.FromResult(this.MarkRequeuedForRecoveryResult);
        }

        /// <inheritdoc />
        public override Task<AiRuntimeRunExecutionIndexEntry?> GetAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = this.RegisteredEntries
                .Concat(this.UnfinishedRuns)
                .Concat(this.RecoverableRuns)
                .FirstOrDefault(x => string.Equals(
                    x.RunId,
                    runId,
                    StringComparison.OrdinalIgnoreCase));

            return Task.FromResult<AiRuntimeRunExecutionIndexEntry?>(entry);
        }

        /// <inheritdoc />
        public override Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>> ListUnfinishedByRuntimeInstanceAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var matches = this.UnfinishedRuns
                .Where(x => string.Equals(
                    x.RuntimeInstanceId,
                    runtimeInstanceId,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.CreatedAtUtc)
                .ThenBy(entry => entry.RunId, StringComparer.Ordinal)
                .ToList();

            return Task.FromResult<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>(matches);
        }

        /// <inheritdoc />
        public override Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>> ListUnfinishedAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var matches = this.UnfinishedRuns
                .OrderBy(entry => entry.CreatedAtUtc)
                .ThenBy(entry => entry.RunId, StringComparer.Ordinal)
                .ToList();

            return Task.FromResult<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>(matches);
        }

        /// <inheritdoc />
        public override Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>> ListRecoverableByRuntimeInstanceAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var source =
                this.RecoverableRuns.Count > 0
                    ? this.RecoverableRuns
                    : this.UnfinishedRuns;

            var matches = source
                .Where(x => string.Equals(
                    x.RuntimeInstanceId,
                    runtimeInstanceId,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.CreatedAtUtc)
                .ThenBy(entry => entry.RunId, StringComparer.Ordinal)
                .ToList();

            return Task.FromResult<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>(matches);
        }

        /// <inheritdoc />
        public override Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>> ListRecoverableAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var source =
                this.RecoverableRuns.Count > 0
                    ? this.RecoverableRuns
                    : this.UnfinishedRuns;

            var matches = source
                .OrderBy(entry => entry.CreatedAtUtc)
                .ThenBy(entry => entry.RunId, StringComparer.Ordinal)
                .ToList();

            return Task.FromResult<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>(matches);
        }
    }
}