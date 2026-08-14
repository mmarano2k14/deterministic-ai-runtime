using System;
using System.Collections.Generic;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork
{
    /// <summary>
    /// Represents one durable work item assigned to the exact failed runtime instance.
    /// </summary>
    public sealed record AiRuntimePoolAssignedWorkCandidate
    {
        /// <summary>
        /// Gets the failure observation that authorized enumeration.
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
        /// Gets the exact route incarnation for route-scoped failure authority.
        /// </summary>
        public string? RouteId { get; init; }

        /// <summary>
        /// Gets the local runtime queue run identifier.
        /// </summary>
        public required string LocalRunId { get; init; }

        /// <summary>
        /// Gets the durable DAG execution identifier, when already created.
        /// </summary>
        public string? ExecutionId { get; init; }

        /// <summary>
        /// Gets the indexed runtime-run status.
        /// </summary>
        public string? Status { get; init; }

        /// <summary>
        /// Gets the durable tenant isolation identifier.
        /// </summary>
        public required string TenantId { get; init; }

        /// <summary>
        /// Gets the durable tenant-group identifier.
        /// </summary>
        public required string TenantGroupId { get; init; }

        /// <summary>
        /// Gets the shared run identifier projected from existing index metadata, when present.
        /// </summary>
        public string? SharedRunId { get; init; }

        /// <summary>
        /// Gets the deterministic recovery priority category.
        /// </summary>
        public AiRuntimePoolAssignedWorkKind Kind { get; init; }

        /// <summary>
        /// Gets when the runtime-run index entry was created.
        /// </summary>
        public DateTimeOffset CreatedAtUtc { get; init; }

        /// <summary>
        /// Gets the existing durable index metadata.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();
    }
}
