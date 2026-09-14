using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.AI.Concurrency;

namespace Multiplexed.AI.Runtime.Invocation
{
    /// <summary>
    /// Compiles declaration scopes for the concurrency checkpoint without evaluating
    /// policies. Configuration parsing remains with the existing concurrency resolver.
    /// As in its merge contract, a nonempty step policy list replaces the pipeline list.
    /// This rule is deliberately not generalized to other policy families.
    /// </summary>
    public sealed class AiConcurrencyPolicyBindingResolver
    {
        private static readonly DefaultAiConcurrencyDefinitionResolver DefinitionResolver = new();
        private static readonly AiInvocationBindingResolver InvocationResolver = new();

        public IReadOnlyList<AiPolicyInvocationBinding> Resolve(
            AiPipelineDefinition pipeline,
            AiPipelineStepDefinition step)
        {
            ArgumentNullException.ThrowIfNull(pipeline);
            ArgumentNullException.ThrowIfNull(step);

            var stepPolicies = ReadPolicies(step.Config);
            var scope = stepPolicies.Count > 0 ? AiPolicyBindingScope.Step : AiPolicyBindingScope.Pipeline;
            var policies = scope == AiPolicyBindingScope.Step ? stepPolicies : ReadPolicies(pipeline.Config);
            if (policies.Count == 0)
            {
                return Array.Empty<AiPolicyInvocationBinding>();
            }

            var bindings = new List<AiPolicyInvocationBinding>(policies.Count);
            foreach (var policy in policies)
            {
                // Preserve the native checkpoint's historical treatment of blank entries,
                // but never let an unsupported or contradictory declaration disappear.
                if (string.IsNullOrWhiteSpace(policy.Name))
                {
                    AiInvocationBindingResolver.EnsureNativePolicy(policy);
                    continue;
                }

                bindings.Add(InvocationResolver.ResolvePolicy(
                    pipeline, policy, scope, scope == AiPolicyBindingScope.Step ? step : null));
            }

            return bindings.AsReadOnly();
        }

        private static IReadOnlyList<AiConfiguredPolicyDefinition> ReadPolicies(
            IReadOnlyDictionary<string, object?> config)
        {
            return DefinitionResolver.ReadPolicyDeclarations(config);
        }
    }
}
