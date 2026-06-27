using System;
using System.Collections.Generic;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Represents one ordered item in a runtime recovery forensics timeline.
    /// </summary>
    public sealed record AiRuntimeRecoveryForensicsTimelineItem
    {
        /// <summary>
        /// Gets the event identifier.
        /// </summary>
        public required string EventId { get; init; }

        /// <summary>
        /// Gets the forensics identifier.
        /// </summary>
        public required string ForensicsId { get; init; }

        /// <summary>
        /// Gets the event timestamp.
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
        /// Gets the execution identifier associated with this event.
        /// </summary>
        public string? ExecutionId { get; init; }

        /// <summary>
        /// Gets the shared run identifier associated with this event.
        /// </summary>
        public string? SharedRunId { get; init; }

        /// <summary>
        /// Gets the local runtime run identifier associated with this event.
        /// </summary>
        public string? LocalRunId { get; init; }

        /// <summary>
        /// Gets the runtime instance identifier associated with this event.
        /// </summary>
        public string? RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the event metadata.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    }
}
