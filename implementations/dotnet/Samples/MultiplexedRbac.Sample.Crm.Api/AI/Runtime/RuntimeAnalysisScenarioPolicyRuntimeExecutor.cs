using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.AI.Stores;
using MultiplexedRbac.Sample.Crm.Api.AI.Models;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Runtime
{
    public sealed class RuntimeAnalysisScenarioPolicyRuntimeExecutor :
        IRuntimeAnalysisScenarioPolicyExecutor
    {
        private static readonly JsonSerializerOptions SerializerOptions =
            new()
            {
                PropertyNameCaseInsensitive = true
            };

        private readonly IAiRuntimePipelineBackgroundController _controller;
        private readonly IAiDagExecutionStore _dagStore;
        private readonly RuntimeAnalysisScenarioPolicyPipelineDefinitionFactory
            _pipelineFactory;
        private readonly RuntimeAnalysisExecutionContextSnapshotFactory
            _executionContextSnapshotFactory;
        private readonly RuntimeAnalysisRuntimeOptions _options;

        public RuntimeAnalysisScenarioPolicyRuntimeExecutor(
            IAiRuntimePipelineBackgroundController controller,
            IAiDagExecutionStore dagStore,
            RuntimeAnalysisScenarioPolicyPipelineDefinitionFactory pipelineFactory,
            RuntimeAnalysisExecutionContextSnapshotFactory executionContextSnapshotFactory,
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
            _options =
                options
                ?? throw new ArgumentNullException(
                    nameof(options));
        }

        public async Task<RuntimeAnalysisScenarioPolicyRuntimeExecutionResult>
            ValidateAsync(
                RuntimeAnalysisSuggestedScenario scenario,
                CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(
                scenario);

            var pipeline = _pipelineFactory.Create(
                scenario);

            var executionContextSnapshot =
                _executionContextSnapshotFactory.Create();

            var handle = await _controller.EnqueueAsync(
                    new AiRuntimePipelineRunRequest
                    {
                        PipelineName =
                            RuntimeAnalysisScenarioPolicyPipelineDefinitionFactory
                                .PipelineName,
                        PipelineDefinition = pipeline,
                        ExecutionContextSnapshot = executionContextSnapshot,
                        Metadata = new Dictionary<string, string>(
                            StringComparer.Ordinal)
                        {
                            ["source"] = "runtime-analysis-api",
                            ["operation"] =
                                "scenario-policy-validation"
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
                    $"Scenario policy validation DAG did not complete within {_options.ExecutionTimeout.TotalSeconds:0} seconds.",
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
                    "Scenario policy validation completed without a durable ExecutionId.");
            }

            var state = await _dagStore.GetStateAsync(
                    executionId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Scenario policy validation execution '{executionId}' has no persisted DAG state.");

            if (!state.Steps.TryGetValue(
                    RuntimeAnalysisScenarioPolicyPipelineDefinitionFactory
                        .ValidateStepName,
                    out var stepState))
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Scenario policy validation execution '{executionId}' does not contain step '{RuntimeAnalysisScenarioPolicyPipelineDefinitionFactory.ValidateStepName}'.");
            }

            var stepResult = stepState.Result;

            if (stepResult is null)
            {
                if (!string.IsNullOrWhiteSpace(
                        stepState.Error))
                {
                    throw new RuntimeAnalysisRuntimeExecutionException(
                        $"Scenario policy validation DAG step failed: {stepState.Error}");
                }

                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Scenario policy validation step '{stepState.StepName}' completed without a persisted result. Runtime status: {finalRecord.Status}.");
            }

            if (!stepResult.Success)
            {
                var error =
                    stepResult.Error
                    ?? stepState.Error
                    ?? "Unknown scenario policy validation failure.";

                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Scenario policy validation DAG step failed: {error}");
            }

            if (string.IsNullOrWhiteSpace(
                    stepResult.Output))
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    "Scenario policy validation DAG completed without structured output.");
            }

            RuntimeAnalysisScenarioPolicyValidationResult result;

            try
            {
                result =
                    JsonSerializer.Deserialize<
                        RuntimeAnalysisScenarioPolicyValidationResult>(
                        stepResult.Output,
                        SerializerOptions)
                    ?? throw new RuntimeAnalysisRuntimeExecutionException(
                        "Scenario policy validation DAG output deserialized to null.");
            }
            catch (JsonException exception)
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    "Scenario policy validation DAG returned invalid structured output.",
                    exception);
            }

            return new RuntimeAnalysisScenarioPolicyRuntimeExecutionResult
            {
                RunId = handle.RunId,
                ExecutionId = executionId,
                PipelineName =
                    finalRecord.PipelineName
                    ?? RuntimeAnalysisScenarioPolicyPipelineDefinitionFactory
                        .PipelineName,
                StepName =
                    RuntimeAnalysisScenarioPolicyPipelineDefinitionFactory
                        .ValidateStepName,
                RuntimeStatus = finalRecord.Status.ToString(),
                Result = result
            };
        }
    }
}
