using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.Abstractions.AI.Steps;

namespace Multiplexed.AI.Runtime.Invocation.Mcp
{
    /// <summary>
    /// Opt-in MCP capability for the existing step binder. Construction and Create only
    /// capture immutable plan metadata; they do not authorize, resolve inputs or call tools.
    /// This factory is not registered by assembly scanning or by the default runtime host.
    /// </summary>
    public sealed class AiMcpStepAdapterFactory : IAiStepInvocationAdapterFactory
    {
        private readonly IAiMcpToolResolver _resolver;
        private readonly IAiMcpToolTransport _transport;
        private readonly TimeSpan _timeout;
        private readonly TimeProvider _timeProvider;

        public AiMcpStepAdapterFactory(
            IAiMcpToolResolver resolver,
            IAiMcpToolTransport transport,
            AiMcpStepInvocationOptions? options = null,
            TimeProvider? timeProvider = null)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _timeout = (options ?? new AiMcpStepInvocationOptions()).InvocationTimeout;
            _timeProvider = timeProvider ?? TimeProvider.System;
            if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(30))
            {
                throw new ArgumentOutOfRangeException(nameof(options), "MCP invocation timeout must be positive and at most 30 seconds.");
            }
        }

        public AiInvocationKind Kind => AiInvocationKind.Mcp;
        public string? ExecutionLanguage => null;

        public IAiStep Create(AiStepInvocationAdapterContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(context.Binding);
            ArgumentException.ThrowIfNullOrWhiteSpace(context.PipelineName);
            ArgumentException.ThrowIfNullOrWhiteSpace(context.StepName);
            ArgumentException.ThrowIfNullOrWhiteSpace(context.StepKey);
            var binding = context.Binding;
            if (binding.Kind != AiInvocationKind.Mcp || binding.ExecutionLanguage is not null ||
                binding.LanguageSource != AiExecutionLanguageSource.None || binding.ImplementationRef is not null ||
                !AiMcpInvocationIdentity.IsReference(binding.ConnectionRef) ||
                !AiMcpInvocationIdentity.IsSegment(binding.Tool))
            {
                throw new InvalidOperationException("An MCP adapter requires a language-free binding, an opaque connection reference and a concrete tool name.");
            }
            return new AiMcpStepAdapter(context, _resolver, _transport, _timeout, _timeProvider);
        }
    }
}
