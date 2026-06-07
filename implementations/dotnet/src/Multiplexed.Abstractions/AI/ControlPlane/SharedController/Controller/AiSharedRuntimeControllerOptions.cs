namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller
{
    /// <summary>
    /// Defines options for the shared runtime controller facade.
    /// </summary>
    /// <remarks>
    /// These options control only the shared controller layer.
    /// They do not modify local queues, runtime instances, DAG execution,
    /// Redis coordination, or Kubernetes infrastructure.
    /// </remarks>
    public sealed class AiSharedRuntimeControllerOptions
    {
        /// <summary>
        /// Enables shared run submission.
        /// </summary>
        public bool EnableSubmitRun { get; init; } = true;

        /// <summary>
        /// Enables shared run lookup by shared run id.
        /// </summary>
        public bool EnableGetRun { get; init; } = true;

        /// <summary>
        /// Enables listing shared runs known by the controller.
        /// </summary>
        public bool EnableListRuns { get; init; } = true;

        /// <summary>
        /// Enables shared run cancellation.
        /// </summary>
        public bool EnableCancelRun { get; init; } = true;

        /// <summary>
        /// Indicates whether the shared controller should dispatch immediately
        /// when admission returns AssignToInstance.
        /// </summary>
        /// <remarks>
        /// V1 should normally keep this disabled until remote/local dispatch is implemented.
        /// </remarks>
        public bool EnableImmediateDispatch { get; init; }

        /// <summary>
        /// Indicates whether the shared controller may keep runs in a future shared/global queue
        /// when admission returns QueueGlobally.
        /// </summary>
        /// <remarks>
        /// V1 records the status but does not yet persist to a Redis-backed shared queue.
        /// </remarks>
        public bool EnableSharedQueueFallback { get; init; } = true;

        /// <summary>
        /// Indicates whether the shared controller may surface scale-out requested decisions.
        /// </summary>
        /// <remarks>
        /// V1 records the status but does not yet call a Kubernetes scaler.
        /// </remarks>
        public bool EnableScaleOutRequest { get; init; } = true;

        /// <summary>
        /// When enabled, expected operational failures should be returned as
        /// structured failed results instead of being thrown to the caller.
        /// </summary>
        public bool ReturnFailureResultInsteadOfThrowing { get; init; } = true;

        /// <summary>
        /// Enables controller-level duration measurement.
        ///
        /// This is useful for future Grafana metrics and control-plane diagnostics.
        /// </summary>
        public bool MeasureDuration { get; init; } = true;

        /// <summary>
        /// Defines how submitted runs are handled by the shared controller.
        /// </summary>
        public AiSharedRuntimeSubmitMode SubmitMode { get; set; } =
            AiSharedRuntimeSubmitMode.DirectDispatch;
    }
}