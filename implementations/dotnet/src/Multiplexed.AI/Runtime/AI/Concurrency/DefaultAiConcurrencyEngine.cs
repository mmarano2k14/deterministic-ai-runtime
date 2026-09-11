using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.AI.Policies;

namespace Multiplexed.AI.Runtime.AI.Concurrency
{
    /// <summary>
    /// Default implementation of the concurrency policy engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This engine is decision-only. It evaluates configured concurrency policies and computes
    /// whether a step may proceed to distributed Redis concurrency-slot acquisition.
    /// </para>
    ///
    /// <para>
    /// This engine does not acquire Redis leases. Distributed admission remains the responsibility
    /// of <see cref="IAiConcurrencyGate"/>.
    /// </para>
    ///
    /// <para>
    /// Configured concurrency policies are executed one by one so each policy receives its own
    /// <c>AiConfiguredPolicyDefinition.Config</c> through <see cref="AiConcurrencyPolicyContext"/>.
    /// </para>
    /// </remarks>
    [AiPolicyEngine(AiPolicyKind.Concurrency)]
    public sealed class DefaultAiConcurrencyEngine : AiPolicyEngine, IAiConcurrencyEngine
    {
        private static readonly IAiConcurrencyDefinitionResolver DefinitionResolver =
            new DefaultAiConcurrencyDefinitionResolver();

        private static readonly AiConcurrencyDefinition DefaultConcurrencyDefinition = new()
        {
            Enabled = false,
            Policies = new List<AiConfiguredPolicyDefinition>(),
            DefaultRetryAfterMs = 250,
            LeaseSeconds = 300,
            MaxJitterMs = 100
        };

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultAiConcurrencyEngine"/> class.
        /// </summary>
        /// <param name="policyRegistry">
        /// The policy registry used to resolve concurrency policies.
        /// </param>
        /// <param name="stepContext">
        /// The current step execution context.
        /// </param>
        /// <param name="obs">
        /// The runtime observability service.
        /// </param>
        public DefaultAiConcurrencyEngine(
            IAiPolicyRegistry policyRegistry,
            AiStepExecutionContext stepContext,
            IAiRuntimeObservability obs)
            : base(policyRegistry, stepContext, obs)
        {
        }

        /// <inheritdoc />
        public override AiPolicyKind Kind => AiPolicyKind.Concurrency;

        /// <inheritdoc />
        public async Task<AiConcurrencyDecision> DecideAsync(
            AiConcurrencyContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var evaluation = await EvaluateAsync(
                    context,
                    cancellationToken)
                .ConfigureAwait(false);

            return evaluation.Decision;
        }

        /// <inheritdoc />
        public async Task<AiConcurrencyEvaluation> EvaluateAsync(
            AiConcurrencyContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var definition = await ResolveConcurrencyDefinitionAsync(
                    cancellationToken)
                .ConfigureAwait(false);

            if (!definition.Enabled)
            {
                return new AiConcurrencyEvaluation
                {
                    Definition = definition,
                    Decision = AiConcurrencyDecision.Allow()
                };
            }

            var results = await ExecuteConfiguredConcurrencyPoliciesAsync(
                    context,
                    definition,
                    cancellationToken)
                .ConfigureAwait(false);

            var decision = CreateDecision(
                definition,
                results);

            return new AiConcurrencyEvaluation
            {
                Definition = definition,
                Decision = decision
            };
        }

        /// <summary>
        /// Resolves concurrency configuration from the current step context.
        /// </summary>
        /// <param name="cancellationToken">
        /// A token used to cancel the asynchronous operation.
        /// </param>
        /// <returns>
        /// The resolved concurrency definition.
        /// </returns>
        /// <remarks>
        /// <para>
        /// The resolver is used instead of direct deserialization so policy-config defaults,
        /// direct config priority, and nullable merge semantics remain consistent with the rest
        /// of the concurrency system.
        /// </para>
        /// </remarks>
        public Task<AiConcurrencyDefinition> ResolveConcurrencyDefinitionAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var definition = DefinitionResolver.Resolve(
                StepContext.StepState);

            return Task.FromResult(definition ?? DefaultConcurrencyDefinition);
        }

        /// <summary>
        /// Executes configured concurrency policies while preserving each policy's own configuration.
        /// </summary>
        /// <param name="context">
        /// The distributed concurrency context.
        /// </param>
        /// <param name="definition">
        /// The resolved concurrency definition.
        /// </param>
        /// <param name="cancellationToken">
        /// A token used to cancel the asynchronous operation.
        /// </param>
        /// <returns>
        /// The ordered policy execution results.
        /// </returns>
        private async Task<IReadOnlyCollection<AiPolicyResult>> ExecuteConfiguredConcurrencyPoliciesAsync(
            AiConcurrencyContext context,
            AiConcurrencyDefinition definition,
            CancellationToken cancellationToken)
        {
            if (definition.Policies.Count == 0)
            {
                return Array.Empty<AiPolicyResult>();
            }

            var results = new List<AiPolicyResult>(definition.Policies.Count);

            foreach (var configuredPolicy in definition.Policies)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Name-only resolution is native-only until the family's contextual adapter exists.
                Multiplexed.AI.Runtime.Invocation.AiInvocationBindingResolver.EnsureNativePolicy(configuredPolicy);

                if (string.IsNullOrWhiteSpace(configuredPolicy.Name))
                {
                    continue;
                }

                var policies = ResolvePolicies(
                    new[] { configuredPolicy.Name },
                    AiPolicyKind.Concurrency);

                if (policies.Count == 0)
                {
                    continue;
                }

                var policyContext = new AiConcurrencyPolicyContext
                {
                    Concurrency = context,
                    Config = configuredPolicy.Config
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

        /// <summary>
        /// Converts policy execution results into a concurrency decision.
        /// </summary>
        /// <param name="definition">
        /// The resolved concurrency definition.
        /// </param>
        /// <param name="results">
        /// The executed policy results.
        /// </param>
        /// <returns>
        /// The computed concurrency decision.
        /// </returns>
        private static AiConcurrencyDecision CreateDecision(
            AiConcurrencyDefinition definition,
            IReadOnlyCollection<AiPolicyResult> results)
        {
            if (results.Count == 0)
            {
                return AiConcurrencyDecision.Allow();
            }

            if (results.Any(x => x.Kind == AiPolicyResultKind.Block))
            {
                var reason = results
                    .Where(x => x.Kind == AiPolicyResultKind.Block)
                    .Select(x => x.Message)
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

                return AiConcurrencyDecision.Deny(
                    reason ?? "Blocked by concurrency policy.",
                    TimeSpan.FromMilliseconds(definition.DefaultRetryAfterMs));
            }

            var outcomes = results
                .OfType<AiPolicyResultGeneric<AiConcurrencyPolicyOutcome>>()
                .Select(x => x.Data)
                .Where(x => x is not null)
                .Select(x => x!)
                .ToList();

            if (outcomes.Count == 0)
            {
                return AiConcurrencyDecision.Allow();
            }

            if (outcomes.Any(x => !x.IsAllowed))
            {
                var reason = outcomes
                    .Select(x => x.Reason)
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

                var retryAfter = outcomes
                    .Where(x => x.RetryAfter.HasValue)
                    .Select(x => x.RetryAfter!.Value)
                    .DefaultIfEmpty(TimeSpan.FromMilliseconds(definition.DefaultRetryAfterMs))
                    .Max();

                return AiConcurrencyDecision.Deny(
                    reason ?? "Concurrency policy denied execution.",
                    retryAfter);
            }

            return AiConcurrencyDecision.Allow();
        }
    }
}