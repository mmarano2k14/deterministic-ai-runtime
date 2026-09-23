namespace Multiplexed.AI.McpServer.PublicSdk
{
    /// <summary>
    /// Server-side public execution Watch retention and bounded-read settings. Durable execution state and
    /// Decision Ledger retention remain independent from this public protocol window.
    /// </summary>
    public sealed class AiPublicSdkExecutionWatchOptions
    {
        /// <summary>
        /// Maximum number of projected public events considered resumable for one execution. Older public
        /// cursors require an authoritative snapshot even when the underlying Decision Ledger still retains
        /// the corresponding audit entries.
        /// </summary>
        public int RetainedPublicEventLimit { get; set; } = 4096;

        /// <summary>
        /// Maximum number of internal Decision Ledger entries read in one Watch query. Watch scans remain
        /// bounded regardless of the total execution history size.
        /// </summary>
        public int LedgerReadBatchSize { get; set; } = 256;

        /// <summary>
        /// Delay between bounded ledger polls when no additional entry is immediately available.
        /// </summary>
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(100);
    }
}
