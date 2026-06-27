using System;
using System.Collections.Generic;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Represents one append-only recovery forensics event.
    /// </summary>
    public sealed record AiRuntimeRecoveryForensicsEvent
    {
        /// <summary>
        /// Gets the event identifier.
        /// </summary>
        public required string EventId { get; init; }

        /// <summary>
        /// Gets the forensics identifier associated with this event.
        /// </summary>
        public required string ForensicsId { get; init; }

        /// <summary>
        /// Gets the UTC timestamp when the event happened.
        /// </summary>
        public DateTimeOffset TimestampUtc { get; init; }

        /// <summary>
        /// Gets the event type.
        /// </summary>
        public required string EventType { get; init; }

        /// <summary>
        /// Gets the event outcome.
        /// </summary>
        public string? Outcome { get; init; }

        /// <summary>
        /// Gets the event reason.
        /// </summary>
        public string? Reason { get; init; }

        /// <summary>
        /// Gets the durable execution identifier when known.
        /// </summary>
        public string? ExecutionId { get; init; }

        /// <summary>
        /// Gets the shared run identifier when known.
        /// </summary>
        public string? SharedRunId { get; init; }

        /// <summary>
        /// Gets the local runtime run identifier when known.
        /// </summary>
        public string? LocalRunId { get; init; }

        /// <summary>
        /// Gets the runtime instance identifier when known.
        /// </summary>
        public string? RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets additional event metadata.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}