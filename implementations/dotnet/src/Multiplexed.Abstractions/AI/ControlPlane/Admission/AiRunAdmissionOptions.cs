namespace Multiplexed.Abstractions.AI.ControlPlane.Admission
{
    /// <summary>
    /// Defines policy options for run admission decisions.
    ///
    /// Admission options control only the control-plane decision layer.
    /// They do not modify local queues, workers, runtime instances, or DAG execution logic.
    /// </summary>
    public sealed class AiRunAdmissionOptions
    {
        /// <summary>
        /// Enables run admission decisions.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Maximum number of runtime instances allowed by admission policy.
        ///
        /// When no available instance exists and the current instance count is below this limit,
        /// admission may return RequestScaleOut.
        /// </summary>
        public int? MaxInstanceCount { get; set; }

        /// <summary>
        /// Indicates whether admission may request scale-out when no instance can accept a run.
        /// </summary>
        public bool EnableScaleOutRequest { get; set; } = true;

        /// <summary>
        /// Indicates whether admission may keep a run pending in a future shared/global queue.
        /// </summary>
        public bool EnableGlobalQueueFallback { get; set; } = true;

        /// <summary>
        /// Indicates whether admission should reject runs when no instance is available
        /// and neither scale-out nor global queue fallback can be used.
        /// </summary>
        public bool RejectWhenNoCapacity { get; set; } = true;

        /// <summary>
        /// Indicates whether paused runtime instances may receive new runs.
        /// </summary>
        public bool AllowPausedInstances { get; set; }

        /// <summary>
        /// Indicates whether draining runtime instances may receive new runs.
        /// </summary>
        public bool AllowDrainingInstances { get; set; }

        /// <summary>
        /// Indicates whether unhealthy runtime instances may receive new runs.
        /// </summary>
        public bool AllowUnhealthyInstances { get; set; }

        /// <summary>
        /// Indicates whether a preferred runtime instance should be selected first when available.
        /// </summary>
        public bool PreferRequestedRuntimeInstance { get; set; } = true;

        /// <summary>
        /// Enables controller-level duration measurement.
        ///
        /// This is useful for future Grafana metrics and control-plane diagnostics.
        /// </summary>
        public bool MeasureDuration { get; set; } = true;
    }
}