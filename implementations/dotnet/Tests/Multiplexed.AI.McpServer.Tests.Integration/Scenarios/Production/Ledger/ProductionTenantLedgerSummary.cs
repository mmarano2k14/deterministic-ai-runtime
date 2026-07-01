using Multiplexed.Abstractions.AI.Observability.Ledger;
using System.Collections.Generic;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Ledger
{
    /// <summary>
    /// Represents tenant-scoped ledger evidence to summarize.
    /// </summary>
    /// <param name="TenantId">The tenant identifier.</param>
    /// <param name="RuntimeInstanceIds">The tenant runtime instance identifiers.</param>
    /// <param name="ExecutionIds">The tenant execution identifiers.</param>
    /// <param name="LedgerEntries">The tenant-scoped ledger entries.</param>
    public sealed record ProductionTenantLedgerSummary(
        string TenantId,
        IReadOnlyCollection<string> RuntimeInstanceIds,
        IReadOnlyCollection<string> ExecutionIds,
        IReadOnlyCollection<AiDecisionLedgerEntry> LedgerEntries);
}