namespace Multiplexed.Abstractions.AI.Publication
{
    /// <summary>
    /// Immutable run admission written before DAG creation. Retrying admission must reproduce
    /// the same publication, owner and canonical inputs; it cannot upgrade an existing run.
    /// </summary>
    public sealed record AiPublicationRunPin(
        int SchemaVersion, AiPublicationPartition Partition, string RunKey, string ExecutionId,
        string UserId, string PublicationRef, string PublicationSha256,
        string DefinitionSha256, string InputsJson, string InputsSha256);
}
