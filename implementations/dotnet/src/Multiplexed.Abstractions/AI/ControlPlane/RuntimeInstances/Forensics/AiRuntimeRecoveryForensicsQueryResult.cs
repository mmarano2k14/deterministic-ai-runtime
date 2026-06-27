using System.Collections.Generic;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Represents a runtime recovery forensics query result.
    /// </summary>
    public sealed record AiRuntimeRecoveryForensicsQueryResult
    {
        /// <summary>
        /// Gets the returned records.
        /// </summary>
        public IReadOnlyList<AiRuntimeRecoveryForensicsReadModel> Items { get; init; } = [];

        /// <summary>
        /// Gets the number of returned records.
        /// </summary>
        public int Count => Items.Count;

        /// <summary>
        /// Gets the limit applied to the query.
        /// </summary>
        public int Limit { get; init; }
    }
}
