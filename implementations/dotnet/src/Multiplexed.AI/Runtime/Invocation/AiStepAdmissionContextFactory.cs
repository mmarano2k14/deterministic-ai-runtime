using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Pipeline;

namespace Multiplexed.AI.Runtime.Invocation
{
    /// <summary>
    /// Projects the resolved plan into a metadata-only admission context. The selected
    /// executable is deliberately NOT copied: no factory, registry or worker is used.
    /// </summary>
    public static class AiStepAdmissionContextFactory
    {
        public static AiStepExecutionContext Create(
            AiExecutionContext execution,
            ResolvedAiPipelineStep source,
            AiConcurrencyDefinition effectiveDefinition)
        {
            ArgumentNullException.ThrowIfNull(execution);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(effectiveDefinition);

            if ((source.Invocation?.Kind is AiInvocationKind.Custom or AiInvocationKind.Mcp) && source.InvocationBinding is null)
            {
                throw new InvalidOperationException(
                    $"Admission requires the already resolved invocation binding for step '{source.Name}'.");
            }

            var view = new ResolvedAiPipelineStep
            {
                Name = source.Name,
                StepKey = source.StepKey,
                ExecutionLanguage = source.ExecutionLanguage,
                Invocation = source.Invocation,
                InvocationBinding = source.InvocationBinding,
                ConcurrencyPolicyBindings = source.ConcurrencyPolicyBindings,
                RetryPolicyBindings = source.RetryPolicyBindings,
                Order = source.Order,
                DependsOn = source.DependsOn,
                Input = source.Input,
                Config = source.Config,
                MaxRetries = source.MaxRetries,
                RetryDelayMs = source.RetryDelayMs
            };

            return new AiStepExecutionContext(execution, view)
            {
                ConcurrencyAdmissionDefinition = effectiveDefinition
            };
        }
    }
}
