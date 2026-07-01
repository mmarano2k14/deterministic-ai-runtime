using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Ledger
{
    /// <summary>
    /// Provides scenario-scoped tenant ledger queries for production control-plane recovery proofs.
    /// </summary>
    public static class ProductionControlPlaneLedgerTenantQuery
    {
        /// <summary>
        /// Queries tenant ledger evidence scoped to recovered runtime instances and recovered executions.
        /// </summary>
        /// <param name="mcp">The tenant-scoped MCP client.</param>
        /// <param name="recovery">The failed runtime recovery proof.</param>
        /// <param name="executionIds">The recovered execution identifiers.</param>
        /// <param name="timestampFromUtc">The inclusive lower timestamp bound.</param>
        /// <param name="timestampToUtc">The inclusive upper timestamp bound.</param>
        /// <returns>The scoped tenant ledger query result.</returns>
        public static async Task<ProductionControlPlaneLedgerTenantQueryResult> QueryRecoveredTenantLedgerEvidenceAsync(
            McpTestClient mcp,
            RealRuntimeCrashFailedRuntimeRecoveryProof recovery,
            IReadOnlyCollection<string> executionIds,
            DateTimeOffset timestampFromUtc,
            DateTimeOffset timestampToUtc)
        {
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentNullException.ThrowIfNull(recovery);
            ArgumentNullException.ThrowIfNull(executionIds);

            var runtimeInstanceIds =
                GetRecoveredRuntimeInstanceIds(recovery);

            var normalizedExecutionIds =
                executionIds
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

            var runtimeEntries =
                await QueryByRuntimeInstancesAsync(
                    mcp,
                    runtimeInstanceIds,
                    timestampFromUtc,
                    timestampToUtc)
                .ConfigureAwait(false);

            var executionEntries =
                await QueryByExecutionsAsync(
                    mcp,
                    normalizedExecutionIds,
                    timestampFromUtc,
                    timestampToUtc)
                .ConfigureAwait(false);

            var entries =
                runtimeEntries
                    .Concat(executionEntries)
                    .GroupBy(CreateLedgerEntryDeduplicationKey, StringComparer.Ordinal)
                    .Select(group => group.OrderBy(entry => entry.TimestampUtc).ThenBy(entry => entry.Sequence).First())
                    .OrderBy(entry => entry.TimestampUtc)
                    .ThenBy(entry => entry.Sequence)
                    .ToArray();

            return new ProductionControlPlaneLedgerTenantQueryResult(
                runtimeInstanceIds,
                normalizedExecutionIds,
                entries);
        }

        /// <summary>
        /// Gets failed and replacement runtime instance identifiers from a recovery proof.
        /// </summary>
        /// <param name="recovery">The failed runtime recovery proof.</param>
        /// <returns>The distinct runtime instance identifiers.</returns>
        public static IReadOnlyCollection<string> GetRecoveredRuntimeInstanceIds(
            RealRuntimeCrashFailedRuntimeRecoveryProof recovery)
        {
            ArgumentNullException.ThrowIfNull(recovery);

            return recovery
                .RecoveredWorks
                .SelectMany(work => new[]
                {
                    recovery.FailedInventory.RuntimeInstanceId,
                    work.Original.SharedRun.AssignedRuntimeInstanceId,
                    work.RedispatchedRun.AssignedRuntimeInstanceId,
                    work.ReplacementRuntimeInstanceId
                })
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray()!;
        }

        private static async Task<IReadOnlyCollection<AiDecisionLedgerEntry>> QueryByRuntimeInstancesAsync(
            McpTestClient mcp,
            IReadOnlyCollection<string> runtimeInstanceIds,
            DateTimeOffset timestampFromUtc,
            DateTimeOffset timestampToUtc)
        {
            var entries =
                new List<AiDecisionLedgerEntry>();

            foreach (var runtimeInstanceId in runtimeInstanceIds.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
            {
                var currentEntries =
                    await mcp
                        .QueryLedgerAsync(
                            new AiDecisionLedgerQuery
                            {
                                RuntimeInstanceId = runtimeInstanceId,
                                TimestampFromUtc = timestampFromUtc,
                                TimestampToUtc = timestampToUtc
                            })
                        .ConfigureAwait(false);

                entries.AddRange(currentEntries);
            }

            return entries;
        }

        private static async Task<IReadOnlyCollection<AiDecisionLedgerEntry>> QueryByExecutionsAsync(
            McpTestClient mcp,
            IReadOnlyCollection<string> executionIds,
            DateTimeOffset timestampFromUtc,
            DateTimeOffset timestampToUtc)
        {
            var entries =
                new List<AiDecisionLedgerEntry>();

            foreach (var executionId in executionIds.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
            {
                var currentEntries =
                    await mcp
                        .QueryLedgerAsync(
                            new AiDecisionLedgerQuery
                            {
                                ExecutionId = executionId,
                                TimestampFromUtc = timestampFromUtc,
                                TimestampToUtc = timestampToUtc
                            })
                        .ConfigureAwait(false);

                entries.AddRange(currentEntries);
            }

            return entries;
        }

        private static string CreateLedgerEntryDeduplicationKey(
            AiDecisionLedgerEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            if (!string.IsNullOrWhiteSpace(entry.EntryId))
            {
                return entry.EntryId;
            }

            return string.Join(
                "|",
                entry.Sequence.ToString(),
                entry.TimestampUtc.ToString("O"),
                entry.Category.ToString(),
                entry.EventType ?? string.Empty,
                entry.Outcome.ToString(),
                entry.CorrelationContext.ExecutionId ?? string.Empty,
                entry.CorrelationContext.RunId ?? string.Empty,
                entry.CorrelationContext.PipelineName ?? string.Empty,
                entry.CorrelationContext.StepId ?? string.Empty,
                entry.CorrelationContext.StepKey ?? string.Empty,
                entry.CorrelationContext.RuntimeInstanceId ?? string.Empty,
                entry.CorrelationContext.WorkerId ?? string.Empty,
                entry.CorrelationContext.Operation ?? string.Empty,
                entry.CorrelationContext.TraceId ?? string.Empty,
                entry.CorrelationContext.CorrelationId ?? string.Empty);
        }
    }

    /// <summary>
    /// Represents scenario-scoped tenant ledger evidence returned from runtime and execution queries.
    /// </summary>
    /// <param name="RuntimeInstanceIds">The runtime instance identifiers used by the query.</param>
    /// <param name="ExecutionIds">The execution identifiers used by the query.</param>
    /// <param name="Entries">The distinct ledger entries returned by the query.</param>
    public sealed record ProductionControlPlaneLedgerTenantQueryResult(
        IReadOnlyCollection<string> RuntimeInstanceIds,
        IReadOnlyCollection<string> ExecutionIds,
        IReadOnlyCollection<AiDecisionLedgerEntry> Entries);
}