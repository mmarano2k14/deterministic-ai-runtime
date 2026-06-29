using System.Collections.Concurrent;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Ledger
{
    /// <summary>
    /// Captures decision ledger records produced during production integration scenarios.
    /// </summary>
    public sealed class CapturingIntegrationDecisionLedgerRecorder : IAiDecisionLedgerRecorder
    {
        private readonly ConcurrentQueue<CapturedIntegrationLedgerRecord> records = new();

        /// <summary>
        /// Gets the captured ledger records.
        /// </summary>
        public IReadOnlyList<CapturedIntegrationLedgerRecord> Records => this.records.ToArray();

        /// <summary>
        /// Clears all captured records.
        /// </summary>
        public void Clear()
        {
            while (this.records.TryDequeue(out _))
            {
            }
        }

        /// <inheritdoc />
        public Task RecordAsync(
            AiRuntimeLedgerEventCorrelationContext context,
            AiDecisionLedgerCategory category,
            string eventType,
            AiDecisionLedgerOutcome outcome,
            string? reason = null,
            IReadOnlyDictionary<string, string?>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            this.records.Enqueue(
                new CapturedIntegrationLedgerRecord(
                    DateTimeOffset.UtcNow,
                    context,
                    category,
                    eventType,
                    outcome,
                    reason,
                    metadata ?? new Dictionary<string, string?>()));

            return Task.CompletedTask;
        }
    }
}
