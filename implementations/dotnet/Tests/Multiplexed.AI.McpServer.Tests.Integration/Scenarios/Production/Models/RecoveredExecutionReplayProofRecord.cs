using System;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Models
{
    /// <summary>
    /// Represents replay proof for one recovered execution.
    /// </summary>
    public sealed class RecoveredExecutionReplayProofRecord
    {
        /// <summary>
        /// Gets the tenant identifier.
        /// </summary>
        public required string TenantId { get; init; }

        /// <summary>
        /// Gets the runtime instance identifier.
        /// </summary>
        public string? RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the local run identifier.
        /// </summary>
        public string? LocalRunId { get; init; }

        /// <summary>
        /// Gets the execution identifier.
        /// </summary>
        public required string ExecutionId { get; init; }

        /// <summary>
        /// Gets a value indicating whether strict replay execution succeeded.
        /// </summary>
        public bool ReplaySucceeded { get; init; }

        /// <summary>
        /// Gets the strict replay failure reason, when replay execution did not succeed.
        /// </summary>
        public string? ReplayFailureReason { get; init; }

        /// <summary>
        /// Gets a value indicating whether the recovered execution is a synthetic seeded recovery execution.
        /// </summary>
        public bool SyntheticRecoveredExecution { get; init; }

        /// <summary>
        /// Gets a value indicating whether replay report was available.
        /// </summary>
        public bool ReplayReportAvailable { get; init; }

        /// <summary>
        /// Gets a value indicating whether replay ledger was available.
        /// </summary>
        public bool ReplayLedgerAvailable { get; init; }

        /// <summary>
        /// Gets a value indicating whether replay trace was available.
        /// </summary>
        public bool ReplayTraceAvailable { get; init; }

        /// <summary>
        /// Gets a value indicating whether the original execution ledger was available through MCP.
        /// </summary>
        public bool ExecutionLedgerAvailable { get; init; }

        /// <summary>
        /// Gets a value indicating whether the original execution trace was available through MCP.
        /// </summary>
        public bool ExecutionTraceAvailable { get; init; }

        /// <summary>
        /// Gets a value indicating whether the original execution ledger contains completion evidence.
        /// </summary>
        public bool CompletionLedgerEvidenceAvailable { get; init; }

        /// <summary>
        /// Gets a value indicating whether the original execution ledger contains step completion evidence.
        /// </summary>
        public bool StepCompletionLedgerEvidenceAvailable { get; init; }
    }
}