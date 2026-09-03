using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Stores;
using MultiplexedRbac.Sample.Crm.Api.AI.Models;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Runtime
{
    public sealed class RuntimeAnalysisExecutionResultReader
    {
        private static readonly JsonSerializerOptions SerializerOptions =
            new()
            {
                PropertyNameCaseInsensitive = true
            };

        private readonly IAiDagExecutionStore _dagStore;
        private readonly IRuntimeAnalysisHumanApprovalStore _approvalStore;

        public RuntimeAnalysisExecutionResultReader(
            IAiDagExecutionStore dagStore,
            IRuntimeAnalysisHumanApprovalStore approvalStore)
        {
            _dagStore =
                dagStore
                ?? throw new ArgumentNullException(
                    nameof(dagStore));
            _approvalStore =
                approvalStore
                ?? throw new ArgumentNullException(
                    nameof(approvalStore));
        }

        public async Task<RuntimeAnalysisRuntimeExecutionResult> ReadAsync(
            string runId,
            string? continuationRunId,
            string executionId,
            string pipelineName,
            string runtimeStatus,
            CancellationToken cancellationToken)
        {
            var state = await GetStateAsync(
                    executionId,
                    cancellationToken)
                .ConfigureAwait(false);

            var analysis = DeserializeRequiredOutput<RuntimeAnalysisResult>(
                state,
                RuntimeAnalysisPipelineDefinitionFactory.AnalyzeStepName,
                "AI analysis");

            var policyValidation =
                DeserializeRequiredOutput<RuntimeAnalysisScenarioPolicyValidationResult>(
                    state,
                    RuntimeAnalysisPipelineDefinitionFactory.ValidateScenarioStepName,
                    "policy validation");

            var approval = await ReadApprovalAsync(
                    state,
                    executionId,
                    cancellationToken)
                .ConfigureAwait(false);

            var approvalStep = state.Steps[
                RuntimeAnalysisPipelineDefinitionFactory.AwaitHumanApprovalStepName];

            return new RuntimeAnalysisRuntimeExecutionResult
            {
                RunId = runId,
                ContinuationRunId = continuationRunId,
                ExecutionId = executionId,
                PipelineName = pipelineName,
                StepName = ResolveCurrentStepName(
                    approvalStep),
                RuntimeStatus = runtimeStatus,
                Result = analysis,
                PolicyValidation = policyValidation,
                HumanApproval = approval
            };
        }

        public async Task<AiStepExecutionStatus> GetApprovalStepStatusAsync(
            string executionId,
            CancellationToken cancellationToken)
        {
            var state = await GetStateAsync(
                    executionId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!state.Steps.TryGetValue(
                    RuntimeAnalysisPipelineDefinitionFactory.AwaitHumanApprovalStepName,
                    out var step))
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Execution '{executionId}' does not contain human approval step '{RuntimeAnalysisPipelineDefinitionFactory.AwaitHumanApprovalStepName}'.");
            }

            return step.Status;
        }

        private async Task<AiExecutionState> GetStateAsync(
            string executionId,
            CancellationToken cancellationToken)
        {
            return await _dagStore.GetStateAsync(
                       executionId,
                       cancellationToken)
                   .ConfigureAwait(false)
                   ?? throw new RuntimeAnalysisRuntimeExecutionException(
                       $"Runtime analysis execution '{executionId}' has no persisted DAG state.");
        }

        private async Task<RuntimeAnalysisHumanApprovalResult> ReadApprovalAsync(
            AiExecutionState state,
            string executionId,
            CancellationToken cancellationToken)
        {
            if (!state.Steps.TryGetValue(
                    RuntimeAnalysisPipelineDefinitionFactory.AwaitHumanApprovalStepName,
                    out var step))
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Execution '{executionId}' does not contain human approval step '{RuntimeAnalysisPipelineDefinitionFactory.AwaitHumanApprovalStepName}'.");
            }

            if (step.Status == AiStepExecutionStatus.WaitingForExternal)
            {
                var record = await _approvalStore.GetAsync(
                        executionId,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new RuntimeAnalysisRuntimeExecutionException(
                        $"Execution '{executionId}' is waiting for human approval without a durable approval record.");

                return new RuntimeAnalysisHumanApprovalResult
                {
                    Required = true,
                    Status = record.Status,
                    ContinuationId = record.ContinuationId,
                    RequestedAtUtc = record.RequestedAtUtc,
                    DecidedAtUtc = record.DecidedAtUtc,
                    DecidedBy = record.DecidedBy,
                    Message = "Deterministic policies passed. Explicit human approval is required before execution can continue."
                };
            }

            if (step.Status == AiStepExecutionStatus.Completed)
            {
                return DeserializeRequiredStepOutput<RuntimeAnalysisHumanApprovalResult>(
                    step,
                    "human approval");
            }

            throw new RuntimeAnalysisRuntimeExecutionException(
                $"Human approval step for execution '{executionId}' is in unexpected status '{step.Status}'.");
        }

        private static string ResolveCurrentStepName(
            AiStepState approvalStep)
        {
            return approvalStep.Status == AiStepExecutionStatus.WaitingForExternal
                ? RuntimeAnalysisPipelineDefinitionFactory.AwaitHumanApprovalStepName
                : approvalStep.StepName;
        }

        private static T DeserializeRequiredOutput<T>(
            AiExecutionState state,
            string stepName,
            string label)
        {
            if (!state.Steps.TryGetValue(
                    stepName,
                    out var step))
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Runtime analysis execution '{state.ExecutionId}' does not contain step '{stepName}'.");
            }

            return DeserializeRequiredStepOutput<T>(
                step,
                label);
        }

        private static T DeserializeRequiredStepOutput<T>(
            AiStepState step,
            string label)
        {
            var result = step.Result;

            if (result is null)
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Runtime analysis {label} step '{step.StepName}' has no persisted result. Status='{step.Status}', Error='{step.Error ?? string.Empty}'.");
            }

            if (!result.Success)
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Runtime analysis {label} step '{step.StepName}' failed: {result.Error ?? step.Error ?? "unknown error"}.");
            }

            if (string.IsNullOrWhiteSpace(
                    result.Output))
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Runtime analysis {label} step '{step.StepName}' completed without structured output.");
            }

            try
            {
                return JsonSerializer.Deserialize<T>(
                           result.Output,
                           SerializerOptions)
                       ?? throw new RuntimeAnalysisRuntimeExecutionException(
                           $"Runtime analysis {label} output deserialized to null.");
            }
            catch (JsonException exception)
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Runtime analysis {label} output is invalid JSON.",
                    exception);
            }
        }
    }
}
