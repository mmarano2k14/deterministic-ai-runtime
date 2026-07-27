using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure
{
    /// <summary>
    /// Represents one immutable first-class runtime-pool failure fact.
    /// </summary>
    public sealed record AiRuntimePoolFailureObservation
    {
        /// <summary>
        /// Gets the immutable failure observation identifier.
        /// </summary>
        public required string FailureId { get; init; }

        /// <summary>
        /// Gets the authoritative failure scope.
        /// </summary>
        public AiRuntimePoolFailureScope Scope { get; init; }

        /// <summary>
        /// Gets the logical runtime pool identifier.
        /// </summary>
        public required string PoolId { get; init; }

        /// <summary>
        /// Gets the immutable host-incarnation identifier.
        /// </summary>
        public required string HostId { get; init; }

        /// <summary>
        /// Gets the exact independently registered runtime instance identifier.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the exact route incarnation that belonged to the failed runtime.
        /// </summary>
        public required string RouteId { get; init; }

        /// <summary>
        /// Gets the typed failure kind.
        /// </summary>
        public AiRuntimePoolFailureKind Kind { get; init; }

        /// <summary>
        /// Gets the optional operating-system exit code.
        /// </summary>
        public int? ExitCode { get; init; }

        /// <summary>
        /// Gets when the failure was observed.
        /// </summary>
        public DateTimeOffset ObservedAtUtc { get; init; }

        /// <summary>
        /// Gets the optional diagnostic lifecycle message.
        /// </summary>
        /// <remarks>
        /// This value is diagnostic only and is never parsed for recovery correctness.
        /// </remarks>
        public string? FailureMessage { get; init; }
    }
}
