using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Concurrency;

namespace Multiplexed.Abstractions.AI.Execution
{
    /// <summary>
    /// Represents a step-scoped execution context bound to one resolved pipeline step.
    ///
    /// PURPOSE:
    /// - Connects the global execution context to the selected step.
    /// - Exposes step identity and durable step state.
    /// - Provides access to execution services and cancellation.
    ///
    /// DESIGN:
    /// - This type is a lightweight context object.
    /// - It does not perform input/config/path resolution.
    /// - Step value resolution is handled by IAiStepContextHelper.
    /// - Path and payload resolution are handled by IAiContextValueResolver.
    /// - State mutation is routed through IAiExecutionStateWriter.
    ///
    /// IMPORTANT:
    /// - This context does not own orchestration decisions.
    /// - It is created after the runtime has selected a step to execute.
    /// - The selected step state is initialized during construction.
    /// </summary>
    public sealed class AiStepExecutionContext
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AiStepExecutionContext"/> class.
        /// </summary>
        /// <param name="execution">The execution-scoped context.</param>
        /// <param name="step">The resolved pipeline step bound to this context.</param>
        public AiStepExecutionContext(
            AiExecutionContext execution,
            ResolvedAiPipelineStep step)
        {
            ArgumentNullException.ThrowIfNull(execution);
            ArgumentNullException.ThrowIfNull(step);
            ArgumentException.ThrowIfNullOrWhiteSpace(step.Name);

            Execution = execution;
            Step = step;

            Execution.EnsureStepInitialized(step);
        }

        /// <summary>
        /// Gets the execution-scoped context.
        /// </summary>
        public AiExecutionContext Execution { get; }

        /// <summary>
        /// Gets the resolved pipeline step bound to this context.
        /// </summary>
        public ResolvedAiPipelineStep Step { get; }

        /// <summary>Effective binding; historical undeclared steps remain native.</summary>
        public AiInvocationBinding InvocationBinding => Step.InvocationBinding ?? AiInvocationBinding.Native;

        /// <summary>Original scopes for the configured concurrency checkpoint.</summary>
        public IReadOnlyList<AiPolicyInvocationBinding> ConcurrencyPolicyBindings => Step.ConcurrencyPolicyBindings;

        /// <summary>
        /// Effective definition already prepared for the admission gate. Present only
        /// in admission contexts; prevents re-reading a different step-only config.
        /// This is a borrowed decision input, not a durable state mutation.
        /// </summary>
        public AiConcurrencyDefinition? ConcurrencyAdmissionDefinition { get; init; }

        /// <summary>
        /// Gets the persisted execution record.
        /// </summary>
        public AiExecutionRecord Record => Execution.Record;

        /// <summary>
        /// Gets the mutable execution state.
        /// </summary>
        public AiExecutionState State => Execution.State;

        /// <summary>
        /// Gets the scoped service provider.
        /// </summary>
        public IServiceProvider Services => Execution.Services;

        /// <summary>
        /// Gets the active cancellation token.
        /// </summary>
        public CancellationToken CancellationToken => Execution.CancellationToken;

        /// <summary>
        /// Gets the current execution identifier.
        /// </summary>
        public string ExecutionId => Execution.ExecutionId;

        /// <summary>
        /// Gets the logical step name.
        /// </summary>
        public string StepName => Step.Name;

        /// <summary>
        /// Gets the resolved step registry key.
        /// </summary>
        public string StepKey => Step.StepKey;

        /// <summary>
        /// Gets the durable runtime state for the current step.
        ///
        /// NOTE:
        /// - Creation is routed through the execution state writer.
        /// - The writer is responsible for timestamp and mutation consistency.
        /// </summary>
        public AiStepState StepState => Execution.GetOrCreateStep(StepName);

        /// <summary>
        /// Resolves a required scoped service from the execution service provider.
        /// </summary>
        public T GetRequiredService<T>() where T : notnull
        {
            return Execution.GetRequiredService<T>();
        }
    }
}