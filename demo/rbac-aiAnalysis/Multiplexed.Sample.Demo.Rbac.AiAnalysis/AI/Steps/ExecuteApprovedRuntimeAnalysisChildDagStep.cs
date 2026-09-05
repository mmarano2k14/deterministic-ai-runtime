using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Context;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Execution;
using Multiplexed.AI.Runtime.Execution.Context;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Runtime;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Steps
{
    /// <summary>
    /// Sample-level approval guard around the native execution.child-dag step.
    /// The runtime primitive remains authoritative for actual child composition.
    /// </summary>
    [AiStep(RuntimeAnalysisStepKeys.ExecuteApprovedChildDag)]
    public sealed class ExecuteApprovedRuntimeAnalysisChildDagStep : IAiStep
    {
        public string Name =>
            RuntimeAnalysisStepKeys.ExecuteApprovedChildDag;

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

            var approved =
                validation.Allowed
                && string.Equals(
                    approval.Status,
                    RuntimeAnalysisHumanApprovalStatuses.Approved,
                    StringComparison.Ordinal);

            var approvalNotRequired =
                validation.Allowed
                && !validation.RequiresHumanApproval
                && !approval.Required
                && string.Equals(
                    approval.Status,
                    RuntimeAnalysisHumanApprovalStatuses.NotRequired,
                    StringComparison.Ordinal);

            if (!approved
                && !approvalNotRequired)
            {
                return AiStepResult.Ok(
                    output:
                        "No child execution was created because the next decision did not cross the approval boundary.",
                    data: new Dictionary<string, object?>(
                        StringComparer.Ordinal)
                    {
                        ["child.created"] = false,
                        ["approval.status"] = approval.Status,
                        ["policy.allowed"] = validation.Allowed
                    });
            }

            var registry = context.Services
                .GetRequiredService<IAiStepRegistry>();

            var nativeChildStep = registry.Resolve(
                ExecuteChildDagStep.StepKey);

            if (ReferenceEquals(
                    nativeChildStep,
                    this))
            {
                throw new InvalidOperationException(
                    "Approval-guarded Child DAG step resolved itself instead of the native execution.child-dag primitive.");
            }

            return await nativeChildStep.ExecuteAsync(
                    context,
                    cancellationToken)
                .ConfigureAwait(false);
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
    }
}
