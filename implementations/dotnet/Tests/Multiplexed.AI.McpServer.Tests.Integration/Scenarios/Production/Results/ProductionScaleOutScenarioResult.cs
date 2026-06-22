using System;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results
{
    /// <summary>
    /// Contains the scale-out result observed for one shared run.
    /// </summary>
    public sealed record ProductionScaleOutScenarioResult
    {
        /// <summary>
        /// Gets the scale-out request id.
        /// </summary>
        public required string RequestId { get; init; }

        /// <summary>
        /// Gets the shared run id linked to the scale-out request.
        /// </summary>
        public required string SharedRunId { get; init; }

        /// <summary>
        /// Gets the tenant id linked to the scale-out request.
        /// </summary>
        public required string TenantId { get; init; }

        /// <summary>
        /// Gets the final scale-out request status.
        /// </summary>
        public required string Status { get; init; }

        /// <summary>
        /// Gets the fulfilled runtime instance id, when the request was fulfilled.
        /// </summary>
        public string? FulfilledRuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the rejection reason, when the request was rejected.
        /// </summary>
        public string? RejectionReason { get; init; }

        /// <summary>
        /// Gets the fulfillment timestamp.
        /// </summary>
        public DateTimeOffset? FulfilledAtUtc { get; init; }

        /// <summary>
        /// Gets the rejection timestamp.
        /// </summary>
        public DateTimeOffset? RejectedAtUtc { get; init; }
    }
}