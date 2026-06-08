namespace Multiplexed.Abstractions.AI.ControlPlane.ExecutionAssistance
{
    /// <summary>
    /// Defines configuration options for cross-instance execution assistance.
    /// </summary>
    /// <remarks>
    /// Execution assistance is intended as a bounded fallback mechanism for
    /// under-provisioned executions. It should not blindly attach all idle runtime
    /// instances to the same execution because that can increase contention on the
    /// same execution state.
    /// </remarks>
    public sealed class AiExecutionAssistanceOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether execution assistance is enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of helper runtime instances allowed per execution.
        /// </summary>
        public int MaxHelpersPerExecution { get; set; } = 1;

        /// <summary>
        /// Gets or sets the maximum number of workers allowed to work on the same execution.
        /// </summary>
        public int MaxWorkersPerExecution { get; set; } = 5;

        /// <summary>
        /// Gets or sets the maximum number of helper workers allowed per helper runtime instance.
        /// </summary>
        public int MaxWorkersPerHelperInstance { get; set; } = 1;

        /// <summary>
        /// Gets or sets the minimum number of ready steps required before assistance can be granted.
        /// </summary>
        public int MinReadyStepsToAssist { get; set; } = 10;

        /// <summary>
        /// Gets or sets the minimum number of remaining non-terminal steps required before assistance can be granted.
        /// </summary>
        public int MinRemainingStepsToAssist { get; set; } = 100;

        /// <summary>
        /// Gets or sets a value indicating whether helpers may assist only when their local queue is idle.
        /// </summary>
        public bool OnlyWhenLocalQueueIdle { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum local queue depth allowed for a helper runtime instance.
        /// </summary>
        public int MaxHelperQueueDepth { get; set; }

        /// <summary>
        /// Gets or sets the assistance lease time-to-live.
        /// </summary>
        public TimeSpan LeaseTtl { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the interval between automatic execution assistance evaluations.
        /// </summary>
        public TimeSpan EvaluationInterval { get; set; } = TimeSpan.FromSeconds(2);
    }
}