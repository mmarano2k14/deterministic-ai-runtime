using System;
using System.Collections.Generic;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Represents a query/read model projection for one runtime recovery forensics record.
    /// </summary>
    public sealed record AiRuntimeRecoveryForensicsReadModel
    {
        /// <summary>
        /// Gets the stable forensics identifier.
        /// </summary>
        public required string ForensicsId { get; init; }

        /// <summary>
        /// Gets the durable execution identifier.
        /// </summary>
        /// <remarks>
        /// This value can be empty for local queued recovery records because no durable DAG execution
        /// had been started on the failed runtime yet.
        /// </remarks>
        public required string ExecutionId { get; init; }

        /// <summary>
        /// Gets the optional shared run identifier.
        /// </summary>
        public string? SharedRunId { get; init; }

        /// <summary>
        /// Gets the optional tenant identifier.
        /// </summary>
        public string? TenantId { get; init; }

        /// <summary>
        /// Gets the optional control-plane identifier.
        /// </summary>
        public string? ControlPlaneId { get; init; }

        /// <summary>
        /// Gets the optional runtime failure incident identifier.
        /// </summary>
        public string? RuntimeFailureIncidentId { get; init; }

        /// <summary>
        /// Gets the record creation time.
        /// </summary>
        public DateTimeOffset CreatedAtUtc { get; init; }

        /// <summary>
        /// Gets the record update time.
        /// </summary>
        public DateTimeOffset UpdatedAtUtc { get; init; }

        /// <summary>
        /// Gets the ordered recovery timeline.
        /// </summary>
        public IReadOnlyList<AiRuntimeRecoveryForensicsTimelineItem> Timeline { get; init; } = [];

        /// <summary>
        /// Gets the original forensics record for detailed API consumers.
        /// </summary>
        public required AiRuntimeRecoveryForensicsRecord Record { get; init; }
    }
}