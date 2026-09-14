using Multiplexed.Abstractions.AI.Invocation;
using System.Text.Json.Serialization;

namespace Multiplexed.Abstractions.AI.Pipeline
{
    /// <summary>
    /// Represents declarative execution policy metadata attached to a pipeline step.
    ///
    /// This section contains runtime orchestration settings rather than business
    /// configuration. It is intended for retry, timeout, and other execution-level
    /// behaviors controlled by the runtime engine.
    /// </summary>
    public sealed class AiPipelineStepExecutionDefinition
    {
        /// <summary>
        /// Gets or sets the maximum number of retry attempts allowed after the
        /// initial execution attempt fails.
        ///
        /// A value of 0 means no retries.
        /// </summary>
        public int MaxRetries { get; init; }

        /// <summary>
        /// Gets or sets the delay, in milliseconds, before the runtime may attempt
        /// the next retry after a retryable failure.
        ///
        /// A value of 0 means immediate retry eligibility.
        /// This value is declarative runtime metadata and must be persisted in
        /// step state rather than implemented as an in-memory delay.
        /// </summary>
        public int RetryDelayMs { get; init; }

        /// <summary>
        /// Gets a value indicating whether this execution policy enables retries.
        /// </summary>
        public bool HasRetryPolicy => MaxRetries > 0;
    }

    /// <summary>
    /// Represents a single declarative step inside an AI pipeline definition.
    /// The step is identified by a provider-neutral key so that the definition
    /// remains portable across storage providers and runtime implementations.
    /// </summary>
    public sealed class AiPipelineStepDefinition
    {
        /// <summary>
        /// Gets or sets the unique declarative step name.
        ///
        /// This is the step instance identity within the pipeline.
        /// It must be unique per pipeline definition.
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Gets or sets the provider-neutral runtime step key used to resolve
        /// the executable step implementation.
        ///
        /// This identifies the step type, not the step instance.
        /// </summary>
        public string StepKey { get; init; } = string.Empty;

        /// <summary>Optional local custom language override. Never propagates to other steps.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ExecutionLanguage { get; init; }

        /// <summary>Optional invocation descriptor. Absence preserves native registry resolution.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AiInvocationDefinition? Invocation { get; init; }

        /// <summary>
        /// Gets or sets the execution order of the step.
        ///
        /// This property is primarily used for sequential execution and as a stable
        /// deterministic ordering hint for DAG scheduling.
        /// </summary>
        public int Order { get; init; }

        /// <summary>
        /// Gets or sets the names of upstream step instances that must complete
        /// before this step can be executed in DAG mode.
        ///
        /// This collection must contain pipeline step names, not step type keys.
        /// An empty collection means the step has no dependencies and can be treated
        /// as a root node in the execution graph.
        /// </summary>
        public IReadOnlyCollection<string> DependsOn { get; init; }
            = Array.Empty<string>();

        /// <summary>
        /// Gets or sets the optional declarative input for this step.
        /// </summary>
        public IReadOnlyDictionary<string, object?> Input { get; init; }
            = new Dictionary<string, object?>();

        /// <summary>
        /// Gets or sets the optional declarative business configuration for this step.
        ///
        /// This section is intended for step-specific behavior and input shaping,
        /// not for runtime orchestration policy such as retries or claim handling.
        /// </summary>
        public IReadOnlyDictionary<string, object?> Config { get; init; }
            = new Dictionary<string, object?>();

        /// <summary>
        /// Gets or sets the optional execution policy for this step.
        ///
        /// This section contains runtime orchestration metadata such as retry
        /// configuration. It is the preferred location for new pipeline definitions.
        /// </summary>
        public AiPipelineStepExecutionDefinition? Execution { get; init; }

        public int ResolvedMaxRetries => Execution?.MaxRetries ?? 0;

        /// <summary>
        /// Gets the resolved retry delay, in milliseconds, for this step.
        ///
        /// Resolution order:
        /// 1. Execution.RetryDelayMs
        /// 2. root-level RetryDelayMs
        /// 3. default value 0
        /// </summary>
        public int ResolvedRetryDelayMs => Execution?.RetryDelayMs ?? 0;

        /// <summary>
        /// Gets a value indicating whether this step declares dependencies.
        /// </summary>
        public bool HasDependencies => DependsOn.Count > 0;

        /// <summary>
        /// Gets a value indicating whether this step declares retry behavior.
        /// </summary>
        public bool HasRetryPolicy => ResolvedMaxRetries > 0;
    }
}