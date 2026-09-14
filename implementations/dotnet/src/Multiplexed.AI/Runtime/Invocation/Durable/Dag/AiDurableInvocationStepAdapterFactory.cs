using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Steps;

namespace Multiplexed.AI.Runtime.Invocation.Durable.Dag
{
    /// <summary>Opt-in plan-only factory. No worker, tenant context or I/O during resolution.</summary>
    public sealed class AiDurableInvocationStepAdapterFactory : IAiStepInvocationAdapterFactory
    {
        public AiDurableInvocationStepAdapterFactory(string executionLanguage)
        {
            if (executionLanguage is not ("python" or "typescript" or "dotnet"))
                throw new ArgumentException("Unsupported hosted execution language.", nameof(executionLanguage));
            ExecutionLanguage = executionLanguage;
        }
        public AiInvocationKind Kind => AiInvocationKind.Custom;
        public string ExecutionLanguage { get; }

        public IAiStep Create(AiStepInvocationAdapterContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.Binding.Kind != Kind || context.Binding.ExecutionLanguage != ExecutionLanguage ||
                string.IsNullOrWhiteSpace(context.PipelineName) || string.IsNullOrWhiteSpace(context.PipelineVersion) ||
                string.IsNullOrWhiteSpace(context.StepName) || string.IsNullOrWhiteSpace(context.StepKey) ||
                string.IsNullOrWhiteSpace(context.Binding.ImplementationRef) ||
                context.Binding.ConnectionRef is not null || context.Binding.Tool is not null)
                throw new InvalidOperationException("Durable custom adapter binding is incomplete or incompatible.");
            return new AiDurableInvocationStepAdapter(context);
        }
    }
}
