using System;
using System.Collections.Generic;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Observability
{
    /// <summary>
    /// Creates canonical engine events from authoritative Child DAG relation transitions.
    /// </summary>
    internal static class AiChildDagEngineEventFactory
    {
        /// <summary>
        /// Creates a canonical child execution lifecycle event when an existing runtime request carries
        /// the deterministic Child DAG metadata produced by the child dispatcher.
        /// </summary>
        public static AiControlPlaneEvent? TryCreateExecutionLifecycle(
            AiRuntimePipelineRunRequest request,
            string executionId,
            string runtimeInstanceId,
            string semanticEventType,
            DateTimeOffset? timestampUtc = null)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(semanticEventType);

            if (request.Metadata is null ||
                !request.Metadata.TryGetValue(AiChildDagMetadataKeys.InvocationKey, out var invocationKey) ||
                string.IsNullOrWhiteSpace(invocationKey))
            {
                return null;
            }

            request.Metadata.TryGetValue(AiChildDagMetadataKeys.ParentExecutionId, out var parentExecutionId);
            request.Metadata.TryGetValue(AiChildDagMetadataKeys.ParentCallSiteId, out var parentCallSiteId);

            return new AiControlPlaneEvent
            {
                EventId = string.Concat(semanticEventType, ":", executionId),
                SemanticEventType = semanticEventType,
                EventType = AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.ChildDag,
                Operation = semanticEventType,
                Outcome = AiControlPlaneOperationOutcome.Succeeded,
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = invocationKey,
                    ExecutionId = executionId,
                    PipelineKey = request.PipelineName,
                    RuntimeInstanceId = runtimeInstanceId
                },
                CausationId = parentExecutionId,
                TimestampUtc = timestampUtc ?? DateTimeOffset.UtcNow,
                Properties = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [AiChildDagMetadataKeys.InvocationKey] = invocationKey,
                    [AiChildDagMetadataKeys.ExecutionId] = executionId,
                    [AiChildDagMetadataKeys.ParentExecutionId] = parentExecutionId,
                    [AiChildDagMetadataKeys.ParentCallSiteId] = parentCallSiteId
                }
            };
        }

        /// <summary>
        /// Creates one canonical Child DAG event after its durable relation transition has committed.
        /// </summary>
        public static AiControlPlaneEvent Create(
            AiChildExecutionRelation relation,
            string semanticEventType,
            string subjectId,
            string? continuationId = null,
            string? reason = null,
            DateTimeOffset? timestampUtc = null)
        {
            ArgumentNullException.ThrowIfNull(relation);
            ArgumentException.ThrowIfNullOrWhiteSpace(semanticEventType);
            ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);

            var eventId = string.Concat(semanticEventType, ":", subjectId);
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [AiChildDagMetadataKeys.InvocationKey] = relation.ChildInvocationKey,
                [AiChildDagMetadataKeys.InvocationGeneration] = relation.InvocationGeneration,
                [AiChildDagMetadataKeys.ExecutionId] = relation.ChildExecutionId,
                [AiChildDagMetadataKeys.ParentExecutionId] = relation.ParentExecutionId,
                [AiChildDagMetadataKeys.ParentCallSiteId] = relation.ParentCallSiteId,
                ["child.dag.id"] = relation.ChildDagId,
                ["child.continuation.id"] = continuationId,
                ["child.event.reason"] = reason
            };

            return new AiControlPlaneEvent
            {
                EventId = eventId,
                SemanticEventType = semanticEventType,
                EventType = semanticEventType == AiEngineEvents.ChildDag.ExecutionFailed
                    ? AiControlPlaneEventType.OperationFailed
                    : AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.ChildDag,
                Operation = semanticEventType,
                Outcome = semanticEventType == AiEngineEvents.ChildDag.ExecutionFailed
                    ? AiControlPlaneOperationOutcome.Failed
                    : AiControlPlaneOperationOutcome.Succeeded,
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = relation.ChildInvocationKey,
                    ExecutionId = relation.ChildExecutionId ?? relation.ParentExecutionId,
                    PipelineKey = relation.ChildDagId
                },
                CausationId = relation.ParentExecutionId,
                TimestampUtc = timestampUtc ?? DateTimeOffset.UtcNow,
                FailureReason = semanticEventType == AiEngineEvents.ChildDag.ExecutionFailed
                    ? reason
                    : null,
                Properties = properties
            };
        }
    }
}
