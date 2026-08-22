using System;
using System.Collections.Generic;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery.Observability
{
    /// <summary>
    /// Creates canonical control-plane event envelopes for runtime execution recovery facts.
    /// </summary>
    /// <remarks>
    /// Recovery production code owns the semantic fact and its stable identities only.
    /// Projection-specific record models remain behind the Event Manager sinks.
    /// </remarks>
    internal static class AiRecoveryEngineEventFactory
    {
        /// <summary>
        /// Creates one canonical runtime recovery engine event.
        /// </summary>
        /// <param name="semanticEventType">The canonical recovery event type.</param>
        /// <param name="eventId">The stable semantic event identifier.</param>
        /// <param name="forensicsId">The recovery forensics correlation identifier.</param>
        /// <param name="timestampUtc">The UTC timestamp when the fact occurred.</param>
        /// <param name="outcome">The existing recovery outcome value.</param>
        /// <param name="reason">The recovery reason.</param>
        /// <param name="executionId">The durable execution identifier when known.</param>
        /// <param name="sharedRunId">The shared run identifier when known.</param>
        /// <param name="localRunId">The local run identifier when known.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier when known.</param>
        /// <param name="metadata">Existing recovery metadata to preserve for projections.</param>
        /// <param name="causationId">The causing event or durable fact identifier when known.</param>
        /// <returns>The canonical control-plane event envelope.</returns>
        public static AiControlPlaneEvent Create(
            string semanticEventType,
            string eventId,
            string forensicsId,
            DateTimeOffset timestampUtc,
            string? outcome,
            string? reason,
            string? executionId,
            string? sharedRunId,
            string? localRunId,
            string? runtimeInstanceId,
            IReadOnlyDictionary<string, string>? metadata = null,
            string? causationId = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(semanticEventType);
            ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
            ArgumentException.ThrowIfNullOrWhiteSpace(forensicsId);

            var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (metadata is not null)
            {
                foreach (var pair in metadata)
                {
                    properties[pair.Key] = pair.Value;
                }
            }

            properties[AiRuntimeRecoveryMetadataKeys.ProjectionForensicsId] = forensicsId;
            properties[AiRuntimeRecoveryMetadataKeys.ProjectionOutcome] = outcome;
            properties[AiRuntimeRecoveryMetadataKeys.ProjectionReason] = reason;
            properties[AiRuntimeRecoveryMetadataKeys.ProjectionSharedRunId] = sharedRunId;
            properties[AiRuntimeRecoveryMetadataKeys.ProjectionLocalRunId] = localRunId;

            var failed = string.Equals(
                semanticEventType,
                AiEngineEvents.Recovery.ExecutionRecoveryFailed,
                StringComparison.Ordinal);

            return new AiControlPlaneEvent
            {
                EventId = eventId,
                SemanticEventType = semanticEventType,
                EventType = failed
                    ? AiControlPlaneEventType.OperationFailed
                    : AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.Recovery,
                Operation = semanticEventType,
                Outcome = failed
                    ? AiControlPlaneOperationOutcome.Failed
                    : AiControlPlaneOperationOutcome.Succeeded,
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = forensicsId,
                    RunId = sharedRunId,
                    ExecutionId = executionId,
                    RuntimeInstanceId = runtimeInstanceId
                },
                CausationId = causationId,
                TimestampUtc = timestampUtc,
                Message = reason,
                FailureReason = failed ? reason : null,
                Properties = properties
            };
        }
    }
}
