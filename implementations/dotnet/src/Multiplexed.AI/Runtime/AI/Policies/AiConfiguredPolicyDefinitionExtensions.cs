using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.Invocation;

namespace Multiplexed.AI.Runtime.AI.Policies
{
    /// <summary>
    /// Helper methods for configured AI policies.
    /// </summary>
    public static class AiConfiguredPolicyDefinitionExtensions
    {
        /// <summary>
        /// Returns ordered policy names.
        /// </summary>
        public static IReadOnlyList<string> GetPolicyNames(
            this IEnumerable<AiConfiguredPolicyDefinition>? policies)
        {
            if (policies is null)
            {
                return Array.Empty<string>();
            }

            var names = new List<string>();
            foreach (var policy in policies)
            {
                AiInvocationBindingResolver.EnsureNativePolicy(policy);
                if (!string.IsNullOrWhiteSpace(policy.Name))
                {
                    names.Add(policy.Name);
                }
            }
            return names;
        }
    }
}