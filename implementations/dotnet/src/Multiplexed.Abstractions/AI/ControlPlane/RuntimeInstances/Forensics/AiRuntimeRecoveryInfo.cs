using System;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Describes the recovery lifecycle for one affected execution.
    /// </summary>
    public sealed record AiRuntimeRecoveryInfo
    {
        /// <summary>
        /// Gets the recovery mode.
        /// </summary>
        public string? RecoveryMode { get; init; }

        /// <summary>
        /// Gets the recovery kind.
        /// </summary>
        public string? RecoveryKind { get; init; }

        /// <summary>
        /// Gets the recovery outcome.
        /// </summary>
        public string? Outcome { get; init; }

        /// <summary>
        /// Gets the reason associated with the recovery transition.
        /// </summary>
        public string? Reason { get; init; }

        /// <summary>
        /// Gets the UTC timestamp when recovery started.
        /// </summary>
        public DateTimeOffset? RecoveryStartedAtUtc { get; init; }

        /// <summary>
        /// Gets the UTC timestamp when recovery completed.
        /// </summary>
        public DateTimeOffset? RecoveryCompletedAtUtc { get; init; }
    }
}