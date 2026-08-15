using System.Collections.Generic;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results
{
    /// <summary>
    /// Contains the execution result for one submitted shared run.
    /// </summary>
    public sealed record ProductionRunScenarioResult
    {
        /// <summary>
        /// Gets the shared run id.
        /// </summary>
        public required string SharedRunId { get; init; }

        /// <summary>
        /// Gets the assigned runtime instance id.
        /// </summary>
        public string? RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the local runtime run id.
        /// </summary>
        public string? LocalRunId { get; init; }

        /// <summary>
        /// Gets the execution id produced by the runtime engine.
        /// </summary>
        public string? ExecutionId { get; init; }

        /// <summary>
        /// Gets the final runtime status.
        /// </summary>
        public string? FinalStatus { get; init; }

        /// <summary>
        /// Gets a value indicating whether ledger entries were available for the execution.
        /// </summary>
        public bool HasLedger { get; init; }

        /// <summary>
        /// Gets a value indicating whether trace events were available for the execution.
        /// </summary>
        public bool HasTrace { get; init; }

        /// <summary>
        /// Gets a value indicating whether replay report was available for the execution.
        /// </summary>
        public bool HasReplayReport { get; init; }

        /// <summary>
        /// Gets a value indicating whether replay ledger was available for the execution.
        /// </summary>
        public bool HasReplayLedger { get; init; }

        /// <summary>
        /// Gets a value indicating whether replay trace was available for the execution.
        /// </summary>
        public bool HasReplayTrace { get; init; }

        /// <summary>
        /// Gets the ordered child DAG relation results captured for this submitted parent execution.
        /// </summary>
        public IReadOnlyList<ProductionChildDagScenarioResult> ChildDagExecutions { get; init; } =
            Array.Empty<ProductionChildDagScenarioResult>();

        /// <summary>
        /// Gets arbitrary run-level metadata captured by the runner.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();
    }
}
