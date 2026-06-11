namespace Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Background
{
    /// <summary>
    /// Defines options for the shared queue background service.
    /// </summary>
    /// <remarks>
    /// These options control the hosted service responsible for periodically
    /// pumping the shared queue into a runtime instance.
    ///
    /// The readiness options are intentionally kept here because readiness waiting
    /// is a background-service startup concern, not a Redis store concern and not
    /// a dispatcher concern.
    /// </remarks>
    public sealed class AiSharedQueueBackgroundServiceOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether the shared queue background service is enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets the runtime instance identifier used by this service
        /// when claiming shared queue items.
        /// </summary>
        /// <remarks>
        /// When not provided, the background service derives a runtime instance
        /// identifier from the machine name and process identifier.
        /// </remarks>
        public string? RuntimeInstanceId { get; set; }

        /// <summary>
        /// Gets or sets the worker identifier used by this service.
        /// </summary>
        /// <remarks>
        /// When not provided, the background service derives a worker identifier
        /// from the resolved runtime instance identifier.
        /// </remarks>
        public string? WorkerId { get; set; }

        /// <summary>
        /// Gets or sets the optional tenant filter used when claiming shared queue items.
        /// </summary>
        public string? TenantId { get; set; }

        /// <summary>
        /// Gets or sets the optional pipeline filter used when claiming shared queue items.
        /// </summary>
        public string? PipelineKey { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of dispatch attempts performed per pump cycle.
        /// </summary>
        public int MaxDispatchesPerCycle { get; set; } = 10;

        /// <summary>
        /// Gets or sets the claim time-to-live used while dispatching shared queue items.
        /// </summary>
        /// <remarks>
        /// This value protects pending queue items from being permanently claimed
        /// by a runtime instance that crashes during dispatch.
        /// </remarks>
        public TimeSpan ClaimTtl { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the delay between pump cycles when the previous cycle
        /// completed without dispatching any item.
        /// </summary>
        public TimeSpan IdleDelay { get; set; } = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Gets or sets the delay between pump cycles when the previous cycle
        /// completed with at least one successful dispatch.
        /// </summary>
        public TimeSpan ActiveDelay { get; set; } = TimeSpan.FromMilliseconds(25);

        /// <summary>
        /// Gets or sets the delay applied after an unexpected pump failure.
        /// </summary>
        public TimeSpan ErrorDelay { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Gets or sets a value indicating whether the background service should wait
        /// for its runtime instance registration and capacity descriptor to become visible
        /// before starting the shared queue pump loop.
        /// </summary>
        /// <remarks>
        /// This prevents the pump from claiming shared queue items before the runtime
        /// instance has published its registry heartbeat and capacity information.
        /// </remarks>
        public bool WaitForRuntimeReadiness { get; set; } = true;

        /// <summary>
        /// Gets or sets the delay between runtime readiness checks.
        /// </summary>
        /// <remarks>
        /// Values less than or equal to zero should be treated by the caller as a small
        /// safe delay.
        /// </remarks>
        public TimeSpan RuntimeReadinessPollInterval { get; set; } =
            TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Gets or sets the maximum amount of time to wait for runtime readiness
        /// before failing the background service startup.
        /// </summary>
        /// <remarks>
        /// When set to <c>null</c>, the background service waits indefinitely until
        /// cancellation is requested.
        /// </remarks>
        public TimeSpan? RuntimeReadinessTimeout { get; set; } =
            TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the optional source label used for diagnostics and observability.
        /// </summary>
        public string Source { get; set; } = "shared-queue-background-service";

        /// <summary>
        /// Gets or sets the optional requester identity used for diagnostics and observability.
        /// </summary>
        public string RequestedBy { get; set; } = "system";

        /// <summary>
        /// Gets or sets optional metadata propagated to shared queue pump requests.
        /// </summary>
        /// <remarks>
        /// The background service may enrich this metadata with runtime-scoped values,
        /// such as the logical control-plane identifier.
        /// </remarks>
        public IReadOnlyDictionary<string, string> Metadata { get; set; } =
            new Dictionary<string, string>();
    }
}