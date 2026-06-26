using System;
using System.Collections.Generic;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Represents the persisted recovery proof for one execution affected by a runtime instance failure.
    /// </summary>
    public sealed record AiRuntimeRecoveryForensicsRecord
    {
        /// <summary>
        /// Gets the recovery forensics identity.
        /// </summary>
        public required AiRuntimeRecoveryForensicsIdentity Identity { get; init; }

        /// <summary>
        /// Gets the runtime failure information.
        /// </summary>
        public AiRuntimeRecoveryFailureInfo? Failure { get; init; }

        /// <summary>
        /// Gets the recovery lifecycle information.
        /// </summary>
        public AiRuntimeRecoveryInfo? Recovery { get; init; }

        /// <summary>
        /// Gets the replacement runtime information.
        /// </summary>
        public AiRuntimeRecoveryReplacementInfo? Replacement { get; init; }

        /// <summary>
        /// Gets the execution context recovery information.
        /// </summary>
        public AiRuntimeRecoveryContextInfo? Context { get; init; }

        /// <summary>
        /// Gets the DAG recovery information.
        /// </summary>
        public AiRuntimeRecoveryDagInfo? Dag { get; init; }

        /// <summary>
        /// Gets the restored, recreated and intentionally lost artifacts.
        /// </summary>
        public AiRuntimeRecoveryArtifacts Artifacts { get; init; } = new();

        /// <summary>
        /// Gets the append-only recovery event timeline.
        /// </summary>
        public IReadOnlyList<AiRuntimeRecoveryForensicsEvent> Events { get; init; } = [];

        /// <summary>
        /// Gets additional metadata for diagnostics, reporting and future ledger/trace correlation.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets the UTC timestamp when the record was created.
        /// </summary>
        public DateTimeOffset CreatedAtUtc { get; init; }

        /// <summary>
        /// Gets the UTC timestamp when the record was last updated.
        /// </summary>
        public DateTimeOffset UpdatedAtUtc { get; init; }
    }
}