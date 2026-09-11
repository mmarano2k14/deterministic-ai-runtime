namespace Multiplexed.AI.Runtime.Invocation.Mcp
{
    /// <summary>
    /// Server-owned limit for a short invocation. It is not a lease, retry budget or
    /// durable timeout. Long-running and effectful calls require the durable contract.
    /// </summary>
    public sealed class AiMcpStepInvocationOptions
    {
        public TimeSpan InvocationTimeout { get; init; } = TimeSpan.FromSeconds(5);
    }
}
