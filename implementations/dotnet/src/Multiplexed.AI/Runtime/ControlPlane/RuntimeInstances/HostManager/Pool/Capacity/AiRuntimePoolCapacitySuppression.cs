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
        /// Gets the authoritative suppression scope.
        /// </summary>
        public AiRuntimePoolCapacitySuppressionScope Scope { get; init; } =
            AiRuntimePoolCapacitySuppressionScope.RuntimeInstanceRoute;

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
        /// Gets the exact route incarnation when the suppression authority is route-scoped.
        /// </summary>
        /// <remarks>
        /// Host-membership suppression intentionally has no route identity. Pool routes are local
        /// to the failed host and cannot be treated as durable cross-host membership authority.
        /// </remarks>
        public string? RouteId { get; init; }

        /// <summary>
        /// Gets when capacity became unsafe.
        /// </summary>
        public DateTimeOffset SuppressedAtUtc { get; init; }
    }
}
