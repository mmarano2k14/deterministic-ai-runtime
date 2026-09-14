namespace Multiplexed.Abstractions.AI.Invocation
{
    /// <summary>
    /// Opt-in identity for server policy adapters. Native policies retain their historical
    /// CLR type name unless they explicitly implement this contract. Metadata is server
    /// generated and must not contain policy configuration, credentials or response data.
    /// </summary>
    public interface IAiPolicyInvocationIdentity
    {
        string PolicyName { get; }
        IReadOnlyDictionary<string, string> InvocationMetadata { get; }
    }

    /// <summary>Additional fields on existing policy events; no new event family.</summary>
    public static class AiPolicyInvocationMetadataKeys
    {
        public const string RequestId = "policy.request.id";
        public const string InvocationKind = "policy.invocation.kind";
        public const string ExecutionLanguage = "policy.execution.language";
        public const string ImplementationRef = "policy.implementation.ref";
        public const string Scope = "policy.scope";
        public const string OwnerStepName = "policy.owner.step";
        public const string AdapterType = "policy.adapter.type";
    }
}
