using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Context;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.AI.Policies;
using MultiplexedRbac.Sample.Crm.Api.AI.Models;
using MultiplexedRbac.Sample.Crm.Api.AI.Runtime;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Policies
{
    public sealed class RuntimeAnalysisScenarioValidationPolicyEngine :
        AiPolicyEngine
    {
        public RuntimeAnalysisScenarioValidationPolicyEngine(
            IAiPolicyRegistry policyRegistry,
            AiStepExecutionContext stepContext,
            IAiRuntimeObservability observability)
            : base(
                policyRegistry,
                stepContext,
                observability)
        {
        }

        public override AiPolicyKind Kind =>
            AiPolicyKind.Validation;

        public async Task<RuntimeAnalysisScenarioPolicyEvaluation>
            ValidateAsync(
                RuntimeAnalysisSuggestedScenario scenario,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(
                scenario);

            var definition =
                await ResolvePolicyDefinitionAsync<
                        RuntimeAnalysisScenarioPolicyDefinition>(
                        RuntimeAnalysisStepConfigKeys
                            .ScenarioPolicyDefinition,
                        cancellationToken)
                    .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Scenario policy definition '{RuntimeAnalysisStepConfigKeys.ScenarioPolicyDefinition}' is missing from the validation pipeline step.");

            var configuredPolicies = definition.Policies
                .Where(
                    policy =>
                        !string.IsNullOrWhiteSpace(
                            policy.Name))
                .ToList();

            if (configuredPolicies.Count == 0)
            {
                throw new InvalidOperationException(
                    "Scenario policy definition must contain at least one configured policy.");
            }

            foreach (var configuredPolicy in configuredPolicies)
            {
                if (!string.IsNullOrWhiteSpace(
                        configuredPolicy.Kind)
                    && !string.Equals(
                        configuredPolicy.Kind,
                        Kind.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Configured policy '{configuredPolicy.Name}' declares kind '{configuredPolicy.Kind}', expected '{Kind}'.");
                }
            }

            var policyDefinitions =
                configuredPolicies.ToDictionary(
                    policy => policy.Name,
                    policy => policy,
                    StringComparer.Ordinal);

            var policyContext =
                new RuntimeAnalysisScenarioPolicyContext
                {
                    Scenario = scenario,
                    PlanKey = definition.PlanKey,
                    PolicyDefinitions = policyDefinitions
                };

            var policies = ResolvePolicies(
                configuredPolicies.GetPolicyNames(),
                Kind);

            if (policies.Count != configuredPolicies.Count)
            {
                throw new InvalidOperationException(
                    $"Scenario policy registry resolved {policies.Count} policies for {configuredPolicies.Count} configured entries.");
            }

            var results = await ExecutePoliciesAsync(
                    policyContext,
                    policies,
                    cancellationToken)
                .ConfigureAwait(false);

            return new RuntimeAnalysisScenarioPolicyEvaluation
            {
                Definition = definition,
                ConfiguredPolicies = configuredPolicies,
                Results = results
            };
        }
    }
}
