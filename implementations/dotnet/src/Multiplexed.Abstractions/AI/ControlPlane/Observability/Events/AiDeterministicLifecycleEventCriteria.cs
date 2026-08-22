using System;
using System.Collections.Generic;

namespace Multiplexed.Abstractions.AI.ControlPlane.Observability.Events
{
    /// <summary>
    /// Defines the canonical engine-event identity filters used by deterministic lifecycle waits.
    /// </summary>
    /// <remarks>
    /// The semantic event type must come from the canonical engine-event namespace. Optional identity
    /// filters narrow the wait without introducing a second event identity model.
    /// </remarks>
    public sealed class AiDeterministicLifecycleEventCriteria
    {
        /// <summary>
        /// Gets the canonical semantic engine event type to await.
        /// </summary>
        public required string SemanticEventType { get; init; }

        /// <summary>
        /// Gets the optional stable event identifier.
        /// </summary>
        public string? EventId { get; init; }

        /// <summary>
        /// Gets the optional correlation identifier.
        /// </summary>
        public string? CorrelationId { get; init; }

        /// <summary>
        /// Gets the optional durable execution identifier.
        /// </summary>
        public string? ExecutionId { get; init; }

        /// <summary>
        /// Gets the optional controller/shared run identifier carried by the correlation context.
        /// </summary>
        public string? RunId { get; init; }

        /// <summary>
        /// Gets the optional runtime instance identifier.
        /// </summary>
        public string? RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the optional durable shared-run identifier.
        /// </summary>
        public string? SharedRunId { get; init; }

        /// <summary>
        /// Gets the optional recovery-forensics identifier.
        /// </summary>
        public string? ForensicsId { get; init; }

        /// <summary>
        /// Gets optional canonical event properties that must match by ordinal string value.
        /// </summary>
        public IReadOnlyDictionary<string, string> Properties { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
