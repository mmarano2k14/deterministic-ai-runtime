using Multiplexed.Abstractions.AI.Steps;

namespace Multiplexed.Abstractions.AI.Invocation
{
    /// <summary>
    /// Trusted server extension for one invocation mechanism/language pair. Factories
    /// describe installed capabilities, not tenant implementations keyed by step name.
    /// No factory is installed by default for custom or MCP invocations.
    /// </summary>
    public interface IAiStepInvocationAdapterFactory
    {
        /// <summary>Must be Custom or Mcp; native steps keep the existing registry.</summary>
        AiInvocationKind Kind { get; }

        /// <summary>A canonical custom language; null for MCP.</summary>
        string? ExecutionLanguage { get; }

        /// <summary>
        /// Creates a server-side IAiStep for the immutable binding. Creation must not
        /// start a worker, call a tool, or execute business code. The returned adapter
        /// must not retain mutable execution/tenant context between ExecuteAsync calls.
        /// Publication authorization, portable inputs and durable dispatch belong to
        /// the eventual transport adapter, not to this factory-selection contract.
        /// </summary>
        IAiStep Create(AiStepInvocationAdapterContext context);
    }
}
