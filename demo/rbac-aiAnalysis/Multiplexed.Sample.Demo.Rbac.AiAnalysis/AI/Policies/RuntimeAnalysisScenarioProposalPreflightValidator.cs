using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.AI.Policies;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Providers;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Policies
{
    /// <summary>
    /// Prevents an out-of-policy AI proposal from being surfaced as a usable
    /// suggested scenario.
    ///
    /// This executes the same registered pluggable validation policies against
    /// the same declarative policy definition embedded in the runtime-analysis
    /// DAG. The downstream DAG policy step remains authoritative and is never
    /// bypassed.
    /// </summary>
    internal sealed class RuntimeAnalysisScenarioProposalPreflightValidator
    {
        private readonly IAiPolicyRegistry _policyRegistry;

        public RuntimeAnalysisScenarioProposalPreflightValidator(
            IAiPolicyRegistry policyRegistry)
        {
            _policyRegistry =
                policyRegistry
                ?? throw new ArgumentNullException(
                    nameof(policyRegistry));
        }

        public async Task ValidateAsync(
            RuntimeAnalysisSuggestedScenario scenario,
            RuntimeAnalysisScenarioPolicyDefinition? definition,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(
                scenario);

            // Backward compatibility for already-durable analyses created
            // before the policy envelope was added to provider requests.
            // New pipeline version 10 submissions always supply it.
            if (definition is null)
            {
                return;
            }

            var configuredPolicies = definition.Policies
                .Where(
                    policy =>
                        !string.IsNullOrWhiteSpace(
                            policy.Name))
                .ToArray();

            if (configuredPolicies.Length == 0)
            {
                throw new RuntimeAnalysisProviderException(
                    "Runtime-analysis scenario policy definition contains no configured validation policies.");
            }

            foreach (var configuredPolicy in configuredPolicies)
            {
                if (!string.IsNullOrWhiteSpace(
                        configuredPolicy.Kind)
                    && !string.Equals(
                        configuredPolicy.Kind,
                        AiPolicyKind.Validation.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new RuntimeAnalysisProviderException(
                        $"Configured scenario policy '{configuredPolicy.Name}' declares kind '{configuredPolicy.Kind}', expected '{AiPolicyKind.Validation}'.");
                }
            }

            var policyDefinitions =
                configuredPolicies.ToDictionary(
                    policy => policy.Name,
                    policy => policy,
                    StringComparer.Ordinal);

            var context =
                new RuntimeAnalysisScenarioPolicyContext
                {
                    Scenario = scenario,
                    PlanKey = definition.PlanKey,
                    PolicyDefinitions = policyDefinitions
                };

            var policies = _policyRegistry.ResolveMany(
                configuredPolicies.Select(
                    policy => policy.Name),
                AiPolicyKind.Validation);

            if (policies.Count != configuredPolicies.Length)
            {
                throw new RuntimeAnalysisProviderException(
                    $"Scenario proposal preflight resolved {policies.Count} policies for {configuredPolicies.Length} configured entries.");
            }

            var violations = new List<string>();

            foreach (var policy in policies)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await policy.ExecuteAsync(
                        context,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!result.IsSuccess)
                {
                    violations.Add(
                        $"{policy.Key}: {result.Message ?? "Blocked by configured policy."}");
                }
            }

            if (violations.Count > 0)
            {
                throw new RuntimeAnalysisProviderException(
                    "AI suggested scenario is outside the configured deterministic policy envelope. "
                    + string.Join(
                        " ",
                        violations));
            }
        }
    }
}
