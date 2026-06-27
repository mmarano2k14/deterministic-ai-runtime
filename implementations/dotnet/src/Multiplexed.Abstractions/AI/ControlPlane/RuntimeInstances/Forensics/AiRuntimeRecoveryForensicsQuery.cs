using System;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Represents read-only runtime recovery forensics search criteria.
    /// </summary>
    public sealed record AiRuntimeRecoveryForensicsQuery
    {
        /// <summary>
        /// Gets the optional stable forensics identifier.
        /// </summary>
        public string? ForensicsId { get; init; }

        /// <summary>
        /// Gets the optional durable execution identifier.
        /// </summary>
        public string? ExecutionId { get; init; }

        /// <summary>
        /// Gets the optional shared run identifier.
        /// </summary>
        public string? SharedRunId { get; init; }

        /// <summary>
        /// Gets the optional runtime instance identifier. This matches either the failed runtime or the replacement runtime.
        /// </summary>
        public string? RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the optional runtime failure incident identifier.
        /// </summary>
        public string? RuntimeFailureIncidentId { get; init; }

        /// <summary>
        /// Gets the optional tenant identifier.
        /// </summary>
        public string? TenantId { get; init; }

        /// <summary>
        /// Gets the optional control-plane identifier.
        /// </summary>
        public string? ControlPlaneId { get; init; }

        /// <summary>
        /// Gets the optional recovery event type to match inside the timeline.
        /// </summary>
        public string? EventType { get; init; }

        /// <summary>
        /// Gets a value indicating whether only records containing a failed recovery event should be returned.
        /// </summary>
        public bool RecentFailuresOnly { get; init; }

        /// <summary>
        /// Gets the optional lower bound for record creation time.
        /// </summary>
        public DateTimeOffset? CreatedFromUtc { get; init; }

        /// <summary>
        /// Gets the optional upper bound for record creation time.
        /// </summary>
        public DateTimeOffset? CreatedToUtc { get; init; }

        /// <summary>
        /// Gets the maximum number of records to return.
        /// </summary>
        public int Limit { get; init; } = 100;
    }
}
