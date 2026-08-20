namespace Multiplexed.AI.Runtime.Execution.Payloads
{
    /// <summary>
    /// Defines implementation-level payload identifiers shared by payload compaction and storage backends.
    /// </summary>
    internal static class AiPayloadIdentifiers
    {
        /// <summary>The diagnostic execution identifier used when no execution id is available.</summary>
        public const string UnknownExecutionId = "unknown-execution";
    }
}
