using System;
using System.Collections.Generic;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle
{
    /// <summary>
    /// Adapts the existing durable runtime lifecycle journal model to the canonical
    /// control-plane event envelope consumed by the centralized Event Manager.
    /// </summary>
    public static class AiRuntimeLifecycleEngineEventFactory
    {
        internal const string ProjectionPayloadProperty = "runtime.lifecycle.projection.payload";

        /// <summary>
        /// Creates one canonical engine event for an already-materialized runtime lifecycle fact.
        /// </summary>
        /// <param name="lifecycleEvent">The existing lifecycle journal event.</param>
        /// <returns>The canonical Event Manager envelope.</returns>
        public static AiControlPlaneEvent Create(AiRuntimeLifecycleEvent lifecycleEvent)
        {
            ArgumentNullException.ThrowIfNull(lifecycleEvent);
            ArgumentException.ThrowIfNullOrWhiteSpace(lifecycleEvent.EventId);
            ArgumentException.ThrowIfNullOrWhiteSpace(lifecycleEvent.EventType);

            return new AiControlPlaneEvent
            {
                EventId = lifecycleEvent.EventId,
                SemanticEventType = lifecycleEvent.EventType,
                EventType = string.Equals(
                    lifecycleEvent.EventType,
                    AiRuntimeLifecycleEvents.HostCreationFailed,
                    StringComparison.Ordinal)
                    ? AiControlPlaneEventType.OperationFailed
                    : AiControlPlaneEventType.OperationCompleted,
                Area = ResolveArea(lifecycleEvent.EventType),
                Operation = lifecycleEvent.EventType,
                Outcome = string.Equals(
                    lifecycleEvent.EventType,
                    AiRuntimeLifecycleEvents.HostCreationFailed,
                    StringComparison.Ordinal)
                    ? AiControlPlaneOperationOutcome.Failed
                    : AiControlPlaneOperationOutcome.Succeeded,
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = string.IsNullOrWhiteSpace(lifecycleEvent.CorrelationId)
                        ? lifecycleEvent.EventId
                        : lifecycleEvent.CorrelationId,
                    RunId = lifecycleEvent.SharedRunId,
                    ExecutionId = lifecycleEvent.ExecutionId,
                    RuntimeInstanceId = lifecycleEvent.RuntimeInstanceId
                },
                CausationId = lifecycleEvent.CausationId,
                TimestampUtc = lifecycleEvent.TimestampUtc,
                FailureReason = string.Equals(
                    lifecycleEvent.EventType,
                    AiRuntimeLifecycleEvents.HostCreationFailed,
                    StringComparison.Ordinal)
                    ? lifecycleEvent.Reason
                    : null,
                Properties = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [ProjectionPayloadProperty] = lifecycleEvent
                }
            };
        }

        private static AiControlPlaneArea ResolveArea(string eventType)
        {
            if (eventType.StartsWith("host.", StringComparison.Ordinal) ||
                eventType.StartsWith("runtime.replacement.", StringComparison.Ordinal))
            {
                return AiControlPlaneArea.Scaling;
            }

            if (eventType.StartsWith("work.", StringComparison.Ordinal))
            {
                return AiControlPlaneArea.SharedController;
            }

            return AiControlPlaneArea.InstanceRegistry;
        }
    }
}
