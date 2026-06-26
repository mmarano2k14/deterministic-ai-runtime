using System;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Describes the failed runtime instance and the local work that was affected.
    /// </summary>
    public sealed record AiRuntimeRecoveryFailureInfo
    {
        /// <summary>
        /// Gets the optional incident identifier shared by multiple recovery records caused by the same runtime failure.
        /// </summary>
        public string? RuntimeFailureIncidentId { get; init; }

        /// <summary>
        /// Gets the failed runtime instance identifier.
        /// </summary>
        public string? FailedRuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the failed local runtime run identifier.
        /// </summary>
        public string? FailedLocalRunId { get; init; }

        /// <summary>
        /// Gets the signal that caused the failure or unavailability detection.
        /// </summary>
        public string? FailureSignal { get; init; }

        /// <summary>
        /// Gets the runtime health status before suppression or failure handling.
        /// </summary>
        public string? HealthStatusBefore { get; init; }

        /// <summary>
        /// Gets the runtime health status after suppression or failure handling.
        /// </summary>
        public string? HealthStatusAfter { get; init; }

        /// <summary>
        /// Gets the reason why capacity was suppressed or removed.
        /// </summary>
        public string? SuppressCapacityReason { get; init; }

        /// <summary>
        /// Gets the UTC timestamp when the failure was detected.
        /// </summary>
        public DateTimeOffset? FailureDetectedAtUtc { get; init; }
    }
}