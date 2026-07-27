using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity
{
    /// <summary>
    /// Represents an immutable exact runtime-instance capacity suppression.
    /// </summary>
    public sealed record AiRuntimePoolCapacitySuppression
    {
        /// <summary>
        /// Gets the failure observation that caused the suppression.
        /// </summary>
        public required string FailureId { get; init; }

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
        /// Gets the exact route incarnation owned by the failed runtime.
        /// </summary>
        public required string RouteId { get; init; }

        /// <summary>
        /// Gets when capacity became unsafe.
        /// </summary>
        public DateTimeOffset SuppressedAtUtc { get; init; }
    }
}
