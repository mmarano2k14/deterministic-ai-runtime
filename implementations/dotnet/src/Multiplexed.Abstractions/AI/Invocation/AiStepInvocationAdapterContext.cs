namespace Multiplexed.Abstractions.AI.Invocation
{
    /// <summary>
    /// Immutable plan metadata passed to a trusted server adapter factory. This is not
    /// a worker request or proof of publication authorization. Execution/tenant context
    /// is supplied later by the runtime for each call, never cached in this descriptor.
    /// </summary>
    public sealed record AiStepInvocationAdapterContext(
        string PipelineName,
        string? PipelineVersion,
        string StepName,
        string StepKey,
        AiInvocationBinding Binding);
}
