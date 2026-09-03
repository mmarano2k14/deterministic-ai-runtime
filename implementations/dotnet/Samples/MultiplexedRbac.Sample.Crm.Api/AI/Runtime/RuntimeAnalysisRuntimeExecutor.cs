using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.AI.Stores;
using MultiplexedRbac.Sample.Crm.Api.AI.Models;
using MultiplexedRbac.Sample.Crm.Api.AI.Services;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Runtime
{
    public sealed class RuntimeAnalysisRuntimeExecutor :
        IRuntimeAnalysisRuntimeExecutor
    {
        private static readonly JsonSerializerOptions SerializerOptions =
            new()
            {
                PropertyNameCaseInsensitive = true
            };

        private readonly IAiRuntimePipelineBackgroundController _controller;
        private readonly IAiDagExecutionStore _dagStore;
        private readonly RuntimeAnalysisPipelineDefinitionFactory _pipelineFactory;
        private readonly RuntimeAnalysisExecutionContextSnapshotFactory
            _executionContextSnapshotFactory;
        private readonly RuntimeAnalysisResultValidator _resultValidator;
        private readonly RuntimeAnalysisRuntimeOptions _options;

        public RuntimeAnalysisRuntimeExecutor(
            IAiRuntimePipelineBackgroundController controller,
            IAiDagExecutionStore dagStore,
            RuntimeAnalysisPipelineDefinitionFactory pipelineFactory,
            RuntimeAnalysisExecutionContextSnapshotFactory executionContextSnapshotFactory,
            RuntimeAnalysisResultValidator resultValidator,
            RuntimeAnalysisRuntimeOptions options)
        {
            _controller =
                controller
                ?? throw new ArgumentNullException(
                    nameof(controller));
            _dagStore =
                dagStore
                ?? throw new ArgumentNullException(
                    nameof(dagStore));
            _pipelineFactory =
                pipelineFactory
                ?? throw new ArgumentNullException(
                    nameof(pipelineFactory));
            _executionContextSnapshotFactory =
                executionContextSnapshotFactory
                ?? throw new ArgumentNullException(
                    nameof(executionContextSnapshotFactory));
            _resultValidator =
                resultValidator
                ?? throw new ArgumentNullException(
                    nameof(resultValidator));
            _options =
                options
                ?? throw new ArgumentNullException(
                    nameof(options));
        }

        public async Task<RuntimeAnalysisRuntimeExecutionResult> AnalyzeAsync(
            RuntimeAnalysisProviderRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(
                request);

            var pipeline = _pipelineFactory.Create(
                request);

            var executionContextSnapshot =
                _executionContextSnapshotFactory.Create();

            var handle = await _controller.EnqueueAsync(
                    new AiRuntimePipelineRunRequest
                    {
                        PipelineName =
                            RuntimeAnalysisPipelineDefinitionFactory.PipelineName,
                        PipelineDefinition = pipeline,
                        ExecutionContextSnapshot = executionContextSnapshot,
                        Metadata = new Dictionary<string, string>(
                            StringComparer.Ordinal)
                        {
                            ["source"] = "runtime-analysis-api",
                            ["operation"] = "openai-analysis"
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            AiExecutionRecord finalRecord;

            try
            {
                finalRecord = await handle.Completion.WaitAsync(
                        _options.ExecutionTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Runtime analysis DAG did not complete within {_options.ExecutionTimeout.TotalSeconds:0} seconds.",
                    exception);
            }

            var executionId = !string.IsNullOrWhiteSpace(
                    handle.ExecutionId)
                ? handle.ExecutionId
                : finalRecord.ExecutionId;

            if (string.IsNullOrWhiteSpace(
                    executionId))
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    "Runtime analysis completed without a durable ExecutionId.");
            }

            var state = await _dagStore.GetStateAsync(
                    executionId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Runtime analysis execution '{executionId}' has no persisted DAG state.");

            if (!state.Steps.TryGetValue(
                    RuntimeAnalysisPipelineDefinitionFactory.AnalyzeStepName,
                    out var stepState))
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Runtime analysis execution '{executionId}' does not contain step '{RuntimeAnalysisPipelineDefinitionFactory.AnalyzeStepName}'.");
            }

            var stepResult = stepState.Result;

            if (stepResult is null)
            {
                if (!string.IsNullOrWhiteSpace(
                        stepState.Error))
                {
                    throw new RuntimeAnalysisRuntimeExecutionException(
                        $"Runtime analysis DAG step failed: {stepState.Error}");
                }

                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Runtime analysis step '{stepState.StepName}' completed without a persisted result. Runtime status: {finalRecord.Status}.");
            }

            if (!stepResult.Success)
            {
                var error =
                    stepResult.Error
                    ?? stepState.Error
                    ?? "Unknown runtime analysis step failure.";

                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Runtime analysis DAG step failed: {error}");
            }

            if (string.IsNullOrWhiteSpace(
                    stepResult.Output))
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    "Runtime analysis DAG step completed without structured output.");
            }

            RuntimeAnalysisResult result;

            try
            {
                result =
                    JsonSerializer.Deserialize<RuntimeAnalysisResult>(
                        stepResult.Output,
                        SerializerOptions)
                    ?? throw new RuntimeAnalysisRuntimeExecutionException(
                        "Runtime analysis DAG output deserialized to null.");
            }
            catch (JsonException exception)
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    "Runtime analysis DAG returned invalid structured output.",
                    exception);
            }

            _resultValidator.Validate(
                result,
                request.Snapshot);

            return new RuntimeAnalysisRuntimeExecutionResult
            {
                RunId = handle.RunId,
                ExecutionId = executionId,
                PipelineName =
                    finalRecord.PipelineName
                    ?? RuntimeAnalysisPipelineDefinitionFactory.PipelineName,
                StepName =
                    RuntimeAnalysisPipelineDefinitionFactory.AnalyzeStepName,
                RuntimeStatus = finalRecord.Status.ToString(),
                Result = result
            };
        }
    }
}
