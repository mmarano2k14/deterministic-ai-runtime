using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Execution;

namespace Multiplexed.AI.Runtime.Invocation
{
    /// <summary>
    /// Compiles the effective Delegation declaration scope for the existing ExecuteChildDag checkpoint.
    /// No policy is executed and no child identity is allocated here.
    /// </summary>
    public sealed class AiDelegationPolicyBindingResolver
    {
        private static readonly AiInvocationBindingResolver InvocationResolver = new();

        public IReadOnlyList<AiPolicyInvocationBinding> Resolve(
            AiPipelineDefinition pipeline,
            AiPipelineStepDefinition step)
        {
            ArgumentNullException.ThrowIfNull(pipeline);
            ArgumentNullException.ThrowIfNull(step);

            if (!string.Equals(step.StepKey, ExecuteChildDagStep.StepKey, StringComparison.Ordinal))
            {
                return Array.Empty<AiPolicyInvocationBinding>();
            }

            var stepHas = TryRead(step.Config, out var stepDefinition);
            var scope = stepHas ? AiPolicyBindingScope.Step : AiPolicyBindingScope.Pipeline;
            var definition = stepHas
                ? stepDefinition
                : (TryRead(pipeline.Config, out var pipelineDefinition) ? pipelineDefinition : null);

            if (definition?.Policies is null || definition.Policies.Count == 0)
            {
                return Array.Empty<AiPolicyInvocationBinding>();
            }

            var result = new List<AiPolicyInvocationBinding>(definition.Policies.Count);
            foreach (var policy in definition.Policies)
            {
                ArgumentNullException.ThrowIfNull(policy);
                if (string.IsNullOrWhiteSpace(policy.Name))
                {
                    AiInvocationBindingResolver.EnsureNativePolicy(policy);
                    continue;
                }

                result.Add(InvocationResolver.ResolvePolicy(
                    pipeline,
                    policy,
                    scope,
                    scope == AiPolicyBindingScope.Step ? step : null));
            }

            return result.AsReadOnly();
        }

        private static bool TryRead(
            IReadOnlyDictionary<string, object?> config,
            out AiChildDelegationPolicyDefinition? definition)
        {
            var matches = config
                .Where(pair => pair.Key.Equals(AiChildDelegationPolicyDefinition.ConfigKey, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matches.Length > 1)
            {
                throw new InvalidOperationException("Ambiguous 'delegation' policy configuration casing.");
            }

            if (matches.Length == 0)
            {
                definition = null;
                return false;
            }

            if (matches[0].Value is null)
            {
                definition = null;
                return true;
            }

            definition = matches[0].Value as AiChildDelegationPolicyDefinition
                ?? JsonSerializer.Deserialize<AiChildDelegationPolicyDefinition>(JsonSerializer.Serialize(matches[0].Value))
                ?? throw new InvalidOperationException("Invalid 'delegation' policy definition.");
            return true;
        }
    }
}
