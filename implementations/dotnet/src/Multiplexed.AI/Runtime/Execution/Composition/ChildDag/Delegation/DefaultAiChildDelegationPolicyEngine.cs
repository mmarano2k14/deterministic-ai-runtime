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
            var bindings = StepContext.DelegationPolicyBindings;
            var bindingIndex = 0;
            var customFactory = StepContext.Services
                .GetService(typeof(Multiplexed.AI.Runtime.Invocation.AiDelegationPolicyAdapterFactory))
                as Multiplexed.AI.Runtime.Invocation.AiDelegationPolicyAdapterFactory;

            foreach (var configuredPolicy in definition.Policies)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(configuredPolicy.Name))
                {
                    Multiplexed.AI.Runtime.Invocation.AiInvocationBindingResolver.EnsureNativePolicy(configuredPolicy);
                    throw new InvalidOperationException(
                        "Configured child delegation policies must declare a non-empty registered policy name.");
                }

                IAiPolicy policy;
                if (bindings.Count == 0)
                {
                    // Preserve historical native-only contexts and fail closed for custom declarations
                    // that were not compiled through the Delegation binding resolver.
                    Multiplexed.AI.Runtime.Invocation.AiInvocationBindingResolver.EnsureNativePolicy(configuredPolicy);
                    policy = ResolvePolicies(
                        new[] { configuredPolicy.Name },
                        AiPolicyKind.Delegation).Single();
                }
                else
                {
                    if (bindingIndex >= bindings.Count)
                    {
                        throw new InvalidOperationException(
                            "Delegation policy bindings do not match the frozen declaration list.");
                    }

                    var binding = bindings[bindingIndex++];
                    if (!string.Equals(binding.PolicyName, configuredPolicy.Name, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "Delegation policy binding order does not match the frozen declaration list.");
                    }

                    if (binding.Invocation.Kind == Multiplexed.Abstractions.AI.Invocation.AiInvocationKind.Custom)
                    {
                        if (customFactory is null)
                        {
                            throw new NotSupportedException(
                                "Custom Delegation policy execution is not installed; native fallback is forbidden.");
                        }

                        policy = customFactory.Bind(StepContext, configuredPolicy, binding);
                    }
                    else
                    {
                        Multiplexed.AI.Runtime.Invocation.AiInvocationBindingResolver.EnsureNativePolicy(configuredPolicy);
                        policy = ResolvePolicies(
                            new[] { configuredPolicy.Name },
                            AiPolicyKind.Delegation).Single();
                    }
                }

                var policyContext = new AiChildDelegationPolicyContext
                {
                    Relation = relation,
                    Config = new Dictionary<string, object?>(
                        configuredPolicy.Config,
                        StringComparer.Ordinal)
                };

                var policyResults = await ExecutePoliciesAsync(
                        policyContext,
                        new[] { policy },
                        cancellationToken)
                    .ConfigureAwait(false);

                results.AddRange(policyResults);
            }

            if (bindings.Count > 0 && bindingIndex != bindings.Count)
            {
                throw new InvalidOperationException(
                    "Delegation policy bindings contain declarations not present in the frozen definition.");
            }

            return results;
        }
    }
}
