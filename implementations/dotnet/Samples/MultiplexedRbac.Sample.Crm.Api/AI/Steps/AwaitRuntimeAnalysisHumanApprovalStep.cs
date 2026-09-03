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
    [AiStep(RuntimeAnalysisStepKeys.AwaitHumanApproval)]
    public sealed class AwaitRuntimeAnalysisHumanApprovalStep : IAiStep
    {
        public string Name =>
            RuntimeAnalysisStepKeys.AwaitHumanApproval;

        public async Task<AiStepResult> ExecuteAsync(
            AiStepExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(
                context);

            var helper = context.GetHelper();

            var policyValidationJson =
                await helper.GetRequiredInputAsync<string>(
                        RuntimeAnalysisStepInputKeys.PolicyValidationJson,
                        cancellationToken)
                    .ConfigureAwait(false);

            RuntimeAnalysisScenarioPolicyValidationResult validation;

            try
            {
                validation =
                    JsonSerializer.Deserialize<RuntimeAnalysisScenarioPolicyValidationResult>(
                        policyValidationJson)
                    ?? throw new InvalidOperationException(
                        "Policy validation result deserialized to null.");
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    "Policy validation result is invalid JSON.",
                    exception);
            }

            if (!validation.Allowed)
            {
                return Complete(
                    new RuntimeAnalysisHumanApprovalResult
                    {
                        Required = false,
                        Status = RuntimeAnalysisHumanApprovalStatuses.NotRequired,
                        Message =
                            "Human approval is not available because deterministic policy validation denied the AI proposal."
                    });
            }

            if (!validation.RequiresHumanApproval)
            {
                return Complete(
                    new RuntimeAnalysisHumanApprovalResult
                    {
                        Required = false,
                        Status = RuntimeAnalysisHumanApprovalStatuses.NotRequired,
                        Message =
                            "The pipeline policy definition does not require human approval."
                    });
            }

            var executionContextSnapshot =
                helper.ExecutionContextSnapshot
                ?? throw new InvalidOperationException(
                    "Human approval external wait requires the persisted execution context snapshot.");

            var store = context.Services
                .GetRequiredService<IRuntimeAnalysisHumanApprovalStore>();

            var existing = await store.GetAsync(
                    helper.ExecutionId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                var continuationId = CreateContinuationId(
                    helper.ExecutionId,
                    helper.StepName);

                existing = await store.CreatePendingAsync(
                        new RuntimeAnalysisHumanApprovalRecord
                        {
                            ExecutionId = helper.ExecutionId,
                            StepName = helper.StepName,
                            ContinuationId = continuationId,
                            Status = RuntimeAnalysisHumanApprovalStatuses.Pending,
                            PolicyValidation = validation,
                            ExecutionContextSnapshot = executionContextSnapshot,
                            RequestedAtUtc = DateTimeOffset.UtcNow
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (string.Equals(
                    existing.Status,
                    RuntimeAnalysisHumanApprovalStatuses.Pending,
                    StringComparison.Ordinal))
            {
                // The application-owned durable approval record is committed above.
                // Only now may the runtime step enter WaitingForExternal.
                return AiStepResult.Park(
                    "Waiting for explicit human approval.");
            }

            if (string.Equals(
                    existing.Status,
                    RuntimeAnalysisHumanApprovalStatuses.Approved,
                    StringComparison.Ordinal)
                || string.Equals(
                    existing.Status,
                    RuntimeAnalysisHumanApprovalStatuses.Rejected,
                    StringComparison.Ordinal))
            {
                return Complete(
                    ToResult(existing));
            }

            throw new InvalidOperationException(
                $"Unsupported human approval status '{existing.Status}' for execution '{helper.ExecutionId}'.");
        }

        private static AiStepResult Complete(
            RuntimeAnalysisHumanApprovalResult result)
        {
            return AiStepResult.Ok(
                output: JsonSerializer.Serialize(
                    result),
                data: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["approval.status"] = result.Status,
                    ["approval.required"] = result.Required,
                    ["approval.decidedBy"] = result.DecidedBy
                });
        }

        private static RuntimeAnalysisHumanApprovalResult ToResult(
            RuntimeAnalysisHumanApprovalRecord record)
        {
            return new RuntimeAnalysisHumanApprovalResult
            {
                Required = true,
                Status = record.Status,
                ContinuationId = record.ContinuationId,
                RequestedAtUtc = record.RequestedAtUtc,
                DecidedAtUtc = record.DecidedAtUtc,
                DecidedBy = record.DecidedBy,
                Message = string.Equals(
                        record.Status,
                        RuntimeAnalysisHumanApprovalStatuses.Approved,
                        StringComparison.Ordinal)
                    ? "Human approval accepted. The same durable execution may continue."
                    : "Human approval rejected. The AI proposal will not execute."
            };
        }

        private static string CreateContinuationId(
            string executionId,
            string stepName)
        {
            return $"human-approval:{executionId}:{stepName}:v1";
        }
    }
}
