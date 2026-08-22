namespace Multiplexed.Abstractions.AI.Policies
{
    /// <summary>
    /// Defines canonical metadata keys associated with policy evaluation and observability.
    /// </summary>
    public static class AiPolicyMetadataKeys
    {
        /// <summary>
        /// Gets the metadata key carrying the policy name.
        /// </summary>
        public const string Name = "policy.name";

        /// <summary>
        /// Gets the metadata key carrying the policy engine kind.
        /// </summary>
        public const string Kind = "policy.kind";

        /// <summary>
        /// Gets the metadata key carrying the policy result kind.
        /// </summary>
        public const string ResultKind = "result.kind";

        /// <summary>
        /// Gets the metadata key carrying whether the policy result succeeded.
        /// </summary>
        public const string ResultSuccess = "result.success";
    }
}
