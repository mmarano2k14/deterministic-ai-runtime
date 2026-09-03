using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using MultiplexedRbac.Sample.Crm.Api.AI.Models;
using MultiplexedRbac.Sample.Crm.Api.AI.Services;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Runtime
{
    public sealed class RuntimeAnalysisRuntimeExecutor :
        IRuntimeAnalysisRuntimeExecutor
    {
        private readonly IAiRuntimePipelineBackgroundController _controller;
        private readonly RuntimeAnalysisPipelineDefinitionFactory _pipelineFactory;
        private readonly RuntimeAnalysisExecutionContextSnapshotFactory
            _executionContextSnapshotFactory;
        private readonly RuntimeAnalysisExecutionResultReader _resultReader;
        private readonly IRuntimeAnalysisHumanApprovalStore _approvalStore;
        private readonly RuntimeAnalysisResultValidator _resultValidator;
        private readonly RuntimeAnalysisRuntimeOptions _options;

        public RuntimeAnalysisRuntimeExecutor(
            IAiRuntimePipelineBackgroundController controller,
            RuntimeAnalysisPipelineDefinitionFactory pipelineFactory,
            RuntimeAnalysisExecutionContextSnapshotFactory executionContextSnapshotFactory,
            RuntimeAnalysisExecutionResultReader resultReader,
            IRuntimeAnalysisHumanApprovalStore approvalStore,
            RuntimeAnalysisResultValidator resultValidator,
            RuntimeAnalysisRuntimeOptions options)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _pipelineFactory = pipelineFactory ?? throw new ArgumentNullException(nameof(pipelineFactory));
            _executionContextSnapshotFactory = executionContextSnapshotFactory ?? throw new ArgumentNullException(nameof(executionContextSnapshotFactory));
            _resultReader = resultReader ?? throw new ArgumentNullException(nameof(resultReader));
            _approvalStore = approvalStore ?? throw new ArgumentNullException(nameof(approvalStore));
            _resultValidator = resultValidator ?? throw new ArgumentNullException(nameof(resultValidator));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public async Task<RuntimeAnalysisRuntimeExecutionResult> AnalyzeAsync(
            RuntimeAnalysisProviderRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var executionContextSnapshot =
                _executionContextSnapshotFactory.Create();

            var handle = await _controller.EnqueueAsync(
                    new AiRuntimePipelineRunRequest
                    {
                        PipelineName = RuntimeAnalysisPipelineDefinitionFactory.PipelineName,
                        PipelineDefinition = _pipelineFactory.Create(request),
                        ExecutionContextSnapshot = executionContextSnapshot,
                        Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["source"] = "runtime-analysis-api",
                            ["operation"] = "analysis-policy-approval"
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            AiExecutionRecord attemptRecord;

            try
            {
                // Completion resolves for both terminal execution and durable external wait.
                attemptRecord = await handle.Completion.WaitAsync(
                        _options.ExecutionTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Runtime analysis DAG did not reach completion or durable human-approval wait within {_options.ExecutionTimeout.TotalSeconds:0} seconds.",
                    exception);
            }

            var executionId = !string.IsNullOrWhiteSpace(handle.ExecutionId)
                ? handle.ExecutionId
                : attemptRecord.ExecutionId;

            if (string.IsNullOrWhiteSpace(executionId))
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    "Runtime analysis attempt returned without a durable ExecutionId.");
            }

            var result = await _resultReader.ReadAsync(
                    handle.RunId,
                    continuationRunId: null,
                    executionId,
                    attemptRecord.PipelineName
                        ?? RuntimeAnalysisPipelineDefinitionFactory.PipelineName,
                    attemptRecord.Status.ToString(),
                    cancellationToken)
                .ConfigureAwait(false);

            // Provider already validates before the OpenAI step completes; keep the
            // adapter-level validation as an explicit response boundary as well.
            _resultValidator.Validate(
                result.Result,
                request.Snapshot);

            if (string.Equals(
                    result.HumanApproval.Status,
                    RuntimeAnalysisHumanApprovalStatuses.Pending,
                    StringComparison.Ordinal))
            {
                await _approvalStore.AttachInitialRunIdAsync(
                        executionId,
                        handle.RunId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return result;
        }
    }
}
