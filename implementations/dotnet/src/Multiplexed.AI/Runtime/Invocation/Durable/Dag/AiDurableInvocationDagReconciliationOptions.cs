using Multiplexed.Abstractions.AI.Invocation.Durable;

namespace Multiplexed.AI.Runtime.Invocation.Durable.Dag
{
    /// <summary>Explicit trusted host scopes. Tenant enumeration/publication discovery is not provided here.</summary>
    public sealed class AiDurableInvocationDagReconciliationOptions
    {
        public IReadOnlyList<AiDurableInvocationScope> Scopes { get; init; } = Array.Empty<AiDurableInvocationScope>();
        public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);
        public int BatchSize { get; init; } = 100;
    }
}
