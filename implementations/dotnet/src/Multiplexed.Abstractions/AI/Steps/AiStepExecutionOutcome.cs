namespace Multiplexed.Abstractions.AI.Steps
{
    /// <summary>
    /// Represents the orchestration outcome produced by one physical step execution attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This outcome is distinct from durable step lifecycle state. The DAG runner translates
    /// the returned outcome into the appropriate atomic durable transition.
    /// </para>
    /// <para>
    /// <see cref="Park"/> is a voluntary non-terminal suspension. It must not consume retry
    /// or infrastructure recovery budget.
    /// </para>
    /// </remarks>
    public enum AiStepExecutionOutcome
    {
        /// <summary>
        /// The step completed successfully.
        /// </summary>
        Complete = 0,

        /// <summary>
        /// The step failed and must be processed by the existing failure/retry path.
        /// </summary>
        Fail = 1,

        /// <summary>
        /// The step durably prepared an external wait and must release its current claim.
        /// </summary>
        Park = 2
    }
}
