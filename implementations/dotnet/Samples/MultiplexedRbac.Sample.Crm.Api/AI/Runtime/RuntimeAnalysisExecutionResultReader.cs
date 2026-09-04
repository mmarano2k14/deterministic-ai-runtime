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
        private readonly IRuntimeAnalysisScenarioExecutionStore _executionStore;
        private readonly RuntimeAnalysisChildDagEvidenceReader _childDagReader;

        public RuntimeAnalysisExecutionResultReader(
            IAiDagExecutionStore dagStore,
            IRuntimeAnalysisHumanApprovalStore approvalStore,
            IRuntimeAnalysisScenarioExecutionStore executionStore,
            RuntimeAnalysisChildDagEvidenceReader childDagReader)
        {
            _dagStore =
                dagStore
                ?? throw new ArgumentNullException(
                    nameof(dagStore));
            _approvalStore =
                approvalStore
                ?? throw new ArgumentNullException(
                    nameof(approvalStore));
            _executionStore =
                executionStore
                ?? throw new ArgumentNullException(
                    nameof(executionStore));
            _childDagReader =
                childDagReader
                ?? throw new ArgumentNullException(
                    nameof(childDagReader));
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

            var analysis =
                DeserializeRequiredOutput<RuntimeAnalysisResult>(
                    state,
                    RuntimeAnalysisPipelineDefinitionFactory.AnalyzeStepName,
                    "AI analysis");

            var policyValidation =
                DeserializeRequiredOutput<
                    RuntimeAnalysisScenarioPolicyValidationResult>(
                    state,
                    RuntimeAnalysisPipelineDefinitionFactory
                        .ValidateScenarioStepName,
                    "policy validation");

            var approval = await ReadApprovalAsync(
                    state,
                    executionId,
                    cancellationToken)
                .ConfigureAwait(false);

            var scenarioExecution =
                await ReadScenarioExecutionAsync(
                        state,
                        executionId,
                        policyValidation,
                        cancellationToken)
                    .ConfigureAwait(false);

            var childDag = await _childDagReader.ReadAsync(
                    state,
                    executionId,
                    cancellationToken)
                .ConfigureAwait(false);

            var verification = ReadVerification(
                state,
                scenarioExecution);

            return new RuntimeAnalysisRuntimeExecutionResult
            {
                RunId = runId,
                ContinuationRunId = continuationRunId,
                ExecutionId = executionId,
                PipelineName = pipelineName,
                StepName = ResolveCurrentStepName(
                    state),
                RuntimeStatus = runtimeStatus,
                Result = analysis,
                PolicyValidation = policyValidation,
                HumanApproval = approval,
                ScenarioExecution = scenarioExecution,
                ChildDag = childDag,
                Verification = verification
            };
        }

        public async Task<AiStepExecutionStatus> GetApprovalStepStatusAsync(
            string executionId,
            CancellationToken cancellationToken)
        {
            return await GetStepStatusAsync(
                    executionId,
                    RuntimeAnalysisPipelineDefinitionFactory
                        .AwaitHumanApprovalStepName,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<AiStepExecutionStatus>
            GetScenarioExecutionStepStatusAsync(
                string executionId,
                CancellationToken cancellationToken)
        {
            return await GetStepStatusAsync(
                    executionId,
                    RuntimeAnalysisPipelineDefinitionFactory
                        .ExecuteApprovedScenarioStepName,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<AiStepExecutionStatus> GetStepStatusAsync(
            string executionId,
            string stepName,
            CancellationToken cancellationToken)
        {
            var state = await GetStateAsync(
                    executionId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!state.Steps.TryGetValue(
                    stepName,
                    out var step))
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Execution '{executionId}' does not contain step '{stepName}'.");
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
                    RuntimeAnalysisPipelineDefinitionFactory
                        .AwaitHumanApprovalStepName,
                    out var step))
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Execution '{executionId}' does not contain human approval step.");
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
                    Message =
                        "Deterministic policies passed. Explicit human approval is required before execution can continue."
                };
            }

            if (step.Status == AiStepExecutionStatus.Completed)
            {
                return DeserializeRequiredStepOutput<
                    RuntimeAnalysisHumanApprovalResult>(
                    step,
                    "human approval");
            }

            throw new RuntimeAnalysisRuntimeExecutionException(
                $"Human approval step for execution '{executionId}' is in unexpected status '{step.Status}'.");
        }

        private async Task<RuntimeAnalysisScenarioExecutionResult>
            ReadScenarioExecutionAsync(
                AiExecutionState state,
                string executionId,
                RuntimeAnalysisScenarioPolicyValidationResult policyValidation,
                CancellationToken cancellationToken)
        {
            if (!state.Steps.TryGetValue(
                    RuntimeAnalysisPipelineDefinitionFactory
                        .ExecuteApprovedScenarioStepName,
                    out var step))
            {
                return CreateNotStartedScenarioExecution(
                    policyValidation,
                    "Approved scenario execution step has not been created yet.");
            }

            if (step.Status == AiStepExecutionStatus.None)
            {
                return CreateNotStartedScenarioExecution(
                    policyValidation,
                    "Approved scenario execution has not started because the human-approval boundary has not completed yet.");
            }

            if (step.Status == AiStepExecutionStatus.WaitingForExternal)
            {
                var record = await _executionStore.GetAsync(
                        executionId,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new RuntimeAnalysisRuntimeExecutionException(
                        $"Execution '{executionId}' is waiting for approved scenario execution without a durable execution record.");

                return new RuntimeAnalysisScenarioExecutionResult
                {
                    Required = true,
                    Status = record.Status,
                    ContinuationId = record.ContinuationId,
                    RequestedAtUtc = record.RequestedAtUtc,
                    CompletedAtUtc = record.CompletedAtUtc,
                    Scenario = record.Scenario,
                    PlanKey = record.PlanKey,
                    Observation = record.Observation,
                    CompletedBy = record.CompletedBy,
                    Message =
                        "Human approval accepted. The runtime is parked while the existing Next.js BurstController executes the approved scenario."
                };
            }

            if (step.Status == AiStepExecutionStatus.Completed)
            {
                return DeserializeRequiredStepOutput<
                    RuntimeAnalysisScenarioExecutionResult>(
                    step,
                    "approved scenario execution");
            }

            return new RuntimeAnalysisScenarioExecutionResult
            {
                Required = true,
                Status =
                    RuntimeAnalysisScenarioExecutionStatuses.Pending,
                Scenario = policyValidation.Scenario,
                PlanKey = policyValidation.PlanKey,
                Message =
                    $"Approved scenario execution step is currently '{step.Status}'."
            };
        }

        private static RuntimeAnalysisScenarioExecutionResult
            CreateNotStartedScenarioExecution(
                RuntimeAnalysisScenarioPolicyValidationResult policyValidation,
                string message)
        {
            return new RuntimeAnalysisScenarioExecutionResult
            {
                Required = false,
                Status =
                    RuntimeAnalysisScenarioExecutionStatuses.NotStarted,
                Scenario = policyValidation.Scenario,
                PlanKey = policyValidation.PlanKey,
                Message = message
            };
        }

        private static RuntimeAnalysisVerificationResult ReadVerification(
            AiExecutionState state,
            RuntimeAnalysisScenarioExecutionResult scenarioExecution)
        {
            if (state.Steps.TryGetValue(
                    RuntimeAnalysisPipelineDefinitionFactory
                        .VerifyScenarioOutcomeStepName,
                    out var step)
                && step.Status == AiStepExecutionStatus.Completed)
            {
                return DeserializeRequiredStepOutput<
                    RuntimeAnalysisVerificationResult>(
                    step,
                    "outcome verification");
            }

            if (string.Equals(
                    scenarioExecution.Status,
                    RuntimeAnalysisScenarioExecutionStatuses.NotExecuted,
                    StringComparison.Ordinal))
            {
                return new RuntimeAnalysisVerificationResult
                {
                    Status =
                        RuntimeAnalysisVerificationStatuses.Skipped,
                    Executed = false,
                    ExpectedRequests =
                        scenarioExecution.Scenario.TotalRequests,
                    Summary =
                        "Verification was skipped because the proposed scenario did not cross the execution boundary."
                };
            }

            return new RuntimeAnalysisVerificationResult
            {
                Status =
                    RuntimeAnalysisVerificationStatuses.Pending,
                Executed = false,
                ExpectedRequests =
                    scenarioExecution.Scenario.TotalRequests,
                Summary =
                    string.Equals(
                        scenarioExecution.Status,
                        RuntimeAnalysisScenarioExecutionStatuses.Completed,
                        StringComparison.Ordinal)
                        ? "Observed scenario execution has been returned; deterministic verification is completing in the same DAG."
                        : "Verification is waiting for the approved scenario result to resume the DAG."
            };
        }

        private static string ResolveCurrentStepName(
            AiExecutionState state)
        {
            var orderedNames = new[]
            {
                RuntimeAnalysisPipelineDefinitionFactory.AnalyzeStepName,
                RuntimeAnalysisPipelineDefinitionFactory.ValidateScenarioStepName,
                RuntimeAnalysisPipelineDefinitionFactory.AwaitHumanApprovalStepName,
                RuntimeAnalysisPipelineDefinitionFactory.ExecuteApprovedScenarioStepName,
                RuntimeAnalysisChildDagDefinitionFactory.ChildDagStepName,
                RuntimeAnalysisPipelineDefinitionFactory.VerifyScenarioOutcomeStepName
            };

            foreach (var stepName in orderedNames)
            {
                if (!state.Steps.TryGetValue(
                        stepName,
                        out var step))
                {
                    continue;
                }

                if (step.Status == AiStepExecutionStatus.WaitingForExternal)
                {
                    return stepName;
                }
            }

            for (var index = orderedNames.Length - 1;
                 index >= 0;
                 index--)
            {
                if (state.Steps.TryGetValue(
                        orderedNames[index],
                        out var step)
                    && step.Status == AiStepExecutionStatus.Completed)
                {
                    return step.StepName;
                }
            }

            return RuntimeAnalysisPipelineDefinitionFactory.AnalyzeStepName;
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
