namespace Multiplexed.Abstractions.AI.Invocation
{
    /// <summary>
    /// Origin of a policy declaration, not the checkpoint currently evaluating it.
    /// A pipeline policy remains pipeline-scoped when evaluated for a step.
    /// </summary>
    public enum AiPolicyBindingScope
    {
        Pipeline = 0,
        Step = 1
    }

    /// <summary>Immutable policy identity, declaration scope, and effective invocation.</summary>
    public sealed record AiPolicyInvocationBinding(
        string PolicyName,
        AiPolicyBindingScope Scope,
        string? OwnerStepName,
        AiInvocationBinding Invocation);
}
