using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Retry;

namespace Multiplexed.AI.Runtime.Invocation
{
    /// <summary>Compiles the effective Retry declaration scope without executing policies.</summary>
    public sealed class AiRetryPolicyBindingResolver
    {
        private static readonly AiInvocationBindingResolver InvocationResolver = new();

        public IReadOnlyList<AiPolicyInvocationBinding> Resolve(AiPipelineDefinition pipeline, AiPipelineStepDefinition step)
        {
            ArgumentNullException.ThrowIfNull(pipeline); ArgumentNullException.ThrowIfNull(step);
            var stepHas = TryRead(step.Config, out var stepDefinition);
            var scope = stepHas ? AiPolicyBindingScope.Step : AiPolicyBindingScope.Pipeline;
            var definition = stepHas ? stepDefinition : (TryRead(pipeline.Config, out var pipelineDefinition) ? pipelineDefinition : null);
            if (definition?.Policies is null || definition.Policies.Count == 0) return Array.Empty<AiPolicyInvocationBinding>();
            var result = new List<AiPolicyInvocationBinding>();
            foreach (var policy in definition.Policies)
            {
                ArgumentNullException.ThrowIfNull(policy);
                if (string.IsNullOrWhiteSpace(policy.Name))
                {
                    AiInvocationBindingResolver.EnsureNativePolicy(policy);
                    continue;
                }
                result.Add(InvocationResolver.ResolvePolicy(pipeline, policy, scope, scope == AiPolicyBindingScope.Step ? step : null));
            }
            return result.AsReadOnly();
        }

        private static bool TryRead(IReadOnlyDictionary<string, object?> config, out AiRetryPolicyDefinition? definition)
            => AiPolicyDeclarationReader.TryRead(
                config,
                "retry",
                "Ambiguous 'retry' policy configuration casing.",
                "Invalid 'retry' policy definition.",
                out definition);
    }
}
