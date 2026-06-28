using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using System;
using System.Collections.Generic;
using System.Text;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Captures decision ledger records written by the runtime observability sink.
    /// </summary>
    public sealed class CapturingDecisionLedgerRecorder : IAiDecisionLedgerRecorder
    {
        /// <summary>
        /// Gets the captured ledger entries.
        /// </summary>
        public List<CapturedLedgerEntry> Entries { get; } = new();

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
            this.Entries.Add(
                new CapturedLedgerEntry(
                    context,
                    category,
                    eventType,
                    outcome,
                    reason,
                    metadata));

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Represents a captured decision ledger entry.
    /// </summary>
    /// <param name="Context">The captured ledger correlation context.</param>
    /// <param name="Category">The captured ledger category.</param>
    /// <param name="EventType">The captured ledger event type.</param>
    /// <param name="Outcome">The captured ledger outcome.</param>
    /// <param name="Reason">The captured ledger reason.</param>
    /// <param name="Metadata">The captured ledger metadata.</param>
    public sealed record CapturedLedgerEntry(
        AiRuntimeLedgerEventCorrelationContext Context,
        AiDecisionLedgerCategory Category,
        string EventType,
        AiDecisionLedgerOutcome Outcome,
        string? Reason,
        IReadOnlyDictionary<string, string?>? Metadata);
}
