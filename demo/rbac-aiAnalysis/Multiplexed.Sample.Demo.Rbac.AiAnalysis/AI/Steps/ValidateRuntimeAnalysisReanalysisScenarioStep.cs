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
    [AiStep(RuntimeAnalysisStepKeys.ValidateReanalysisScenario)]
    public sealed class ValidateRuntimeAnalysisReanalysisScenarioStep : IAiStep
    {
        public string Name =>
            RuntimeAnalysisStepKeys.ValidateReanalysisScenario;

        public async Task<AiStepResult> ExecuteAsync(
            AiStepExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(
                context);

            var helper = context.GetHelper();

            var result = Deserialize<RuntimeAnalysisReanalysisResult>(
                await helper.GetRequiredInputAsync<string>(
                        RuntimeAnalysisStepInputKeys.ReanalysisResultJson,
                        cancellationToken)
                    .ConfigureAwait(false),
                "runtime re-analysis");

            var depth = await helper.GetRequiredConfigAsync<int>(
                    RuntimeAnalysisStepConfigKeys.ChildDagDepth,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!result.ShouldContinue)
            {
                return Complete(
                    new RuntimeAnalysisScenarioPolicyValidationResult
                    {
                        Allowed = false,
                        RequiresHumanApproval = false,
                        PlanKey = "read",
                        Scenario = result.SuggestedScenario,
                        PolicyDecisions =
                        [
                            new RuntimeAnalysisScenarioPolicyDecision
                            {
                                PolicyKey =
                                    "demo.runtime-analysis.reanalysis.stop",
                                ResultKind = "Skipped",
                                Allowed = false,
                                Message =
                                    "AI re-analysis concluded that no additional experiment is currently warranted."
                            }
                        ]
                    },
                    depth);
            }

            if (depth >= RuntimeAnalysisChildDagDefinitionFactory.MaximumApprovedChildDepth)
            {
                return Complete(
                    new RuntimeAnalysisScenarioPolicyValidationResult
                    {
                        Allowed = false,
                        RequiresHumanApproval = false,
                        PlanKey = "read",
                        Scenario = result.SuggestedScenario,
                        PolicyDecisions =
                        [
                            new RuntimeAnalysisScenarioPolicyDecision
                            {
                                PolicyKey =
                                    "demo.runtime-analysis.child-depth-limit",
                                ResultKind = "Denied",
                                Allowed = false,
                                Message =
                                    $"Approval-driven Child DAG depth is deterministically capped at {RuntimeAnalysisChildDagDefinitionFactory.MaximumApprovedChildDepth} for this demo."
                            }
                        ]
                    },
                    depth);
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
                    result.SuggestedScenario,
                    cancellationToken)
                .ConfigureAwait(false);

            if (evaluation.Results.Count !=
                evaluation.ConfiguredPolicies.Count)
            {
                throw new InvalidOperationException(
                    "Re-analysis scenario validation did not return one decision per dynamically configured policy.");
            }

            var results = evaluation.Results.ToArray();

            var decisions = evaluation.ConfiguredPolicies
                .Select(
                    (policyDefinition, index) =>
                    {
                        var policyResult = results[index];

                        return new RuntimeAnalysisScenarioPolicyDecision
                        {
                            PolicyKey = policyDefinition.Name,
                            ResultKind = policyResult.Kind.ToString(),
                            Allowed = policyResult.IsSuccess,
                            Message = policyResult.Message ?? string.Empty
                        };
                    })
                .ToArray();

            var allowed = decisions.All(
                decision => decision.Allowed);

            return Complete(
                new RuntimeAnalysisScenarioPolicyValidationResult
                {
                    Allowed = allowed,
                    RequiresHumanApproval =
                        allowed
                        && evaluation.Definition.RequireHumanApproval,
                    PlanKey = evaluation.Definition.PlanKey,
                    Scenario = result.SuggestedScenario,
                    PolicyDecisions = decisions
                },
                depth);
        }

        private static AiStepResult Complete(
            RuntimeAnalysisScenarioPolicyValidationResult validation,
            int depth)
        {
            return AiStepResult.Ok(
                output: JsonSerializer.Serialize(
                    validation),
                data: new Dictionary<string, object?>(
                    StringComparer.Ordinal)
                {
                    ["runtimeStepKey"] =
                        RuntimeAnalysisStepKeys.ValidateReanalysisScenario,
                    ["reanalysis.depth"] = depth,
                    ["allowed"] = validation.Allowed,
                    ["requiresHumanApproval"] =
                        validation.RequiresHumanApproval,
                    ["planKey"] = validation.PlanKey
                });
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
