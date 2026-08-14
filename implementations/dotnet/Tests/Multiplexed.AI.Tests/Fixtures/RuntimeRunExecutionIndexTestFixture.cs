using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Centralizes the test-only runtime-run index contract so interface evolution
    /// does not require duplicating compatibility methods across local test doubles.
    /// </summary>
    public abstract class RuntimeRunExecutionIndexTestFixture :
        IAiRuntimeRunExecutionIndex
    {
        /// <inheritdoc />
        public abstract Task RegisterQueuedAsync(
            AiRuntimeRunExecutionIndexEntry entry,
            CancellationToken cancellationToken = default);

        /// <inheritdoc />
        public virtual async Task<bool> TryRegisterQueuedAsync(
            AiRuntimeRunExecutionIndexEntry entry,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(entry);
            cancellationToken.ThrowIfCancellationRequested();

            await this.RegisterQueuedAsync(
                    entry,
                    cancellationToken)
                .ConfigureAwait(false);

            return true;
        }

        /// <inheritdoc />
        public abstract Task MarkStartedAsync(
            string runId,
            string executionId,
            CancellationToken cancellationToken = default);

        /// <inheritdoc />
        public abstract Task MarkCompletedAsync(
            string runId,
            string executionId,
            CancellationToken cancellationToken = default);

        /// <inheritdoc />
        public abstract Task MarkFailedAsync(
            string runId,
            string? executionId,
            string failureReason,
            CancellationToken cancellationToken = default);

        /// <inheritdoc />
        public abstract Task MarkCancelledAsync(
            string runId,
            string? executionId,
            string? reason,
            CancellationToken cancellationToken = default);

        /// <inheritdoc />
        public abstract Task<bool> MarkRequeuedForRecoveryAsync(
            string runId,
            string executionId,
            string reason,
            CancellationToken cancellationToken = default);

        /// <inheritdoc />
        public abstract Task<AiRuntimeRunExecutionIndexEntry?> GetAsync(
            string runId,
            CancellationToken cancellationToken = default);

        /// <inheritdoc />
        public abstract Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>
            ListUnfinishedByRuntimeInstanceAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default);

        /// <inheritdoc />
        public abstract Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>
            ListUnfinishedAsync(
                CancellationToken cancellationToken = default);

        /// <inheritdoc />
        public abstract Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>
            ListRecoverableByRuntimeInstanceAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default);

        /// <inheritdoc />
        public abstract Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>
            ListRecoverableAsync(
                CancellationToken cancellationToken = default);
    }
}
