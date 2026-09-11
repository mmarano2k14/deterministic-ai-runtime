namespace Multiplexed.Abstractions.AI.Invocation.Durable
{
    /// <summary>
    /// Server-created evidence stored with the DAG result in the same terminal transition.
    /// A worker response never supplies this receipt. Reading a response is not application.
    /// </summary>
    public sealed record AiDurableInvocationApplicationReceipt(string OperationId, string ResultSha256);
}
