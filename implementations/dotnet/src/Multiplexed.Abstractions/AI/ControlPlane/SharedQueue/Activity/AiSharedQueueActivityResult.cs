using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;

namespace Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Activity
{
    /// <summary>
    /// Represents recent shared queue activity.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Exposes recent shared run records for MCP tools, dashboards,
    ///   Kubernetes diagnostics, and operational visibility.
    /// - Provides a history-oriented view that remains useful even when the
    ///   active shared queue has already been drained by a fast background pump.
    /// </remarks>
    public sealed class AiSharedQueueActivityResult
    {
        /// <summary>
        /// Gets or sets the recent shared run activity records.
        /// </summary>
        public IReadOnlyList<AiSharedRunRecord> Runs { get; set; } =
            Array.Empty<AiSharedRunRecord>();

        /// <summary>
        /// Gets or sets the total number of returned records.
        /// </summary>
        public int Count { get; set; }

        /// <summary>
        /// Gets or sets the UTC timestamp when the activity snapshot was created.
        /// </summary>
        public DateTimeOffset SnapshotAtUtc { get; set; } =
            DateTimeOffset.UtcNow;
    }
}