using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.AI.Policies;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Delegation
{
    /// <summary>
    /// Defines the child-DAG delegation specialization of the existing step-scoped AI policy engine.
    /// </summary>
    public interface IAiChildDelegationPolicyEngine : IAiPolicyEngine
    {
        /// <summary>
        /// Resolves the delegation policy definition using the current step configuration precedence.
        /// </summary>
        /// <param name="cancellationToken">A token used to cancel the asynchronous operation.</param>
        /// <returns>The resolved delegation policy definition, or the default allow definition when no configuration exists.</returns>
        Task<AiChildDelegationPolicyDefinition> ResolveDefinitionAsync(
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Evaluates the exact frozen delegation policy definition against a durable child execution relation.
        /// </summary>
        /// <param name="relation">The durable relation being authorized.</param>
        /// <param name="definition">The frozen delegation policy definition loaded from the relation.</param>
        /// <param name="cancellationToken">A token used to cancel the asynchronous operation.</param>
        /// <returns>The ordered policy results produced by the existing policy execution pipeline.</returns>
        Task<IReadOnlyCollection<AiPolicyResult>> EvaluateAsync(
            AiChildExecutionRelation relation,
            AiChildDelegationPolicyDefinition definition,
            CancellationToken cancellationToken = default);
    }
}
