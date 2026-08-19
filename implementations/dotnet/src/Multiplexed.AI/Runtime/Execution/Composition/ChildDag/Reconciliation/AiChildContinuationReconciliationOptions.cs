namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Reconciliation
{
    /// <summary>
    /// Configures the durable child-completion and parent-continuation reconciliation loop.
    /// </summary>
    public sealed class AiChildContinuationReconciliationOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether the reconciliation loop is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the interval between reconciliation iterations.
        /// </summary>
        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Gets or sets the maximum number of relations processed from each durable query family per iteration.
        /// </summary>
        public int BatchSize { get; set; } = 100;
    }
}
