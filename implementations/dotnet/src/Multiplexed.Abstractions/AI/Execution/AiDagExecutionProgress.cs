namespace Multiplexed.Abstractions.AI.Execution
{
    /// <summary>
    /// Represents a lightweight authoritative progress snapshot for a DAG execution.
    /// </summary>
    public sealed class AiDagExecutionProgress
    {
        /// <summary>
        /// Gets or sets the durable execution identifier.
        /// </summary>
        public string ExecutionId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the authoritative global execution status.
        /// </summary>
        public AiExecutionStatus Status { get; set; }

        /// <summary>
        /// Gets or sets the number of distributed steps currently completed.
        /// </summary>
        public int CompletedStepCount { get; set; }

        /// <summary>
        /// Gets or sets the total configured step count.
        /// </summary>
        public int TotalStepCount { get; set; }

        /// <summary>
        /// Gets or sets the distributed step status counts.
        /// </summary>
        public IReadOnlyDictionary<AiStepExecutionStatus, int> StatusCounts { get; set; } =
            new Dictionary<AiStepExecutionStatus, int>();
    }
}