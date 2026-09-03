using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Context;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Execution.Context;
using MultiplexedRbac.Sample.Crm.Api.AI.Models;
using MultiplexedRbac.Sample.Crm.Api.AI.Runtime;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Steps
{
    [AiStep(RuntimeAnalysisStepKeys.ExecuteApprovedScenario)]
    public sealed class ExecuteApprovedRuntimeAnalysisScenarioStep : IAiStep
    {
        public string Name =>
            RuntimeAnalysisStepKeys.ExecuteApprovedScenario;

        public async Task<AiStepResult> ExecuteAsync(
            AiStepExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(
                context);

            var helper = context.GetHelper();

            var validation = Deserialize<
                RuntimeAnalysisScenarioPolicyValidationResult>(
                await helper.GetRequiredInputAsync<string>(
                        RuntimeAnalysisStepInputKeys.PolicyValidationJson,
                        cancellationToken)
                    .ConfigureAwait(false),
                "policy validation");

            var approval = Deserialize<
                RuntimeAnalysisHumanApprovalResult>(
                await helper.GetRequiredInputAsync<string>(
                        RuntimeAnalysisStepInputKeys.HumanApprovalJson,
                        cancellationToken)
                    .ConfigureAwait(false),
                "human approval");

            if (!validation.Allowed)
            {
                return Complete(
                    new RuntimeAnalysisScenarioExecutionResult
                    {
                        Required = false,
                        Status =
                            RuntimeAnalysisScenarioExecutionStatuses.NotExecuted,
                        Scenario = validation.Scenario,
                        PlanKey = validation.PlanKey,
                        Message =
                            "Scenario was not executed because deterministic policy validation denied the AI proposal."
                    });
            }

            var approved =
                string.Equals(
                    approval.Status,
                    RuntimeAnalysisHumanApprovalStatuses.Approved,
                    StringComparison.Ordinal);

            var approvalNotRequired =
                !approval.Required
                && string.Equals(
                    approval.Status,
                    RuntimeAnalysisHumanApprovalStatuses.NotRequired,
                    StringComparison.Ordinal)
                && !validation.RequiresHumanApproval;

            if (!approved
                && !approvalNotRequired)
            {
                return Complete(
                    new RuntimeAnalysisScenarioExecutionResult
                    {
                        Required = false,
                        Status =
                            RuntimeAnalysisScenarioExecutionStatuses.NotExecuted,
                        Scenario = validation.Scenario,
                        PlanKey = validation.PlanKey,
                        Message =
                            "Scenario was not executed because human approval was not granted."
                    });
            }

            var executionContextSnapshot =
                helper.ExecutionContextSnapshot
                ?? throw new InvalidOperationException(
                    "Approved scenario external execution requires the persisted execution context snapshot.");

            var executionStore = context.Services
                .GetRequiredService<IRuntimeAnalysisScenarioExecutionStore>();

            var existing = await executionStore.GetAsync(
                    helper.ExecutionId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                var approvalStore = context.Services
                    .GetRequiredService<IRuntimeAnalysisHumanApprovalStore>();

                var approvalRecord = await approvalStore.GetAsync(
                        helper.ExecutionId,
                        cancellationToken)
                    .ConfigureAwait(false);

                existing = await executionStore.CreatePendingAsync(
                        new RuntimeAnalysisScenarioExecutionRecord
                        {
                            ExecutionId = helper.ExecutionId,
                            StepName = helper.StepName,
                            ContinuationId =
                                CreateContinuationId(
                                    helper.ExecutionId,
                                    helper.StepName),
                            InitialRunId =
                                approvalRecord?.InitialRunId,
                            Status =
                                RuntimeAnalysisScenarioExecutionStatuses.Pending,
                            Scenario = validation.Scenario,
                            PlanKey = validation.PlanKey,
                            ExecutionContextSnapshot =
                                executionContextSnapshot,
                            RequestedAtUtc =
                                DateTimeOffset.UtcNow
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (string.Equals(
                    existing.Status,
                    RuntimeAnalysisScenarioExecutionStatuses.Pending,
                    StringComparison.Ordinal))
            {
                // The application-owned execution request is durable in Redis.
                // The runtime may now release capacity and wait for the existing
                // Next.js BurstController to report the observed result.
                return AiStepResult.Park(
                    "Waiting for the approved scenario to execute through the existing client burst runner.");
            }

            if (string.Equals(
                    existing.Status,
                    RuntimeAnalysisScenarioExecutionStatuses.Completed,
                    StringComparison.Ordinal))
            {
                return Complete(
                    ToResult(
                        existing));
            }

            throw new InvalidOperationException(
                $"Unsupported approved scenario execution status '{existing.Status}' for execution '{helper.ExecutionId}'.");
        }

        private static T Deserialize<T>(
            string json,
            string label)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(
                           json)
                       ?? throw new InvalidOperationException(
                           $"{label} deserialized to null.");
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    $"{label} is invalid JSON.",
                    exception);
            }
        }

        private static AiStepResult Complete(
            RuntimeAnalysisScenarioExecutionResult result)
        {
            return AiStepResult.Ok(
                output: JsonSerializer.Serialize(
                    result),
                data: new Dictionary<string, object?>(
                    StringComparer.Ordinal)
                {
                    ["scenario.execution.status"] =
                        result.Status,
                    ["scenario.execution.required"] =
                        result.Required,
                    ["scenario.execution.completed"] =
                        result.Observation?.Completed,
                    ["scenario.execution.ok"] =
                        result.Observation?.Ok
                });
        }

        private static RuntimeAnalysisScenarioExecutionResult ToResult(
            RuntimeAnalysisScenarioExecutionRecord record)
        {
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
                    "Approved scenario executed through the existing Next.js BurstController and the observed result was returned to the same durable ExecutionId."
            };
        }

        private static string CreateContinuationId(
            string executionId,
            string stepName)
        {
            return $"scenario-execution:{executionId}:{stepName}:v1";
        }
    }
}
