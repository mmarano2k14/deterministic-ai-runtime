using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Steps;

namespace Multiplexed.AI.Runtime.Pipeline
{
    /// <summary>
    /// Executes a resolved AI pipeline using strict sequential orchestration.
    ///
    /// Responsibilities:
    /// - Select the appropriate pipeline definition provider during preparation
    /// - Resolve the configured pipeline into a runtime executable pipeline
    /// - Determine the current step using sequential index-based progression
    /// - Create a step-scoped execution context for the selected step
    /// - Delegate step execution to <see cref="IAiStepExecutor"/>
    /// - Return structured progression data to the execution engine
    ///
    /// This executor is strictly sequential.
    /// DAG execution is handled separately by <c>AiDagExecutionEngine</c>.
    /// </summary>
    public sealed class AiSequentialPipelineExecutor : IAiSequentialPipelineExecutor
    {
        private readonly IAiPipelineDefinitionSourceSelector _sourceSelector;
        private readonly IAiPipelineResolver _pipelineResolver;
        private readonly IAiStepExecutor _stepExecutor;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiSequentialPipelineExecutor"/> class.
        /// </summary>
        public AiSequentialPipelineExecutor(
            IAiPipelineDefinitionSourceSelector sourceSelector,
            IAiPipelineResolver pipelineResolver,
            IAiStepExecutor stepExecutor)
        {
            ArgumentNullException.ThrowIfNull(sourceSelector);
            ArgumentNullException.ThrowIfNull(pipelineResolver);
            ArgumentNullException.ThrowIfNull(stepExecutor);

            _sourceSelector = sourceSelector;
            _pipelineResolver = pipelineResolver;
            _stepExecutor = stepExecutor;
        }

        /// <inheritdoc />
        public async Task<ResolvedAiPipeline> PrepareAsync(
            string pipelineName,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);

            var provider = _sourceSelector.Select(pipelineName);

            var definition = await provider.GetDefinitionAsync(
                pipelineName,
                cancellationToken);

            return await PrepareAsync(definition, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<ResolvedAiPipeline> PrepareAsync(
            AiPipelineDefinition definition,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);

            var pipeline = await _pipelineResolver.ResolveAsync(
                definition,
                cancellationToken);

            var orderedSteps = pipeline.Steps
                .OrderBy(x => x.Order)
                .ToArray();

            if (orderedSteps.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Pipeline '{definition.Name}' does not contain any resolved steps.");
            }

            return new ResolvedAiPipeline
            {
                Name = pipeline.Name,
                Version = pipeline.Version,
                ExecutionMode = pipeline.ExecutionMode,
                Config = pipeline.Config,
                Steps = orderedSteps
            };
        }

        /// <inheritdoc />
        public async Task<PipelineExecutionResult> ExecuteNextAsync(
            ResolvedAiPipeline pipeline,
            AiExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(pipeline);
            ArgumentNullException.ThrowIfNull(context);

            var record = context.Record;

            var orderedSteps = pipeline.Steps
                .OrderBy(x => x.Order)
                .ToArray();

            if (record.CurrentStepIndex < 0 || record.CurrentStepIndex > orderedSteps.Length)
            {
                throw new InvalidOperationException(
                    $"Execution '{record.ExecutionId}' has an invalid current step index '{record.CurrentStepIndex}'.");
            }

            if (record.CurrentStepIndex >= orderedSteps.Length)
            {
                throw new InvalidOperationException(
                    $"Execution '{record.ExecutionId}' has no remaining step to execute.");
            }

            var resolvedStep = orderedSteps[record.CurrentStepIndex];

            var stepContext = new AiStepExecutionContext(
                context,
                resolvedStep);

            var stepResult = await _stepExecutor.ExecuteAsync(
                resolvedStep,
                stepContext,
                cancellationToken);

            context.StateWriter.SetStepResult(
                context.State,
                resolvedStep.Name,
                stepResult);

            var nextStepIndex = record.CurrentStepIndex + 1;
            var isCompleted = nextStepIndex >= orderedSteps.Length;
            var nextStepName = isCompleted
                ? null
                : orderedSteps[nextStepIndex].Name;

            return new PipelineExecutionResult
            {
                PipelineName = pipeline.Name,
                Steps = orderedSteps.Select(x => x.Name).ToArray(),
                ExecutedStepName = resolvedStep.Name,
                ExecutedStepIndex = record.CurrentStepIndex,
                NextStepIndex = nextStepIndex,
                NextStepName = nextStepName,
                IsCompleted = isCompleted,
                StepResult = stepResult
            };
        }
    }
}