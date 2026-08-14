using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;

namespace Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation
{
    /// <summary>
    /// Provides the immutable child invocation authority and per-policy configuration evaluated by a child delegation policy.
    /// </summary>
    /// <remarks>
    /// Policies must treat <see cref="Relation"/> as read-only. Durable relation mutation remains owned by the
    /// child delegation coordinator after policy evaluation and compare-and-swap.
    /// </remarks>
    public sealed class AiChildDelegationPolicyContext
    {
        /// <summary>
        /// Gets the durable parent-to-child execution relation being authorized.
        /// </summary>
        public required AiChildExecutionRelation Relation { get; init; }

        /// <summary>
        /// Gets the policy-specific configuration frozen in the delegation binding.
        /// </summary>
        public IReadOnlyDictionary<string, object?> Config { get; init; } =
            new Dictionary<string, object?>();
    }
}
