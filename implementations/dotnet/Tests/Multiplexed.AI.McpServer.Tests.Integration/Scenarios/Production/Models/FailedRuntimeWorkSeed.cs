using System;
using System.Collections.Generic;
using System.Text;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Models
{
    public sealed record FailedRuntimeWorkSeed
    {
        /// <summary>
        /// Gets the durable shared run identifier.
        /// </summary>
        public required string SharedRunId { get; init; }

        /// <summary>
        /// Gets the failed local runtime run identifier.
        /// </summary>
        public required string FailedLocalRunId { get; init; }

        /// <summary>
        /// Gets the optional durable execution identifier.
        /// </summary>
        public string? ExecutionId { get; init; }

        /// <summary>
        /// Gets the work kind.
        /// </summary>
        public required FailedRuntimeWorkKind Kind { get; init; }
    }

    /// <summary>
    /// Represents the kind of durable work assigned to a failed runtime instance.
    /// </summary>
    public enum FailedRuntimeWorkKind
    {
        /// <summary>
        /// Work was assigned to the runtime but still queued locally.
        /// </summary>
        LocalQueued,

        /// <summary>
        /// Work had already started a durable execution.
        /// </summary>
        InFlightExecution
    }
}
