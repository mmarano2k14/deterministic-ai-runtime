namespace Multiplexed.AI.Runtime.Invocation
{
    /// <summary>Server-owned deadline for one short custom Retry policy evaluation.</summary>
    public sealed class AiRetryPolicyInvocationOptions
    {
        public TimeSpan EvaluationTimeout { get; init; } = TimeSpan.FromSeconds(5);
    }
}
