namespace Multiplexed.AI.Runtime.Invocation
{
    /// <summary>
    /// Server-owned deadline for short policy evaluations. It is copied and validated by
    /// the adapter factory, never taken from a tenant's policy configuration. This bound
    /// limits asynchronous waiting; it is not a process sandbox or a CPU watchdog.
    /// </summary>
    public sealed class AiConcurrencyPolicyInvocationOptions
    {
        public TimeSpan EvaluationTimeout { get; init; } = TimeSpan.FromSeconds(5);
    }
}
