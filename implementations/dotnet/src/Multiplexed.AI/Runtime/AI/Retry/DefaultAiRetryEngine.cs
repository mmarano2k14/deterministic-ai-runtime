using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Retry;
using Multiplexed.AI.Runtime.AI.Policies;
using Multiplexed.AI.Runtime.Invocation;

namespace Multiplexed.AI.Runtime.AI.Retry
{
    /// <summary>
    /// Default implementation of the retry policy engine.
    /// </summary>
    /// <remarks>
    /// This engine is step-scoped and policy-driven.
    /// </remarks>
    [AiPolicyEngine(AiPolicyKind.Retry)]
    public sealed class DefaultAiRetryEngine : AiPolicyEngine, IAiRetryEngine
    {
        private static readonly AiRetryPolicyDefinition DefaultRetryDefinition = new()
        {
            Policies =
            [
                new AiConfiguredPolicyDefinition
                {
                    Name = "retry.transient.default"
                }
            ],
            MaxRetries = 3,
            Strategy = AiRetryBackoffStrategy.Fixed,
            BaseDelayMs = 500,
            MaxDelayMs = null,
            Jitter = false
        };

        public DefaultAiRetryEngine(
            IAiPolicyRegistry policyRegistry,
            AiStepExecutionContext stepContext,
            IAiRuntimeObservability obs)
            : base(policyRegistry, stepContext, obs)
        {
        }

        public override AiPolicyKind Kind => AiPolicyKind.Retry;

        public async Task<AiRetryDecision> HandleFailureAsync(
            AiStepState stepState,
            string? error,
            Exception? exception,
            DateTime utcNow,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(stepState);

            if (stepState.Status != AiStepExecutionStatus.Running)
            {
                throw new InvalidOperationException(
                    $"Retry cannot be applied to step '{stepState.StepName}' from status '{stepState.Status}'.");
            }

            stepState.RetryState ??= new AiStepRetryState();

            var retryDefinition = await ResolveRetryDefinitionAsync(cancellationToken)
                .ConfigureAwait(false);

            stepState.Retry ??= retryDefinition;

            var retryContext = CreateRetryContext(
                stepState,
                retryDefinition,
                error,
                exception,
                utcNow);

            var decision = await DecideAsync(
                    retryContext,
                    retryDefinition,
                    cancellationToken)
                .ConfigureAwait(false);

            ApplyDecision(stepState, decision, error, utcNow);

            return decision;
        }

        public async Task<AiRetryDecision> DecideAsync(
            AiRetryContext retryContext,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(retryContext);

            var retryDefinition = await ResolveRetryDefinitionAsync(cancellationToken)
                .ConfigureAwait(false);

            return await DecideAsync(
                    retryContext,
                    retryDefinition,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private static AiRetryPolicyDefinition CreateDefaultRetryDefinition()
        {
            return new AiRetryPolicyDefinition
            {
                Policies =
                [
                    new AiConfiguredPolicyDefinition
                    {
                        Name = "retry.transient.default"
                    }
                ],
                MaxRetries = 3,
                Strategy = AiRetryBackoffStrategy.Fixed,
                BaseDelayMs = 500,
                MaxDelayMs = null,
                Jitter = false
            };
        }

        private async Task<AiRetryDecision> DecideAsync(
            AiRetryContext retryContext,
            AiRetryPolicyDefinition retryDefinition,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(retryContext);
            ArgumentNullException.ThrowIfNull(retryDefinition);

            var policies = ResolveRetryPolicies(retryDefinition);

            var results = await ExecutePoliciesAsync(
                    retryContext,
                    policies,
                    cancellationToken)
                .ConfigureAwait(false);

            var blocking = results.FirstOrDefault(x => x.Kind == AiPolicyResultKind.Block);
            if (blocking is not null)
            {
                var typed = blocking as AiPolicyResultGeneric<AiRetryPolicyOutcome>;
                return AiRetryDecision.Fail(typed?.Data?.Reason ?? blocking.Message ?? "Blocked by retry policy.");
            }

            if (retryContext.RetryCount >= retryDefinition.MaxRetries)
            {
                return AiRetryDecision.Fail("Retry budget exhausted.");
            }

            var outcomes = results
                .OfType<AiPolicyResultGeneric<AiRetryPolicyOutcome>>()
                .Select(x => x.Data)
                .Where(x => x is not null)
                .Select(x => x!)
                .ToList();

            if (outcomes.Count > 0 && outcomes.All(x => !x.IsRetryable))
            {
                return AiRetryDecision.Fail("Failure is not retryable.");
            }

            var delay = ResolveDelay(
                retryContext,
                retryDefinition,
                outcomes);

            var reason = ResolveReason(outcomes);

            return AiRetryDecision.Retry(delay, reason);
        }


        private IReadOnlyCollection<IAiPolicy> ResolveRetryPolicies(AiRetryPolicyDefinition definition)
        {
            var bindings = StepContext.RetryPolicyBindings;
            if (bindings.Count == 0)
            {
                return ResolvePolicies(definition.Policies.GetPolicyNames(), AiPolicyKind.Retry);
            }

            var result = new List<IAiPolicy>();
            var bindingIndex = 0;
            var customFactory = StepContext.Services.GetService(typeof(AiRetryPolicyAdapterFactory)) as AiRetryPolicyAdapterFactory;
            foreach (var declaration in definition.Policies)
            {
                if (string.IsNullOrWhiteSpace(declaration.Name))
                {
                    AiInvocationBindingResolver.EnsureNativePolicy(declaration);
                    continue;
                }
                if (bindingIndex >= bindings.Count) throw new InvalidOperationException("Retry policy bindings do not match the effective declaration list.");
                var binding = bindings[bindingIndex++];
                if (binding.PolicyName != declaration.Name) throw new InvalidOperationException("Retry policy binding order does not match the effective declaration list.");
                if (binding.Invocation.Kind == AiInvocationKind.Custom)
                {
                    if (customFactory is null) throw new NotSupportedException("Custom Retry policy execution is not installed; native fallback is forbidden.");
                    result.Add(customFactory.Bind(StepContext, declaration, binding));
                }
                else
                {
                    AiInvocationBindingResolver.EnsureNativePolicy(declaration);
                    result.Add(ResolvePolicies(new[] { declaration.Name }, AiPolicyKind.Retry).Single());
                }
            }
            if (bindingIndex != bindings.Count) throw new InvalidOperationException("Retry policy bindings contain declarations not present in the effective Retry definition.");
            return result.AsReadOnly();
        }

        public async Task<AiRetryPolicyDefinition> ResolveRetryDefinitionAsync(
            CancellationToken cancellationToken)
        {
            var retryDefinition = await ResolvePolicyDefinitionAsync<AiRetryPolicyDefinition>(
                    "retry",
                    cancellationToken)
                .ConfigureAwait(false);

            return retryDefinition ?? DefaultRetryDefinition;
        }

        private static void ApplyDecision(
            AiStepState stepState,
            AiRetryDecision decision,
            string? error,
            DateTime utcNow)
        {
            switch (decision.Kind)
            {
                case AiRetryDecisionKind.Retry:
                    ApplyRetry(stepState, decision, error, utcNow);
                    return;

                case AiRetryDecisionKind.Fail:
                case AiRetryDecisionKind.Stop:
                    stepState.MarkFailed(error);
                    return;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported retry decision '{decision.Kind}'.");
            }
        }

        private static void ApplyRetry(
            AiStepState stepState,
            AiRetryDecision decision,
            string? error,
            DateTime utcNow)
        {
            if (!decision.Delay.HasValue)
            {
                throw new InvalidOperationException(
                    "Retry decision must provide a delay.");
            }

            var nextRetryAtUtc = utcNow.Add(decision.Delay.Value);

            stepState.RetryState ??= new AiStepRetryState();

            stepState.RetryState.RetryCount++;
            stepState.RetryState.RetryReason = decision.Reason;
            stepState.RetryState.LastRetryAtUtc = utcNow;
            stepState.RetryState.NextRetryAtUtc = nextRetryAtUtc;

            stepState.MarkWaitingForRetry(error, nextRetryAtUtc);
        }

        private AiRetryContext CreateRetryContext(
            AiStepState stepState,
            AiRetryPolicyDefinition retryDefinition,
            string? error,
            Exception? exception,
            DateTime utcNow)
        {
            return new AiRetryContext
            {
                ExecutionId = StepContext.ExecutionId,
                StepId = stepState.StepName,
                StepKey = StepContext.StepKey,
                RetryCount = stepState.RetryState?.RetryCount ?? 0,
                MaxRetries = retryDefinition.MaxRetries,
                Exception = exception,
                FailureReason = error,
                FailedAtUtc = utcNow,
                Retry = retryDefinition,
                Inputs = stepState.Inputs,
                Config = stepState.Config
            };
        }

        private static TimeSpan ResolveDelay(
            AiRetryContext context,
            AiRetryPolicyDefinition definition,
            IReadOnlyCollection<AiRetryPolicyOutcome> outcomes)
        {
            var suggested = outcomes
                .Where(x => x.SuggestedDelay.HasValue)
                .Select(x => x.SuggestedDelay!.Value);

            if (suggested.Any())
            {
                return suggested.Max();
            }

            double delay = definition.Strategy switch
            {
                AiRetryBackoffStrategy.Fixed => definition.BaseDelayMs,
                AiRetryBackoffStrategy.Exponential =>
                    definition.BaseDelayMs * Math.Pow(2, context.RetryCount),
                _ => definition.BaseDelayMs
            };

            if (definition.MaxDelayMs.HasValue)
            {
                delay = Math.Min(delay, definition.MaxDelayMs.Value);
            }

            delay = Math.Max(0, delay);

            return TimeSpan.FromMilliseconds(delay);
        }

        private static string? ResolveReason(
            IReadOnlyCollection<AiRetryPolicyOutcome> outcomes)
        {
            return outcomes
                .Select(x => x.Reason)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        }
    }
}