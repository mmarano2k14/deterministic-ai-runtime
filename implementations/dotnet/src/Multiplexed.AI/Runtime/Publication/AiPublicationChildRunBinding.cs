namespace Multiplexed.AI.Runtime.Publication
{
    /// <summary>
    /// Immutable server-side binding from one allocated Child DAG execution to the publication material
    /// already selected by its published parent. It does not replace the Child DAG relation or root run pin.
    /// </summary>
    internal sealed record AiPublicationChildRunBinding(
        int SchemaVersion,
        Multiplexed.Abstractions.AI.Publication.AiPublicationPartition Partition,
        string ExecutionId,
        string ParentExecutionId,
        string UserId,
        string PublicationRef,
        string PublicationSha256,
        string DefinitionPath,
        string DefinitionSha256,
        string PipelineName,
        string PipelineVersion);

    /// <summary>
    /// Unified immutable publication association used internally by root and nested executions.
    /// </summary>
    internal sealed record AiPublicationExecutionBinding(
        string PublicationRef,
        string PublicationSha256,
        string DefinitionSha256,
        string? DefinitionPath);
}
