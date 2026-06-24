namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Health
{
    /// <summary>
    /// Represents the result of a runtime instance health reconciliation pass.
    /// </summary>
    public sealed class AiRuntimeInstanceHealthReconciliationResult
    {
        /// <summary>
        /// Gets or sets the number of scanned runtime instances.
        /// </summary>
        public int ScannedCount { get; set; }

        /// <summary>
        /// Gets or sets the number of runtime instances marked unhealthy.
        /// </summary>
        public int MarkedUnhealthyCount { get; set; }

        /// <summary>
        /// Gets or sets the number of ignored runtime instances.
        /// </summary>
        public int IgnoredCount { get; set; }

        /// <summary>
        /// Gets or sets the health reconciliation decisions.
        /// </summary>
        public IReadOnlyList<AiRuntimeInstanceHealthDecision> Decisions { get; set; } =
            Array.Empty<AiRuntimeInstanceHealthDecision>();
    }
}