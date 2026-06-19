using Multiplexed.Abstractions.Core.ExecutionContext;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Provides a fixed execution context snapshot for tests.
    /// </summary>
    public sealed class FakeExecutionContextSnapshotProvider :
        IExecutionContextSnapshotProvider
    {
        private readonly ExecutionContextSnapshot snapshot;

        /// <summary>
        /// Initializes a new instance of the <see cref="FakeExecutionContextSnapshotProvider"/> class.
        /// </summary>
        /// <param name="snapshot">The snapshot to return.</param>
        public FakeExecutionContextSnapshotProvider(
            ExecutionContextSnapshot snapshot)
        {
            this.snapshot = snapshot
                ?? throw new ArgumentNullException(nameof(snapshot));
        }

        /// <inheritdoc />
        public ExecutionContextSnapshot MapToSnapshot()
        {
            return snapshot;
        }
    }
}