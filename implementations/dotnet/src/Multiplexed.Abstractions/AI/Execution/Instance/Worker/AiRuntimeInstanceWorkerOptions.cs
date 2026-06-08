namespace Multiplexed.AI.Runtime.Execution.Instance.Worker
{
    /// <summary>
    /// Configures runtime instance worker execution behavior.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These options control how a runtime instance worker advances an execution
    /// by repeatedly invoking batch execution cycles until the execution reaches
    /// a terminal state or cancellation is requested.
    /// </para>
    /// <para>
    /// When multiple runtime instance workers participate in the same distributed
    /// DAG execution, the underlying Redis-backed runtime coordination layer remains
    /// responsible for atomic claims, claim tokens, retry eligibility, throttling,
    /// lease expiration, and deterministic convergence.
    /// </para>
    /// </remarks>
    public sealed class AiRuntimeInstanceWorkerOptions
    {
        /// <summary>
        /// Gets or sets the maximum number of DAG steps this worker asks the engine
        /// to advance during one worker cycle.
        /// </summary>
        /// <remarks>
        /// This is not a concurrency limit. It only controls batch acquisition size.
        /// Actual execution admission remains controlled by the runtime concurrency
        /// engine and distributed throttling policies.
        /// </remarks>
        public int MaxStepsPerCycle { get; set; } = 4;

        /// <summary>
        /// Gets or sets the delay applied between non-terminal worker cycles.
        /// </summary>
        public TimeSpan IdleDelay { get; set; } = TimeSpan.FromMilliseconds(25);

        /// <summary>
        /// Gets or sets the maximum number of worker cycles before the worker stops.
        /// A value of 0 means unlimited cycles.
        /// </summary>
        public int MaxCycles { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether concurrency conflict exceptions
        /// should be treated as expected distributed race losses.
        /// </summary>
        public bool IgnoreConcurrencyConflicts { get; set; } = true;

        
    }
}