using System;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Describes the replacement runtime state recreated during recovery.
    /// </summary>
    public sealed record AiRuntimeRecoveryReplacementInfo
    {
        /// <summary>
        /// Gets the replacement runtime instance identifier.
        /// </summary>
        public string? ReplacementRuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the replacement local runtime run identifier.
        /// </summary>
        public string? ReplacementLocalRunId { get; init; }

        /// <summary>
        /// Gets the reason why the replacement runtime was selected.
        /// </summary>
        public string? DispatchReason { get; init; }

        /// <summary>
        /// Gets the UTC timestamp when the replacement runtime was selected.
        /// </summary>
        public DateTimeOffset? SelectedAtUtc { get; init; }

        /// <summary>
        /// Gets the UTC timestamp when the replacement local run was registered.
        /// </summary>
        public DateTimeOffset? LocalRunRegisteredAtUtc { get; init; }
    }
}