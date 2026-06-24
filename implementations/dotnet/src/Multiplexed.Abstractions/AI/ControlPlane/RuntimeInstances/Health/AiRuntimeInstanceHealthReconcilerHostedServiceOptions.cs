namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Health
{
    /// <summary>
    /// Options controlling the runtime instance health reconciler hosted service.
    /// </summary>
    public sealed class AiRuntimeInstanceHealthReconcilerHostedServiceOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether the hosted health reconciliation loop is enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets the reconciliation interval.
        /// </summary>
        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Gets or sets the delay used after a reconciliation failure.
        /// </summary>
        public TimeSpan ErrorDelay { get; set; } = TimeSpan.FromSeconds(5);
    }
}