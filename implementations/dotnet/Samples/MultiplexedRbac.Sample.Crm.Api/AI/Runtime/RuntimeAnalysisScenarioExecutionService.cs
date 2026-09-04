using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.AI.Stores;
using MultiplexedRbac.Sample.Crm.Api.AI.Models;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Runtime
{
    public sealed class RuntimeAnalysisScenarioExecutionService :
        IRuntimeAnalysisScenarioExecutionService
    {
        private static readonly TimeSpan TerminalPollInterval =
            TimeSpan.FromMilliseconds(100);

        private readonly IAiRuntimePipelineBackgroundController _controller;
        private readonly IAiDagExecutionStore _dagStore;
        private readonly IRuntimeAnalysisScenarioExecutionStore _executionStore;
        private readonly RuntimeAnalysisExecutionResultReader _resultReader;
        private readonly RuntimeAnalysisRuntimeOptions _options;

        public RuntimeAnalysisScenarioExecutionService(
            IAiRuntimePipelineBackgroundController controller,
            IAiDagExecutionStore dagStore,
            IRuntimeAnalysisScenarioExecutionStore executionStore,
            RuntimeAnalysisExecutionResultReader resultReader,
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
            _executionStore =
                executionStore
                ?? throw new ArgumentNullException(
                    nameof(executionStore));
            _resultReader =
                resultReader
                ?? throw new ArgumentNullException(
                    nameof(resultReader));
            _options =
                options
                ?? throw new ArgumentNullException(
                    nameof(options));
        }

        public async Task<RuntimeAnalysisRuntimeExecutionResult> CompleteAsync(
            string executionId,
            RuntimeAnalysisScenarioExecutionObservation observation,
            string completedBy,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                executionId);
            ArgumentNullException.ThrowIfNull(
                observation);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                completedBy);

            var record = await _executionStore.CompleteAsync(
                    executionId,
                    observation,
                    completedBy,
                    cancellationToken)
                .ConfigureAwait(false);

            var stepStatus =
                await _resultReader.GetScenarioExecutionStepStatusAsync(
                        executionId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (stepStatus == AiStepExecutionStatus.Completed)
            {
                var currentRecord =
                    await _dagStore.GetRecordAsync(
                            executionId,
                            cancellationToken)
                        .ConfigureAwait(false)
                    ?? throw new RuntimeAnalysisRuntimeExecutionException(
                        $"Runtime analysis execution '{executionId}' has no persisted execution record.");

                currentRecord = await WaitForTerminalExecutionAsync(
                        currentRecord,
                        executionId,
                        cancellationToken)
                    .ConfigureAwait(false);

                return await _resultReader.ReadAsync(
                        record.InitialRunId ?? "unknown",
                        continuationRunId: null,
                        executionId,
                        currentRecord.PipelineName
                            ?? RuntimeAnalysisPipelineDefinitionFactory.PipelineName,
                        currentRecord.Status.ToString(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (stepStatus != AiStepExecutionStatus.WaitingForExternal)
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Scenario execution step for execution '{executionId}' cannot continue from status '{stepStatus}'.");
            }

            var handle = await _controller.EnqueueAsync(
                    new AiRuntimePipelineRunRequest
                    {
                        PipelineName =
                            RuntimeAnalysisPipelineDefinitionFactory.PipelineName,
                        ExternalWaitContinuation =
                            new AiRuntimeExternalWaitContinuation
                            {
                                ExecutionId = executionId,
                                StepName = record.StepName,
                                ContinuationId = record.ContinuationId
                            },
                        ExecutionContextSnapshot =
                            record.ExecutionContextSnapshot,
                        Metadata = new Dictionary<string, string>(
                            StringComparer.Ordinal)
                        {
                            ["source"] = "runtime-analysis-api",
                            ["operation"] =
                                "approved-scenario-execution-continuation",
                            ["client.state"] =
                                observation.ClientState
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
                    $"Approved scenario execution continuation did not complete within {_options.ExecutionTimeout.TotalSeconds:0} seconds.",
                    exception);
            }

            if (!string.Equals(
                    finalRecord.ExecutionId,
                    executionId,
                    StringComparison.Ordinal))
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Approved scenario execution continuation changed durable execution identity from '{executionId}' to '{finalRecord.ExecutionId}'.");
            }

            finalRecord = await WaitForTerminalExecutionAsync(
                    finalRecord,
                    executionId,
                    cancellationToken)
                .ConfigureAwait(false);

            return await _resultReader.ReadAsync(
                    record.InitialRunId ?? handle.RunId,
                    continuationRunId: handle.RunId,
                    executionId,
                    finalRecord.PipelineName
                        ?? RuntimeAnalysisPipelineDefinitionFactory.PipelineName,
                    finalRecord.Status.ToString(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        private async Task<AiExecutionRecord> WaitForTerminalExecutionAsync(
            AiExecutionRecord observedRecord,
            string executionId,
            CancellationToken cancellationToken)
        {
            if (observedRecord.IsTerminal)
            {
                return observedRecord;
            }

            var deadline =
                DateTimeOffset.UtcNow + _options.ExecutionTimeout;
            var currentRecord = observedRecord;

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                currentRecord =
                    await _dagStore.GetRecordAsync(
                            executionId,
                            cancellationToken)
                        .ConfigureAwait(false)
                    ?? throw new RuntimeAnalysisRuntimeExecutionException(
                        $"Runtime analysis execution '{executionId}' disappeared while waiting for recursive Child DAG convergence.");

                if (currentRecord.IsTerminal)
                {
                    return currentRecord;
                }

                await Task.Delay(
                        TerminalPollInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new RuntimeAnalysisRuntimeExecutionException(
                $"Runtime analysis execution '{executionId}' did not reach a terminal state after approved scenario execution and recursive Child DAG convergence within {_options.ExecutionTimeout.TotalSeconds:0} seconds. Last status='{currentRecord.Status}'.");
        }

    }
}
