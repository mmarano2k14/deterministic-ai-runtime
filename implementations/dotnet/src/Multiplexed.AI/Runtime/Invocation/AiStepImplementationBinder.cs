using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Steps;

namespace Multiplexed.AI.Runtime.Invocation
{
    /// <summary>
    /// Selects a trusted server adapter from an immutable capability map. Native steps
    /// retain their registry; custom/MCP bindings never fall back to a matching StepKey.
    /// This class has no worker transport, tenant catalog or execution-context cache.
    /// </summary>
    public sealed class AiStepImplementationBinder
    {
        private readonly IAiStepRegistry _nativeRegistry;
        private readonly IReadOnlyDictionary<(AiInvocationKind Kind, string? Language), IAiStepInvocationAdapterFactory> _factories;

        public AiStepImplementationBinder(
            IAiStepRegistry nativeRegistry,
            IEnumerable<IAiStepInvocationAdapterFactory> factories)
        {
            ArgumentNullException.ThrowIfNull(nativeRegistry);
            ArgumentNullException.ThrowIfNull(factories);
            _nativeRegistry = nativeRegistry;

            var installed = new Dictionary<(AiInvocationKind, string?), IAiStepInvocationAdapterFactory>();
            foreach (var factory in factories)
            {
                ArgumentNullException.ThrowIfNull(factory);
                var kind = factory.Kind;
                var language = factory.ExecutionLanguage;
                if (kind == AiInvocationKind.Custom)
                {
                    if (!AiExecutionLanguages.IsSupported(language))
                    {
                        throw new InvalidOperationException("A custom adapter factory requires a canonical execution language.");
                    }
                }
                else if (kind != AiInvocationKind.Mcp || language is not null)
                {
                    throw new InvalidOperationException("Only custom-language and language-free MCP adapter factories can be registered.");
                }

                if (!installed.TryAdd((kind, language), factory))
                {
                    throw new InvalidOperationException($"Multiple adapter factories are registered for '{kind}/{language}'.");
                }
            }

            _factories = installed;
        }

        /// <summary>
        /// Checks availability without touching the native registry or creating an
        /// adapter. Non-native execution is initially restricted to explicit DAGs.
        /// </summary>
        public void EnsureSupported(AiStepInvocationAdapterContext context, AiExecutionMode executionMode)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(context.Binding);
            ArgumentException.ThrowIfNullOrWhiteSpace(context.PipelineName);
            ArgumentException.ThrowIfNullOrWhiteSpace(context.StepName);
            ArgumentException.ThrowIfNullOrWhiteSpace(context.StepKey);

            if (context.Binding.Kind == AiInvocationKind.Native)
            {
                return;
            }

            if (executionMode != AiExecutionMode.Dag)
            {
                throw new NotSupportedException(
                    $"Step '{context.StepName}' requires an explicit DAG for '{context.Binding.Kind}' invocation. " +
                    "Sequential durable invocation is not installed; the execution mode is not rewritten.");
            }

            _ = GetFactory(context);
        }

        /// <summary>Creates one server implementation after the pipeline preflight succeeds.</summary>
        public IAiStep Bind(AiStepInvocationAdapterContext context, AiExecutionMode executionMode)
        {
            EnsureSupported(context, executionMode);
            if (context.Binding.Kind == AiInvocationKind.Native)
            {
                return _nativeRegistry.Resolve(context.StepKey);
            }

            return GetFactory(context).Create(context)
                ?? throw new InvalidOperationException(
                    $"The invocation adapter factory returned no implementation for step '{context.StepName}'.");
        }

        private IAiStepInvocationAdapterFactory GetFactory(AiStepInvocationAdapterContext context)
        {
            if (_factories.TryGetValue((context.Binding.Kind, context.Binding.ExecutionLanguage), out var factory))
            {
                return factory;
            }

            throw new NotSupportedException(
                $"Step '{context.StepName}' requires a '{context.Binding.Kind}/{context.Binding.ExecutionLanguage}' invocation adapter. " +
                "No such adapter is installed; native fallback is forbidden.");
        }
    }
}
