namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Defines options for the simulated runtime scale-out provider.
    /// </summary>
    public sealed class SimulatedAiRuntimeScaleOutProviderOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether the simulated provider should succeed.
        /// </summary>
        public bool Succeed { get; set; } = true;

        /// <summary>
        /// Gets or sets the runtime instance id prefix returned by the simulated provider.
        /// </summary>
        public string RuntimeInstanceIdPrefix { get; set; } = "simulated-runtime";

        /// <summary>
        /// Gets or sets an optional simulated provider operation delay.
        /// </summary>
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        /// <summary>
        /// Gets or sets the failure reason returned when simulation is configured to fail.
        /// </summary>
        public string FailureReason { get; set; } = "Simulated scale-out provider failure.";
    }
}