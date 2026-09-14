namespace Multiplexed.Abstractions.AI.Invocation.Durable
{
    /// <summary>A server lookup keyed by an already verified execution-bound definition, never latest.</summary>
    public sealed record AiDurableInvocationTargetRequest(
        AiDurableInvocationScope Scope, string ExecutionId, string DefinitionSha256,
        string PipelineName, string PipelineVersion, string StepName, string StepKey,
        string ImplementationRef, string ExecutionLanguage);

    /// <summary>
    /// Resolves authorized immutable publication material for a verified DAG call site.
    /// Implementations must honor the tenant and exact definition digest; they must not
    /// select latest, execute code, or expose engine credentials. No default is installed.
    /// This server interface is not a contract for the external SDK or an artifact uploader.
    /// </summary>
    public interface IAiDurableInvocationTargetResolver
    {
        Task<AiDurableInvocationTarget?> ResolveAsync(
            AiDurableInvocationTargetRequest request, CancellationToken cancellationToken = default);
    }
}
