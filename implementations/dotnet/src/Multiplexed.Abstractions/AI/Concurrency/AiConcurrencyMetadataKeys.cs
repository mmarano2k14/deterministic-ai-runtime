namespace Multiplexed.Abstractions.AI.Concurrency
{
    /// <summary>
    /// Defines canonical metadata keys associated with distributed concurrency coordination.
    /// </summary>
    public static class AiConcurrencyMetadataKeys
    {
        /// <summary>
        /// Gets the metadata key carrying the distributed concurrency lease identifier.
        /// </summary>
        public const string LeaseId = "lease.id";
    }
}
