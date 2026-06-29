using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Ledger
{
    /// <summary>
    /// Captured decision ledger record used by production integration proof output and assertions.
    /// </summary>
    /// <param name="CapturedAtUtc">The capture timestamp.</param>
    /// <param name="Context">The ledger correlation context.</param>
    /// <param name="Category">The ledger category.</param>
    /// <param name="EventType">The ledger event type.</param>
    /// <param name="Outcome">The ledger outcome.</param>
    /// <param name="Reason">The optional reason.</param>
    /// <param name="Metadata">The metadata.</param>
    public sealed record CapturedIntegrationLedgerRecord(
        DateTimeOffset CapturedAtUtc,
        AiRuntimeLedgerEventCorrelationContext Context,
        AiDecisionLedgerCategory Category,
        string EventType,
        AiDecisionLedgerOutcome Outcome,
        string? Reason,
        IReadOnlyDictionary<string, string?> Metadata);
}
