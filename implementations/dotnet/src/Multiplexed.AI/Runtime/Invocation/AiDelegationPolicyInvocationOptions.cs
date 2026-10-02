namespace Multiplexed.AI.Runtime.Invocation
{
    /// <summary>Server-owned deadline applied to one hosted Delegation policy evaluation.</summary>
    public sealed class AiDelegationPolicyInvocationOptions
    {
        public TimeSpan EvaluationTimeout { get; init; } = TimeSpan.FromSeconds(5);
    }
}
