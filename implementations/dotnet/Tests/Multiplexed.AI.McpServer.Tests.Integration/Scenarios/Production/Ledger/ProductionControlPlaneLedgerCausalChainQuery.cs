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
    /// Provides scenario/control-plane scoped ledger queries for reusable production causal-chain proofs.
    /// </summary>
    public static class ProductionControlPlaneLedgerCausalChainQuery
    {
        /// <summary>
        /// Queries causal-chain ledger evidence for a recovered production scenario.
        /// </summary>
        /// <param name="mcp">The MCP client used to query the durable ledger.</param>
        /// <param name="controlPlaneId">The control-plane identifier.</param>
        /// <param name="tenantIds">The tenant identifiers involved in the scenario.</param>
        /// <param name="recoveries">The runtime recovery proofs.</param>
        /// <param name="executionIds">The recovered execution identifiers.</param>
        /// <param name="pipelinePrefixes">The scenario pipeline prefixes.</param>
        /// <param name="timestampFromUtc">The inclusive lower timestamp bound.</param>
        /// <param name="timestampToUtc">The inclusive upper timestamp bound.</param>
        /// <returns>The scenario/control-plane causal-chain ledger entries.</returns>
        public static async Task<IReadOnlyCollection<AiDecisionLedgerEntry>> QueryRecoveredScenarioCausalChainEvidenceAsync(
            McpTestClient mcp,
            string controlPlaneId,
            IReadOnlyCollection<string> tenantIds,
            IReadOnlyCollection<RealRuntimeCrashFailedRuntimeRecoveryProof> recoveries,
            IReadOnlyCollection<string> executionIds,
            IReadOnlyCollection<string> pipelinePrefixes,
            DateTimeOffset timestampFromUtc,
            DateTimeOffset timestampToUtc)
        {
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentNullException.ThrowIfNull(tenantIds);
            ArgumentNullException.ThrowIfNull(recoveries);
            ArgumentNullException.ThrowIfNull(executionIds);
            ArgumentNullException.ThrowIfNull(pipelinePrefixes);

            var runtimeInstanceIds =
                recoveries
                    .SelectMany(ProductionControlPlaneLedgerTenantQuery.GetRecoveredRuntimeInstanceIds)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

            var sharedRunIds =
                recoveries
                    .SelectMany(recovery => recovery.RecoveredWorks)
                    .SelectMany(work => new[]
                    {
                        work.Original.SharedRunId,
                        work.RedispatchedRun.SharedRunId
                    })
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

            var localRunIds =
                recoveries
                    .SelectMany(recovery => recovery.RecoveredWorks)
                    .SelectMany(work => new[]
                    {
                        work.Original.LocalRunId,
                        work.Original.SharedRun.LocalRunId,
                        work.RedispatchedRun.LocalRunId,
                        work.ReplacementLocalRunId
                    })
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

            var recoveryExecutionIds =
                recoveries
                    .SelectMany(recovery => recovery.RecoveredWorks)
                    .SelectMany(work => new[]
                    {
                        work.Original.ExecutionId,
                        work.RecoveredExecutionId,
                        work.RedispatchedRun.ExecutionId
                    })
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

            var normalizedExecutionIds =
                executionIds
                    .Concat(recoveryExecutionIds!)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

            var controlPlaneRunExecutionIds =
                sharedRunIds
                    .Select(sharedRunId => $"control-plane-run:{sharedRunId}")
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

            var normalizedTenantIds =
                tenantIds
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

            var normalizedPipelinePrefixes =
                pipelinePrefixes
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

            var controlPlaneRunEntries =
                await QueryByExecutionsAsync(
                    mcp,
                    controlPlaneRunExecutionIds,
                    timestampFromUtc,
                    timestampToUtc)
                .ConfigureAwait(false);

            var entries =
                runtimeEntries
                    .Concat(executionEntries)
                    .Concat(controlPlaneRunEntries)
                    .Where(entry =>
                        IsCausalChainEvent(entry) &&
                        BelongsToScenario(
                            entry,
                            controlPlaneId,
                            normalizedTenantIds,
                            runtimeInstanceIds,
                            sharedRunIds,
                            localRunIds,
                            normalizedExecutionIds,
                            normalizedPipelinePrefixes))
                    .GroupBy(CreateLedgerEntryDeduplicationKey, StringComparer.Ordinal)
                    .Select(group => group.OrderBy(entry => entry.TimestampUtc).ThenBy(entry => entry.Sequence).First())
                    .OrderBy(entry => entry.TimestampUtc)
                    .ThenBy(entry => entry.Sequence)
                    .ToArray();

            return entries;
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

        private static bool IsCausalChainEvent(
            AiDecisionLedgerEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            if (string.IsNullOrWhiteSpace(entry.EventType))
            {
                return false;
            }

            return entry.EventType.StartsWith("control.", StringComparison.Ordinal) ||
                entry.EventType.Contains("runtime-scale-out-request-publish", StringComparison.Ordinal) ||
                entry.EventType.Contains("runtime-scale-out-request-watch", StringComparison.Ordinal) ||
                entry.EventType.Contains("runtime-scale-out-provider-selection", StringComparison.Ordinal) ||
                entry.EventType.Contains("runtime-host-creation", StringComparison.Ordinal) ||
                entry.EventType.Contains("runtime-process-host-creation", StringComparison.Ordinal) ||
                entry.EventType.Contains("runtime-instance-capacity-publish", StringComparison.Ordinal) ||
                entry.EventType.Contains("runtime-instance-capacity-get", StringComparison.Ordinal) ||
                entry.EventType.Contains("runtime-instance-register", StringComparison.Ordinal) ||
                entry.EventType.Contains("runtime-instance-get", StringComparison.Ordinal) ||
                entry.EventType.Contains("runtime-instance-list", StringComparison.Ordinal) ||
                entry.EventType.Contains("runtime-instance-mark-unhealthy", StringComparison.Ordinal) ||
                entry.EventType.Contains("runtime-instance-mark-unsafe", StringComparison.Ordinal) ||
                entry.EventType.Contains("runtime-instance-suppress-capacity", StringComparison.Ordinal) ||
                entry.EventType.Contains("runtime-execution-recovery-reconcile", StringComparison.Ordinal) ||
                entry.EventType.Contains("remote-shared-run-dispatch", StringComparison.Ordinal);
        }

        private static bool BelongsToScenario(
            AiDecisionLedgerEntry entry,
            string controlPlaneId,
            IReadOnlyCollection<string> tenantIds,
            IReadOnlyCollection<string> runtimeInstanceIds,
            IReadOnlyCollection<string> sharedRunIds,
            IReadOnlyCollection<string> localRunIds,
            IReadOnlyCollection<string> executionIds,
            IReadOnlyCollection<string> pipelinePrefixes)
        {
            var haystack =
                CreateSearchText(entry);

            return haystack.Contains(controlPlaneId, StringComparison.Ordinal) ||
                tenantIds.Any(value => haystack.Contains(value, StringComparison.Ordinal)) ||
                runtimeInstanceIds.Any(value => haystack.Contains(value, StringComparison.Ordinal)) ||
                sharedRunIds.Any(value => haystack.Contains(value, StringComparison.Ordinal)) ||
                localRunIds.Any(value => haystack.Contains(value, StringComparison.Ordinal)) ||
                executionIds.Any(value => haystack.Contains(value, StringComparison.Ordinal)) ||
                pipelinePrefixes.Any(value => haystack.Contains(value, StringComparison.Ordinal));
        }

        private static string CreateSearchText(
            AiDecisionLedgerEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            var metadataText =
                entry.Metadata is null
                    ? string.Empty
                    : string.Join("|", entry.Metadata.Select(pair => $"{pair.Key}={pair.Value}"));

            return string.Join(
                "|",
                entry.EntryId ?? string.Empty,
                entry.EventType ?? string.Empty,
                entry.Category.ToString(),
                entry.Outcome.ToString(),
                entry.Reason ?? string.Empty,
                entry.CorrelationContext.ExecutionId ?? string.Empty,
                entry.CorrelationContext.RunId ?? string.Empty,
                entry.CorrelationContext.PipelineName ?? string.Empty,
                entry.CorrelationContext.StepId ?? string.Empty,
                entry.CorrelationContext.StepKey ?? string.Empty,
                entry.CorrelationContext.RuntimeInstanceId ?? string.Empty,
                entry.CorrelationContext.WorkerId ?? string.Empty,
                entry.CorrelationContext.Operation ?? string.Empty,
                entry.CorrelationContext.TraceId ?? string.Empty,
                entry.CorrelationContext.CorrelationId ?? string.Empty,
                metadataText);
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
}