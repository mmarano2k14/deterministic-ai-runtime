using Multiplexed.Abstractions.AI.Policies;

namespace Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation
{
    /// <summary>
    /// Defines the ordered policies that authorize one parent-to-child DAG delegation boundary.
    /// </summary>
    /// <remarks>
    /// Resolution follows the runtime's existing step-configuration precedence: step override,
    /// pipeline fallback, then the default empty definition. An empty policy collection allows
    /// delegation by default while preserving the ability to add explicit authorization policies.
    /// </remarks>
    public sealed class AiChildDelegationPolicyDefinition
    {
        /// <summary>
        /// Gets the configuration key used by the existing step context helper to resolve child delegation policy configuration.
        /// </summary>
        public const string ConfigKey = "delegation";

        /// <summary>
        /// Gets or sets the ordered configured delegation policies.
        /// </summary>
        public List<AiConfiguredPolicyDefinition> Policies { get; set; } = new();
    }
}
