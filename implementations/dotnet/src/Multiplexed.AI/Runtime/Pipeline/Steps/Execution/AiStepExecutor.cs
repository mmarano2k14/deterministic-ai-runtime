using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.Abstractions.Runtime;
using Multiplexed.AI.Runtime.Observability.Logging;

namespace Multiplexed.AI.Runtime.Pipeline.Steps.Execution
{
    /// <summary>
    /// Executes a single resolved AI step once and records local execution metadata.
    ///
    /// PURPOSE:
    /// - Execute the resolved step exactly once.
    /// - Track local attempt metadata for observability and diagnostics.
    /// - Prevent re-execution of steps already marked as completed in local metadata.
    /// - Emit structured execution events.
    ///
    /// IMPORTANT:
    /// - This class does not own retry decisions.
    /// - This class does not perform local retry loops.
    /// - Durable retry decisions are owned by the Retry Engine.
    /// - Distributed retry scheduling is owned by the DAG store / Redis Lua layer.
    /// - Durable retry state remains owned by <see cref="AiStepState.RetryState"/>.
    /// - Retry configuration remains owned by <see cref="AiStepState.Retry"/>.
    ///
    /// CENTRALIZATION NOTE:
    /// - Payload compaction is handled centrally by orchestration-level code.
    /// - Memory writing should be handled centrally by orchestration-level runtime services.
    /// - Keeping this executor execution-only avoids inconsistent behavior between
    ///   sequential, DAG, RAG, operation, and provider execution paths.
    ///
    /// SEMANTICS:
    /// - <see cref="AiStepExecutionMetadata.AttemptCount"/> counts local executor invocations.
    /// - It is not equivalent to durable business retry count.
    /// - Durable retry count is stored in <see cref="AiStepState.RetryState"/>.
    /// </summary>
    public sealed class AiStepExecutor : IAiStepExecutor
    {
        private readonly IAiRuntimeLogger _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiStepExecutor"/> class.
        /// </summary>
        /// <param name="logger">The runtime logger.</param>
        public AiStepExecutor(IAiRuntimeLogger logger)
        {
            ArgumentNullException.ThrowIfNull(logger);

            _logger = logger;
        }

        /// <summary>
        /// Executes the specified resolved step exactly once.
        ///
        /// FLOW:
        /// - Load or create local step execution metadata.
        /// - Skip execution if local metadata already marks the step as completed.
        /// - Execute the step once.
        /// - Record success, failure result, or exception metadata.
        /// - Return the step result or rethrow the exception.
        ///
        /// IMPORTANT:
        /// - This method does not perform retry.
        /// - This method does not schedule durable retry windows.
        /// - This method does not mutate distributed retry counters.
        /// - Retry/fail decisions must be handled by orchestration-level retry services.
        /// </summary>
        /// <param name="resolvedStep">The resolved pipeline step to execute.</param>
        /// <param name="context">The current step execution context.</param>
        /// <param name="cancellationToken">A token used to cancel execution.</param>
        /// <returns>The result returned by the step.</returns>
        public async Task<AiStepResult> ExecuteAsync(
            ResolvedAiPipelineStep resolvedStep,
            AiStepExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(resolvedStep);
            ArgumentNullException.ThrowIfNull(resolvedStep.Step);
            ArgumentNullException.ThrowIfNull(context);

            var stepType = resolvedStep.Step.GetType();

            var stepName = resolvedStep.Name;
            var fallbackStepName = stepType.Name;

            var metadata =
                TryGetStepMetadata(context.State, stepName)
                ?? TryGetStepMetadata(context.State, fallbackStepName);

            if (metadata != null && metadata.IsCompleted)
            {
                _logger.StepExecutor.Skipped(context.ExecutionId, stepName);

                return AiStepResult.Ok(
                    output: $"Step '{stepName}' was skipped because it had already completed successfully.");
            }

            metadata ??= GetOrCreateStepMetadata(context.State, stepName);

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                BeginAttempt(metadata);

                _logger.StepExecutor.AttemptStarted(
                    context.ExecutionId,
                    stepName,
                    metadata.AttemptCount);

                var result = await resolvedStep.Step.ExecuteAsync(
                    context,
                    cancellationToken);

                if (result.EffectiveOutcome == AiStepExecutionOutcome.Park)
                {
                    throw new InvalidOperationException(
                        $"Step '{stepName}' returned a Park outcome outside DAG orchestration.");
                }

                if (!result.Success)
                {
                    metadata.Status = AiStepExecutionStatus.Failed;
                    metadata.LastError = result.Error;
                    metadata.LastExceptionType = null;

                    _logger.StepExecutor.AttemptFailed(
                        context.ExecutionId,
                        stepName,
                        metadata.AttemptCount,
                        result.Error);

                    return result;
                }

                MarkSucceeded(metadata);

                _logger.StepExecutor.AttemptSucceeded(
                    context.ExecutionId,
                    stepName,
                    metadata.AttemptCount);

                return result;
            }
            catch (Exception ex)
            {
                metadata.Status = AiStepExecutionStatus.Failed;
                metadata.LastError = ex.Message;
                metadata.LastExceptionType = ex.GetType().FullName;

                _logger.StepExecutor.AttemptException(
                    context.ExecutionId,
                    stepName,
                    metadata.AttemptCount,
                    ex);

                throw;
            }
        }

        /// <summary>
        /// Attempts to retrieve existing local step execution metadata without creating it.
        /// </summary>
        /// <param name="state">The execution state.</param>
        /// <param name="stepName">The logical step name.</param>
        /// <returns>The metadata entry when found; otherwise, <see langword="null"/>.</returns>
        private static AiStepExecutionMetadata? TryGetStepMetadata(
            AiExecutionState state,
            string stepName)
        {
            if (!state.Metadata.TryGetValue(AiExecutionKeys.StepExecutionMetadata, out var existing) ||
                existing is not Dictionary<string, AiStepExecutionMetadata> stepMap)
            {
                return null;
            }

            return stepMap.TryGetValue(stepName, out var metadata)
                ? metadata
                : null;
        }

        /// <summary>
        /// Begins a new local execution attempt.
        /// </summary>
        /// <param name="metadata">The local step execution metadata to update.</param>
        private static void BeginAttempt(AiStepExecutionMetadata metadata)
        {
            metadata.AttemptCount++;
            metadata.Status = AiStepExecutionStatus.Running;
            metadata.LastStartedAtUtc = DateTimeOffset.UtcNow;
            metadata.FirstStartedAtUtc ??= metadata.LastStartedAtUtc;
        }

        /// <summary>
        /// Marks the local execution metadata as successfully completed.
        /// </summary>
        /// <param name="metadata">The local step execution metadata to update.</param>
        private static void MarkSucceeded(AiStepExecutionMetadata metadata)
        {
            metadata.Status = AiStepExecutionStatus.Completed;
            metadata.CompletedAtUtc = DateTimeOffset.UtcNow;
            metadata.LastError = null;
            metadata.LastExceptionType = null;
        }

        /// <summary>
        /// Gets the local step execution metadata map or creates it when missing,
        /// then returns the entry for the specified logical step name.
        /// </summary>
        /// <param name="state">The execution state.</param>
        /// <param name="stepName">The logical step name.</param>
        /// <returns>The metadata entry for the step.</returns>
        private static AiStepExecutionMetadata GetOrCreateStepMetadata(
            AiExecutionState state,
            string stepName)
        {
            if (!state.Metadata.TryGetValue(AiExecutionKeys.StepExecutionMetadata, out var existing) ||
                existing is not Dictionary<string, AiStepExecutionMetadata> stepMap)
            {
                stepMap = new Dictionary<string, AiStepExecutionMetadata>(StringComparer.Ordinal);
                state.Metadata[AiExecutionKeys.StepExecutionMetadata] = stepMap;
            }

            if (!stepMap.TryGetValue(stepName, out var metadata))
            {
                metadata = new AiStepExecutionMetadata
                {
                    StepName = stepName
                };

                stepMap[stepName] = metadata;
            }

            state.UpdatedAtUtc = DateTime.UtcNow;

            return metadata;
        }
    }
}