using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Runtime
{
    public sealed class RuntimeAnalysisHumanApprovalService :
        IRuntimeAnalysisHumanApprovalService
    {
        private readonly IAiRuntimePipelineBackgroundController _controller;
        private readonly IRuntimeAnalysisHumanApprovalStore _approvalStore;
        private readonly RuntimeAnalysisExecutionResultReader _resultReader;
        private readonly RuntimeAnalysisRuntimeOptions _options;

        public RuntimeAnalysisHumanApprovalService(
            IAiRuntimePipelineBackgroundController controller,
            IRuntimeAnalysisHumanApprovalStore approvalStore,
            RuntimeAnalysisExecutionResultReader resultReader,
            RuntimeAnalysisRuntimeOptions options)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _approvalStore = approvalStore ?? throw new ArgumentNullException(nameof(approvalStore));
            _resultReader = resultReader ?? throw new ArgumentNullException(nameof(resultReader));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public async Task<RuntimeAnalysisRuntimeExecutionResult> DecideAsync(
            string executionId,
            string decision,
            string decidedBy,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(decidedBy);

            var targetStatus = NormalizeDecision(decision);

            var record = await _approvalStore.DecideAsync(
                    executionId,
                    targetStatus,
                    decidedBy,
                    cancellationToken)
                .ConfigureAwait(false);

            var approvalStepStatus = await _resultReader.GetApprovalStepStatusAsync(
                    executionId,
                    cancellationToken)
                .ConfigureAwait(false);

            // Idempotent duplicate client delivery after a successful continuation.
            if (approvalStepStatus == AiStepExecutionStatus.Completed)
            {
                return await _resultReader.ReadAsync(
                        record.InitialRunId ?? "unknown",
                        continuationRunId: null,
                        executionId,
                        RuntimeAnalysisPipelineDefinitionFactory.PipelineName,
                        AiExecutionStatus.Completed.ToString(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (approvalStepStatus != AiStepExecutionStatus.WaitingForExternal)
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Human approval step for execution '{executionId}' cannot continue from status '{approvalStepStatus}'.");
            }

            var handle = await _controller.EnqueueAsync(
                    new AiRuntimePipelineRunRequest
                    {
                        PipelineName = RuntimeAnalysisPipelineDefinitionFactory.PipelineName,
                        ExternalWaitContinuation = new AiRuntimeExternalWaitContinuation
                        {
                            ExecutionId = executionId,
                            StepName = record.StepName,
                            ContinuationId = record.ContinuationId
                        },
                        ExecutionContextSnapshot = record.ExecutionContextSnapshot,
                        Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["source"] = "runtime-analysis-api",
                            ["operation"] = "human-approval-continuation",
                            ["approval.status"] = targetStatus
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
                    $"Human approval continuation did not complete within {_options.ExecutionTimeout.TotalSeconds:0} seconds.",
                    exception);
            }

            if (!string.Equals(
                    finalRecord.ExecutionId,
                    executionId,
                    StringComparison.Ordinal))
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Human approval continuation changed durable execution identity from '{executionId}' to '{finalRecord.ExecutionId}'.");
            }

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

        private static string NormalizeDecision(
            string decision)
        {
            if (string.Equals(
                    decision,
                    RuntimeAnalysisHumanApprovalDecisions.Approve,
                    StringComparison.OrdinalIgnoreCase))
            {
                return RuntimeAnalysisHumanApprovalStatuses.Approved;
            }

            if (string.Equals(
                    decision,
                    RuntimeAnalysisHumanApprovalDecisions.Reject,
                    StringComparison.OrdinalIgnoreCase))
            {
                return RuntimeAnalysisHumanApprovalStatuses.Rejected;
            }

            throw new ArgumentException(
                "Human approval decision must be 'approve' or 'reject'.",
                nameof(decision));
        }
    }
}
