using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure
{
    /// <summary>
    /// Represents one immutable first-class runtime-pool failure fact.
    /// </summary>
    public sealed record AiRuntimePoolFailureObservation
    {
        public required string FailureId { get; init; }

        public AiRuntimePoolFailureScope Scope { get; init; }

        public required string PoolId { get; init; }

        public required string HostId { get; init; }

        /// <summary>
        /// Gets the failed runtime identity for runtime-scoped failures.
        /// </summary>
        public string? RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the failed route identity for runtime-scoped failures.
        /// </summary>
        public string? RouteId { get; init; }

        public AiRuntimePoolFailureKind Kind { get; init; }

        public int? ExitCode { get; init; }

        public DateTimeOffset ObservedAtUtc { get; init; }

        /// <summary>
        /// Gets optional diagnostics that are never parsed for recovery correctness.
        /// </summary>
        public string? FailureMessage { get; init; }
    }
}
