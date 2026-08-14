using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.AI.Policies;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Delegation
{
    /// <summary>
    /// Evaluates parent-to-child DAG delegation through the runtime's existing policy engine infrastructure.
    /// </summary>
    /// <remarks>
    /// This implementation introduces no second policy system. Policy registration, resolution, execution,
    /// metrics, tracing, and ledger events continue to flow through <see cref="AiPolicyEngine"/>.
    /// </remarks>
    [AiPolicyEngine(AiPolicyKind.Delegation)]
    public sealed class DefaultAiChildDelegationPolicyEngine : AiPolicyEngine, IAiChildDelegationPolicyEngine
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultAiChildDelegationPolicyEngine"/> class.
        /// </summary>
        /// <param name="policyRegistry">The existing runtime policy registry.</param>
        /// <param name="stepContext">The parent step execution context.</param>
        /// <param name="observability">The runtime observability facade used by the shared policy engine.</param>
        public DefaultAiChildDelegationPolicyEngine(
            IAiPolicyRegistry policyRegistry,
            AiStepExecutionContext stepContext,
            IAiRuntimeObservability observability)
            : base(policyRegistry, stepContext, observability)
        {
        }

        /// <inheritdoc />
        public override AiPolicyKind Kind => AiPolicyKind.Delegation;

        /// <inheritdoc />
        public async Task<AiChildDelegationPolicyDefinition> ResolveDefinitionAsync(
            CancellationToken cancellationToken = default)
        {
            var definition = await ResolvePolicyDefinitionAsync<AiChildDelegationPolicyDefinition>(
                    AiChildDelegationPolicyDefinition.ConfigKey,
                    cancellationToken)
                .ConfigureAwait(false);

            return definition ?? new AiChildDelegationPolicyDefinition();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyCollection<AiPolicyResult>> EvaluateAsync(
            AiChildExecutionRelation relation,
            AiChildDelegationPolicyDefinition definition,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(relation);
            ArgumentNullException.ThrowIfNull(definition);

            if (relation.Status != AiChildExecutionRelationStatus.DelegationPolicyPending)
            {
                throw new InvalidOperationException(
                    $"Child delegation policy can only be evaluated from status '{AiChildExecutionRelationStatus.DelegationPolicyPending}', " +
                    $"but relation '{relation.ChildInvocationKey}' is '{relation.Status}'.");
            }

            if (definition.Policies.Count == 0)
            {
                return Array.Empty<AiPolicyResult>();
            }

            var results = new List<AiPolicyResult>(definition.Policies.Count);

            foreach (var configuredPolicy in definition.Policies)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(configuredPolicy.Name))
                {
                    throw new InvalidOperationException(
                        "Configured child delegation policies must declare a non-empty registered policy name.");
                }

                var policies = ResolvePolicies(
                    new[] { configuredPolicy.Name },
                    AiPolicyKind.Delegation);

                var policyContext = new AiChildDelegationPolicyContext
                {
                    Relation = relation,
                    Config = new Dictionary<string, object?>(
                        configuredPolicy.Config,
                        StringComparer.Ordinal)
                };

                var policyResults = await ExecutePoliciesAsync(
                        policyContext,
                        policies,
                        cancellationToken)
                    .ConfigureAwait(false);

                results.AddRange(policyResults);
            }

            return results;
        }
    }
}
