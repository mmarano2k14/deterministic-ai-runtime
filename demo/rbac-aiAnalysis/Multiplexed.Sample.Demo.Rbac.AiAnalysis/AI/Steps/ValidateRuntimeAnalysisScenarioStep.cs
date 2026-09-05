using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Context;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.AI.Policies;
using Multiplexed.AI.Runtime.Execution.Context;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Policies;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Runtime;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Steps
{
    [AiStep(RuntimeAnalysisStepKeys.ValidateSuggestedScenario)]
    public sealed class ValidateRuntimeAnalysisScenarioStep : IAiStep
    {
        public string Name =>
            RuntimeAnalysisStepKeys.ValidateSuggestedScenario;

        public async Task<AiStepResult> ExecuteAsync(
            AiStepExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(
                context);

            var helper = context.GetHelper();

            var analysisResultJson = await helper.GetRequiredInputAsync<string>(
                    RuntimeAnalysisStepInputKeys.AnalysisResultJson,
                    cancellationToken)
                .ConfigureAwait(false);

            RuntimeAnalysisResult analysisResult;

            try
            {
                analysisResult =
                    JsonSerializer.Deserialize<RuntimeAnalysisResult>(
                        analysisResultJson)
                    ?? throw new InvalidOperationException(
                        "Upstream runtime analysis result deserialized to null.");
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    "Upstream runtime analysis result is invalid JSON.",
                    exception);
            }

            var policyRegistry = context.Services
                .GetRequiredService<IAiPolicyRegistry>();

            var observability = context.Services
                .GetRequiredService<IAiRuntimeObservability>();

            var policyEngine =
                new RuntimeAnalysisScenarioValidationPolicyEngine(
                    policyRegistry,
                    context,
                    observability);

            var evaluation = await policyEngine.ValidateAsync(
                    analysisResult.SuggestedScenario,
                    cancellationToken)
                .ConfigureAwait(false);

            if (evaluation.Results.Count !=
                evaluation.ConfiguredPolicies.Count)
            {
                throw new InvalidOperationException(
                    "Scenario validation did not return one decision per dynamically configured policy.");
            }

            var results = evaluation.Results.ToArray();

            var decisions =
                evaluation.ConfiguredPolicies
                    .Select(
                        (policyDefinition, index) =>
                        {
                            var result = results[index];

                            return new RuntimeAnalysisScenarioPolicyDecision
                            {
                                PolicyKey = policyDefinition.Name,
                                ResultKind = result.Kind.ToString(),
                                Allowed = result.IsSuccess,
                                Message = result.Message ?? string.Empty
                            };
                        })
                    .ToArray();

            var allowed = decisions.All(
                decision => decision.Allowed);

            var validation =
                new RuntimeAnalysisScenarioPolicyValidationResult
                {
                    Allowed = allowed,
                    RequiresHumanApproval =
                        allowed
                        && evaluation.Definition.RequireHumanApproval,
                    PlanKey = evaluation.Definition.PlanKey,
                    Scenario = analysisResult.SuggestedScenario,
                    PolicyDecisions = decisions
                };

            return AiStepResult.Ok(
                output: JsonSerializer.Serialize(
                    validation),
                data: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["runtimeStepKey"] = Name,
                    ["executionId"] = helper.ExecutionId,
                    ["stepName"] = helper.StepName,
                    ["configuredPolicyCount"] =
                        evaluation.ConfiguredPolicies.Count,
                    ["allowed"] = allowed,
                    ["requiresHumanApproval"] =
                        validation.RequiresHumanApproval,
                    ["planKey"] = validation.PlanKey
                });
        }
    }
}
