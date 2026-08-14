using System;
using System.Collections.Generic;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork
{
    /// <summary>
    /// Represents one read-only inventory of recoverable work assigned to an exact failed runtime.
    /// </summary>
    public sealed record AiRuntimePoolAssignedWorkInventory
    {
        /// <summary>
        /// Gets the failure observation that authorized the inventory.
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
        /// Gets the exact failed runtime instance identifier.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the exact failed route incarnation for route-scoped authority.
        /// </summary>
        public string? RouteId { get; init; }

        /// <summary>
        /// Gets when the inventory was enumerated.
        /// </summary>
        public DateTimeOffset EnumeratedAtUtc { get; init; }

        /// <summary>
        /// Gets the deterministic exact-runtime candidates.
        /// </summary>
        public IReadOnlyList<AiRuntimePoolAssignedWorkCandidate> Candidates
        {
            get;
            init;
        } = Array.Empty<AiRuntimePoolAssignedWorkCandidate>();
    }
}
