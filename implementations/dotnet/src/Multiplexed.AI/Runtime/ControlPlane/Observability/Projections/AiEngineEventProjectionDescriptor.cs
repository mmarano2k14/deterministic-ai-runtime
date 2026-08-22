using System;

namespace Multiplexed.AI.Runtime.ControlPlane.Observability
{
    /// <summary>
    /// Describes the centralized projection and durability contract for one canonical engine event.
    /// </summary>
    public sealed class AiEngineEventProjectionDescriptor
    {
        /// <summary>
        /// Gets the canonical semantic event type.
        /// </summary>
        public required string EventType { get; init; }

        /// <summary>
        /// Gets the durability class of the semantic event.
        /// </summary>
        public required AiEngineEventDurability Durability { get; init; }

        /// <summary>
        /// Gets the Decision Ledger projection requirement.
        /// </summary>
        public AiEngineEventProjectionRequirement Ledger { get; init; }

        /// <summary>
        /// Gets the Recovery Forensics projection requirement.
        /// </summary>
        public AiEngineEventProjectionRequirement RecoveryForensics { get; init; }

        /// <summary>
        /// Gets the Execution Forensics projection requirement.
        /// </summary>
        public AiEngineEventProjectionRequirement ExecutionForensics { get; init; }

        /// <summary>
        /// Gets the Runtime Lifecycle Journal projection requirement.
        /// </summary>
        public AiEngineEventProjectionRequirement LifecycleJournal { get; init; }

        /// <summary>
        /// Gets the Metrics projection requirement.
        /// </summary>
        public AiEngineEventProjectionRequirement Metrics { get; init; }

        /// <summary>
        /// Gets the structured Logging projection requirement.
        /// </summary>
        public AiEngineEventProjectionRequirement Logging { get; init; }

        /// <summary>
        /// Gets the Realtime observation projection requirement.
        /// </summary>
        public AiEngineEventProjectionRequirement Realtime { get; init; }

        /// <summary>
        /// Gets the requirement for the specified projection target.
        /// </summary>
        /// <param name="target">The projection target.</param>
        /// <returns>The configured projection requirement.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="target"/> is not a known projection target.
        /// </exception>
        public AiEngineEventProjectionRequirement GetRequirement(
            AiEngineEventProjectionTarget target)
        {
            return target switch
            {
                AiEngineEventProjectionTarget.Ledger => this.Ledger,
                AiEngineEventProjectionTarget.RecoveryForensics => this.RecoveryForensics,
                AiEngineEventProjectionTarget.ExecutionForensics => this.ExecutionForensics,
                AiEngineEventProjectionTarget.LifecycleJournal => this.LifecycleJournal,
                AiEngineEventProjectionTarget.Metrics => this.Metrics,
                AiEngineEventProjectionTarget.Logging => this.Logging,
                AiEngineEventProjectionTarget.Realtime => this.Realtime,
                _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown engine event projection target.")
            };
        }
    }
}
