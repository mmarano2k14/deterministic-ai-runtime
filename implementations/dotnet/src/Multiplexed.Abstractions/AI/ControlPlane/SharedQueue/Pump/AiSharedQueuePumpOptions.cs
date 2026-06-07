namespace Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump
{
    /// <summary>
    /// Defines execution options for a shared queue pump cycle.
    /// </summary>
    public sealed class AiSharedQueuePumpOptions
    {
        /// <summary>
        /// Enables the shared queue pump.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Maximum number of queue items to dispatch in a single pump cycle.
        /// </summary>
        public int MaxDispatchesPerCycle { get; init; } = 10;

        /// <summary>
        /// Default claim TTL used when the pump claims queue items.
        /// </summary>
        public TimeSpan DefaultClaimTtl { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Indicates whether the pump should stop the current cycle when no item is available.
        /// </summary>
        public bool StopCycleWhenNoItemAvailable { get; init; } = true;

        /// <summary>
        /// Indicates whether the pump should stop the current cycle on first dispatch failure.
        /// </summary>
        public bool StopCycleOnDispatchFailure { get; init; }

        /// <summary>
        /// Optional worker id used by the pump.
        /// </summary>
        public string? WorkerId { get; init; }

        /// <summary>
        /// Optional source label used for observability.
        /// </summary>
        public string Source { get; init; } = "shared-queue-pump";
    }
}