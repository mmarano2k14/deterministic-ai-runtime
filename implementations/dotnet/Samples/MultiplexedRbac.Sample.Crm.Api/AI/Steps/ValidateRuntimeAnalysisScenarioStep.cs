using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Context;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.AI.Policies;
using Multiplexed.AI.Runtime.Execution.Context;
using MultiplexedRbac.Sample.Crm.Api.AI.Models;
using MultiplexedRbac.Sample.Crm.Api.AI.Policies;
using MultiplexedRbac.Sample.Crm.Api.AI.Runtime;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Steps
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

            var scenarioJson = await helper.GetConfigAsync<string>(
                    RuntimeAnalysisStepConfigKeys.SuggestedScenarioJson,
                    cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(
                    scenarioJson))
            {
                throw new InvalidOperationException(
                    $"Scenario validation step '{helper.StepName}' is missing '{RuntimeAnalysisStepConfigKeys.SuggestedScenarioJson}'.");
            }

            RuntimeAnalysisSuggestedScenario scenario;

            try
            {
                scenario =
                    JsonSerializer.Deserialize<RuntimeAnalysisSuggestedScenario>(
                        scenarioJson)
                    ?? throw new InvalidOperationException(
                        "Suggested scenario deserialized to null.");
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    "Suggested scenario is invalid JSON.",
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
                    scenario,
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
                                PolicyKey =
                                    policyDefinition.Name,
                                ResultKind =
                                    result.Kind.ToString(),
                                Allowed =
                                    result.IsSuccess,
                                Message =
                                    result.Message
                                    ?? string.Empty
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
                    PlanKey =
                        evaluation.Definition.PlanKey,
                    Scenario = scenario,
                    PolicyDecisions = decisions
                };

            return AiStepResult.Ok(
                output: JsonSerializer.Serialize(
                    validation),
                data: new Dictionary<string, object?>(
                    StringComparer.Ordinal)
                {
                    ["runtimeStepKey"] = Name,
                    ["executionId"] = helper.ExecutionId,
                    ["stepName"] = helper.StepName,
                    ["configuredPolicyCount"] =
                        evaluation.ConfiguredPolicies.Count,
                    ["allowed"] = allowed,
                    ["requiresHumanApproval"] =
                        validation.RequiresHumanApproval,
                    ["planKey"] =
                        validation.PlanKey
                });
        }
    }
}
