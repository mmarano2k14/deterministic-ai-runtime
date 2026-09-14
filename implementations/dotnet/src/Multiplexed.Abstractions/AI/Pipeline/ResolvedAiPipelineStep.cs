using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.Abstractions.AI.Invocation;
using System.Text.Json.Serialization;

namespace Multiplexed.Abstractions.AI.Pipeline
{
    /// <summary>
    /// Represents a resolved executable pipeline step.
    /// This model bridges the declarative pipeline definition and the
    /// concrete runtime step instance resolved by the runtime.
    ///
    /// In sequential mode, <see cref="Order"/> can be used to determine
    /// the next step to execute.
    ///
    /// In DAG mode, step execution should be determined from dependency
    /// satisfaction through <see cref="DependsOn"/>, not from <see cref="Order"/> alone.
    /// </summary>
    public sealed class ResolvedAiPipelineStep
    {
        /// <summary>
        /// Gets or sets the unique step instance name inside the pipeline.
        /// This value must be unique per pipeline and is used as the runtime
        /// identity for state, bindings, and result resolution.
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Gets or sets the declarative step key used for registry resolution.
        /// This identifies the step type, not the pipeline instance.
        /// </summary>
        public string StepKey { get; init; } = string.Empty;

        /// <summary>The original local declaration, not an inherited value.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ExecutionLanguage { get; init; }

        /// <summary>Original invocation descriptor, preserved for definition projections.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AiInvocationDefinition? Invocation { get; init; }

        /// <summary>Effective immutable metadata; null on historical undeclared native steps.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AiInvocationBinding? InvocationBinding { get; init; }

        /// <summary>
        /// Ordered concurrency-policy bindings with their original declaration scope.
        /// Recompiled from the pinned definition, never from a merged admission config.
        /// </summary>
        [JsonIgnore]
        public IReadOnlyList<AiPolicyInvocationBinding> ConcurrencyPolicyBindings { get; init; }
            = Array.Empty<AiPolicyInvocationBinding>();

        /// <summary>
        /// Gets or sets the resolved runtime step instance.
        /// </summary>
        public IAiStep Step { get; init; } = default!;

        /// <summary>
        /// Gets or sets the execution order of the step.
        ///
        /// This property remains useful for sequential execution and as a stable
        /// deterministic ordering hint, but it is not sufficient on its own for DAG execution.
        /// </summary>
        public int Order { get; init; }

        /// <summary>
        /// Gets or sets the names of upstream step instances that must complete
        /// before this step can be executed in DAG mode.
        ///
        /// An empty collection means the step has no dependencies and may be considered
        /// a root step in the execution graph.
        /// </summary>
        public IReadOnlyCollection<string> DependsOn { get; init; }
            = Array.Empty<string>();

        /// <summary>
        /// Gets or sets the optional declarative input for this step instance.
        /// </summary>
        public IReadOnlyDictionary<string, object?> Input { get; init; }
            = new Dictionary<string, object?>();

        /// <summary>
        /// Gets or sets the optional declarative configuration for this step instance.
        /// </summary>
        public IReadOnlyDictionary<string, object?> Config { get; init; }
            = new Dictionary<string, object?>();

        /// <summary>
        /// Gets or sets the maximum number of retry attempts allowed for this step
        /// after the initial execution attempt fails.
        ///
        /// A value of 0 means no retries.
        /// This value is resolved from the declarative pipeline definition and carried
        /// forward so the runtime can initialize persisted retry state deterministically.
        /// </summary>
        public int MaxRetries { get; init; }

        /// <summary>
        /// Gets or sets the delay, in milliseconds, to wait before a failed step
        /// becomes eligible for another retry attempt.
        ///
        /// A value of 0 means immediate retry eligibility.
        /// This value should be used to compute persisted retry timing such as
        /// NextRetryAtUtc in step runtime state.
        /// </summary>
        public int RetryDelayMs { get; init; }

        /// <summary>
        /// Gets a value indicating whether this step has any declared dependencies.
        /// </summary>
        public bool HasDependencies => DependsOn.Count > 0;

        /// <summary>
        /// Gets a value indicating whether this step declares retry behavior.
        /// </summary>
        public bool HasRetryPolicy => MaxRetries > 0;
    }
}