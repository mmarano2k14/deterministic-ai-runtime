namespace Multiplexed.AI.Abstractions.AI.Policies
{
    /// <summary>
    /// Represents the category of an AI policy used by the runtime.
    /// </summary>
    /// <remarks>
    /// Policy kinds allow the runtime to organize and resolve policies by responsibility
    /// while maintaining a unified policy registration and discovery model.
    /// </remarks>
    public enum AiPolicyKind
    {
        /// <summary>
        /// Defines retry behavior for failed execution steps.
        /// </summary>
        Retry = 0,

        /// <summary>
        /// Defines timeout behavior for long-running operations.
        /// </summary>
        Timeout = 1,

        /// <summary>
        /// Defines circuit-breaker behavior for unstable dependencies.
        /// </summary>
        CircuitBreaker = 2,

        /// <summary>
        /// Defines rate-limiting behavior for external services or providers.
        /// </summary>
        RateLimit = 3,

        /// <summary>
        /// Defines validation rules for inputs, outputs, or execution state.
        /// </summary>
        Validation = 4,

        /// <summary>
        /// Defines routing logic between providers, tools, or execution paths.
        /// </summary>
        Routing = 5,

        /// <summary>
        /// Defines retention behavior for execution state, including compaction,
        /// payload externalization, and hot-state eviction.
        /// </summary>
        Retention = 6,

        /// <summary>
        /// Defines concurrency behavior for distributed step admission,
        /// throttling, and execution capacity decisions.
        /// </summary>
        Concurrency = 7,

        /// <summary>
        /// Defines authorization behavior for delegating execution from a parent step to a child DAG.
        /// </summary>
        Delegation = 8
    }
}