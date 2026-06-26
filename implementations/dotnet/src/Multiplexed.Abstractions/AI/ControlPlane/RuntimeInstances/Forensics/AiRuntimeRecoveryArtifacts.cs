using System.Collections.Generic;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Describes what was restored, recreated and intentionally not restored during recovery.
    /// </summary>
    public sealed record AiRuntimeRecoveryArtifacts
    {
        /// <summary>
        /// Gets artifacts restored from durable truth.
        /// </summary>
        public IReadOnlyList<string> Restored { get; init; } = [];

        /// <summary>
        /// Gets artifacts recreated because the previous runtime state was volatile.
        /// </summary>
        public IReadOnlyList<string> Recreated { get; init; } = [];

        /// <summary>
        /// Gets volatile artifacts intentionally not restored.
        /// </summary>
        public IReadOnlyList<string> LostVolatile { get; init; } = [];
    }
}