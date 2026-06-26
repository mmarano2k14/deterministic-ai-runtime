namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Provides runtime recovery forensics configuration.
    /// </summary>
    public sealed class AiRuntimeRecoveryForensicsOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether runtime recovery forensics is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether forensics persistence failures should fail the caller.
        /// </summary>
        public bool StrictPersistence { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of events kept per recovery record.
        /// </summary>
        public int MaxEventsPerRecord { get; set; } = 500;
    }
}