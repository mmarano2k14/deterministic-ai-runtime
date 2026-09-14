namespace Multiplexed.Abstractions.AI.Invocation.Durable
{
    /// <summary>
    /// References already-pinned publication material. Hashes describe expected bytes;
    /// this contract neither publishes artifacts nor verifies their availability.
    /// </summary>
    public sealed record AiDurableInvocationTarget(
        string PipelineName,
        string PipelineVersion,
        string DefinitionSha256,
        string PublicationRef,
        string PublicationSha256,
        string ImplementationRef,
        string ImplementationSha256,
        string ExecutionLanguage,
        string EnvironmentRef,
        string EnvironmentSha256);

    /// <summary>
    /// Frozen preparation for one operation. InputsJson contains resolved JSON data only,
    /// not an execution context, service provider, RBAC snapshot or engine store reference.
    /// These are internal server contracts, not the future external SDK's models.
    /// </summary>
    public sealed record AiDurableInvocationDefinition(
        AiDurableInvocationIdentity Identity,
        AiDurableInvocationScope Scope,
        AiDurableInvocationTarget Target,
        string InputsJson);
}
