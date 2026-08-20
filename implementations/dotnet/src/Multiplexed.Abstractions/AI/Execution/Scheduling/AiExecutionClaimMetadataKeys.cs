namespace Multiplexed.Abstractions.AI.Execution.Scheduling
{
    /// <summary>
    /// Defines canonical metadata keys associated with distributed execution claims.
    /// </summary>
    public static class AiExecutionClaimMetadataKeys
    {
        /// <summary>
        /// Gets the metadata key carrying the distributed claim token.
        /// </summary>
        public const string ClaimToken = "claim.token";

        /// <summary>
        /// Gets the camel-case claim token metadata key used by compatibility and diagnostic payloads.
        /// </summary>
        public const string CamelCaseClaimToken = "claimToken";
    }
}
