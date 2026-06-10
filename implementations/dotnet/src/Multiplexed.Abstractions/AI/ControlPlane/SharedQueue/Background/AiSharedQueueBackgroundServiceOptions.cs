namespace Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Background
{
    /// <summary>
    /// Defines options for the shared queue background service.
    /// </summary>
    public sealed class AiSharedQueueBackgroundServiceOptions
    {
        /// <summary>
        /// Enables the shared queue background service.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Runtime instance id used by this service when claiming shared queue items.
        /// </summary>
        public string? RuntimeInstanceId { get; set; }

        /// <summary>
        /// Worker id used by this service.
        /// </summary>
        public string? WorkerId { get; set; }

        /// <summary>
        /// Optional tenant filter.
        /// </summary>
        public string? TenantId { get; set; }

        /// <summary>
        /// Optional pipeline filter.
        /// </summary>
        public string? PipelineKey { get; set; }

        /// <summary>
        /// Maximum number of dispatches per pump cycle.
        /// </summary>
        public int MaxDispatchesPerCycle { get; set; } = 10;

        /// <summary>
        /// Claim TTL used while dispatching queue items.
        /// </summary>
        public TimeSpan ClaimTtl { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Delay between pump cycles when the previous cycle completed normally.
        /// </summary>
        public TimeSpan IdleDelay { get; set; } = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Delay after a cycle with at least one successful dispatch.
        /// </summary>
        public TimeSpan ActiveDelay { get; set; } = TimeSpan.FromMilliseconds(25);

        /// <summary>
        /// Delay after an unexpected pump failure.
        /// </summary>
        public TimeSpan ErrorDelay { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Gets or sets a value indicating whether the shared queue pump should wait
        /// for at least one ready runtime instance and matching capacity descriptor before starting.
        /// </summary>
        public bool WaitForRuntimeReadiness { get; set; } = true;

        /// <summary>
        /// Gets or sets the delay between runtime readiness checks.
        /// </summary>
        public TimeSpan RuntimeReadinessPollInterval { get; set; } =
            TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Gets or sets the maximum amount of time to wait for runtime readiness before continuing.
        /// </summary>
        /// <remarks>
        /// When null, the pump waits indefinitely until cancellation.
        /// </remarks>
        public TimeSpan? RuntimeReadinessTimeout { get; set; } =
            TimeSpan.FromSeconds(30);

        /// <summary>
        /// Optional source label used for diagnostics and observability.
        /// </summary>
        public string Source { get; set; } = "shared-queue-background-service";

        /// <summary>
        /// Optional requester identity.
        /// </summary>
        public string RequestedBy { get; set; } = "system";

        /// <summary>
        /// Optional metadata propagated to pump requests.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; set; } =
            new Dictionary<string, string>();
    }
}