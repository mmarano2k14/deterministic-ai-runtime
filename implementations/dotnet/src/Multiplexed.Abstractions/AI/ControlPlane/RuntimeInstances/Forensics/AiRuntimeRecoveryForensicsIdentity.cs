using System;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Identifies one runtime recovery forensics record across execution, shared run,
    /// tenant and control-plane boundaries.
    /// </summary>
    public sealed record AiRuntimeRecoveryForensicsIdentity
    {
        /// <summary>
        /// Gets the optional storage document identifier.
        /// </summary>
        public string? Id { get; init; }

        /// <summary>
        /// Gets the stable forensics identifier for one recovery attempt.
        /// </summary>
        public required string ForensicsId { get; init; }

        /// <summary>
        /// Gets the durable execution identifier preserved across recovery.
        /// </summary>
        public required string ExecutionId { get; init; }

        /// <summary>
        /// Gets the shared run identifier associated with the recovered execution.
        /// </summary>
        public string? SharedRunId { get; init; }

        /// <summary>
        /// Gets the pipeline name or pipeline key associated with the recovered execution.
        /// </summary>
        public string? PipelineName { get; init; }

        /// <summary>
        /// Gets the tenant identifier associated with the recovery.
        /// </summary>
        public string? TenantId { get; init; }

        /// <summary>
        /// Gets the tenant group identifier associated with the recovery.
        /// </summary>
        public string? TenantGroupId { get; init; }

        /// <summary>
        /// Gets the control-plane identifier that observed or coordinated the recovery.
        /// </summary>
        public string? ControlPlaneId { get; init; }
    }
}