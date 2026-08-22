using Multiplexed.Abstractions.AI.Execution;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;

namespace Multiplexed.AI.Runtime.ControlPlane.Observability
{
    /// <summary>
    /// Projects structured control-plane events to the existing Decision Ledger recorder.
    /// </summary>
    /// <remarks>
    /// Generic legacy control-plane events preserve their historical best-effort behavior.
    /// Canonical semantic events surface recorder failures to the Event Manager so the central
    /// projection catalog, rather than the sink itself, owns failure semantics.
    /// </remarks>
    public sealed class RuntimeObservabilityAiControlPlaneEventSink : IAiControlPlaneEventProjectionSink
    {
        private readonly IAiDecisionLedgerRecorder ledger;

        /// <inheritdoc />
        public AiEngineEventProjectionTarget ProjectionTarget => AiEngineEventProjectionTarget.Ledger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RuntimeObservabilityAiControlPlaneEventSink"/> class
        /// from the existing runtime observability facade.
        /// </summary>
        /// <param name="observability">The existing runtime observability facade.</param>
        /// <remarks>
        /// This constructor is preserved for direct callers and existing tests. Dependency injection uses
        /// <see cref="CreateForLedger"/> so the singleton Event Manager does not capture the scoped facade.
        /// </remarks>
        public RuntimeObservabilityAiControlPlaneEventSink(
            IAiRuntimeObservability observability)
            : this((observability ?? throw new ArgumentNullException(nameof(observability))).Ledger)
        {
        }

        /// <summary>
        /// Initializes the sink directly from the existing Decision Ledger recorder.
        /// </summary>
        /// <param name="ledger">The existing Decision Ledger recorder.</param>
        private RuntimeObservabilityAiControlPlaneEventSink(
            IAiDecisionLedgerRecorder ledger)
        {
            this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        }

        /// <summary>
        /// Creates the projection sink directly from the existing singleton-safe Decision Ledger recorder.
        /// </summary>
        /// <param name="ledger">The existing Decision Ledger recorder.</param>
        /// <returns>A Ledger projection sink.</returns>
        internal static RuntimeObservabilityAiControlPlaneEventSink CreateForLedger(
            IAiDecisionLedgerRecorder ledger)
        {
            return new RuntimeObservabilityAiControlPlaneEventSink(ledger);
        }

        /// <inheritdoc />
        public async Task RecordAsync(
            AiControlPlaneEvent controlPlaneEvent,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(controlPlaneEvent);

            var metadata = BuildMetadata(controlPlaneEvent);

            var context = new AiRuntimeLedgerEventCorrelationContext
            {
                ExecutionId = GetRequiredExecutionId(controlPlaneEvent),
                RunId = controlPlaneEvent.Correlation.RunId,
                PipelineName = controlPlaneEvent.Correlation.PipelineName,
                PipelineVersion = controlPlaneEvent.Correlation.PipelineVersion,
                StepId = GetPropertyValue(controlPlaneEvent, AiStepMetadataKeys.StepId)
                    ?? GetPropertyValue(controlPlaneEvent, AiStepMetadataKeys.StepName),
                StepKey = GetPropertyValue(controlPlaneEvent, AiStepMetadataKeys.StepKey)
                    ?? GetPropertyValue(controlPlaneEvent, AiStepMetadataKeys.StepName),
                Operation = controlPlaneEvent.Operation,
                CorrelationId = controlPlaneEvent.Correlation.CorrelationId,
                RuntimeInstanceId = controlPlaneEvent.Correlation.RuntimeInstanceId,
                WorkerId = controlPlaneEvent.Correlation.WorkerId
            };

            try
            {
                await this.ledger
                    .RecordAsync(
                        context,
                        GetLedgerCategory(controlPlaneEvent),
                        GetEventType(controlPlaneEvent),
                        GetLedgerOutcome(controlPlaneEvent),
                        GetLedgerReason(controlPlaneEvent),
                        metadata,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch when (string.IsNullOrWhiteSpace(controlPlaneEvent.SemanticEventType))
            {
                // Preserve the historical best-effort behavior for generic control-plane operation events.
                // Canonical semantic events must surface failures to the Event Manager, which applies the
                // centralized projection requirement declared by AiEngineEventProjectionCatalog.
            }
        }

        /// <summary>
        /// Builds ledger metadata from a structured control-plane event.
        /// </summary>
        /// <param name="controlPlaneEvent">The control-plane event.</param>
        /// <returns>The ledger metadata dictionary.</returns>
        private static IReadOnlyDictionary<string, string?> BuildMetadata(
            AiControlPlaneEvent controlPlaneEvent)
        {
            var metadata = new Dictionary<string, string?>
            {
                ["event.id"] = controlPlaneEvent.EventId,
                ["event.type"] = controlPlaneEvent.EventType.ToString(),
                ["eventType"] = controlPlaneEvent.EventType.ToString(),
                ["event.semanticType"] = controlPlaneEvent.SemanticEventType,
                ["event.causationId"] = controlPlaneEvent.CausationId,

                ["area"] = controlPlaneEvent.Area.ToString(),
                ["operation"] = controlPlaneEvent.Operation,
                ["outcome"] = controlPlaneEvent.Outcome?.ToString(),
                [AiObservabilityMetadataKeys.DottedDurationMs] = controlPlaneEvent.DurationMs?.ToString(),
                [AiObservabilityMetadataKeys.DurationMs] = controlPlaneEvent.DurationMs?.ToString(),
                ["failure.reason"] = controlPlaneEvent.FailureReason,
                [AiObservabilityMetadataKeys.FailureReason] = controlPlaneEvent.FailureReason,

                [AiObservabilityMetadataKeys.CorrelationId] = controlPlaneEvent.Correlation.CorrelationId,
                [AiObservabilityMetadataKeys.CamelCaseCorrelationId] = controlPlaneEvent.Correlation.CorrelationId,

                [AiRunMetadataKeys.RunId] = controlPlaneEvent.Correlation.RunId,
                [AiRunMetadataKeys.CamelCaseRunId] = controlPlaneEvent.Correlation.RunId,

                [AiExecutionMetadataKeys.ExecutionId] = controlPlaneEvent.Correlation.ExecutionId,
                [AiExecutionMetadataKeys.CamelCaseExecutionId] = controlPlaneEvent.Correlation.ExecutionId,

                [AiPipelineMetadataKeys.Name] = controlPlaneEvent.Correlation.PipelineName,
                [AiPipelineMetadataKeys.CamelCasePipelineName] = controlPlaneEvent.Correlation.PipelineName,

                [AiPipelineMetadataKeys.Version] = controlPlaneEvent.Correlation.PipelineVersion,
                [AiPipelineMetadataKeys.CamelCasePipelineVersion] = controlPlaneEvent.Correlation.PipelineVersion,

                [AiPipelineMetadataKeys.Key] = controlPlaneEvent.Correlation.PipelineKey,
                [AiPipelineMetadataKeys.CamelCasePipelineKey] = controlPlaneEvent.Correlation.PipelineKey,

                [AiRuntimeInstanceMetadataKeys.RuntimeInstanceId] = controlPlaneEvent.Correlation.RuntimeInstanceId,
                [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = controlPlaneEvent.Correlation.RuntimeInstanceId,

                [AiWorkerMetadataKeys.WorkerId] = controlPlaneEvent.Correlation.WorkerId,
                [AiWorkerMetadataKeys.CamelCaseWorkerId] = controlPlaneEvent.Correlation.WorkerId
            };

            foreach (var property in controlPlaneEvent.Properties)
            {
                metadata[property.Key] = property.Value?.ToString();
                metadata["property." + property.Key] = property.Value?.ToString();
            }

            TryAddMetadataFromProperty(metadata, controlPlaneEvent, AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantId, AiRuntimeInstanceIsolationMetadataKeys.TenantId);
            TryAddMetadataFromProperty(metadata, controlPlaneEvent, AiRuntimeInstanceIsolationMetadataKeys.TenantId, AiRuntimeInstanceIsolationMetadataKeys.TenantId);
            TryAddMetadataFromProperty(metadata, controlPlaneEvent, AiControlPlaneMetadataKeys.ControlPlaneId, AiControlPlaneMetadataKeys.LegacyDottedControlPlaneId);
            TryAddMetadataFromProperty(metadata, controlPlaneEvent, AiControlPlaneMetadataKeys.LegacyDottedControlPlaneId, AiControlPlaneMetadataKeys.LegacyDottedControlPlaneId);
            TryAddMetadataFromProperty(metadata, controlPlaneEvent, AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId, AiRuntimeInstanceMetadataKeys.RuntimeInstanceId);
            TryAddMetadataFromProperty(metadata, controlPlaneEvent, AiRuntimeInstanceMetadataKeys.RuntimeInstanceId, AiRuntimeInstanceMetadataKeys.RuntimeInstanceId);
            TryAddMetadataFromProperty(metadata, controlPlaneEvent, AiWorkerMetadataKeys.CamelCaseWorkerId, AiWorkerMetadataKeys.WorkerId);
            TryAddMetadataFromProperty(metadata, controlPlaneEvent, AiWorkerMetadataKeys.WorkerId, AiWorkerMetadataKeys.WorkerId);
            TryAddMetadataFromProperty(metadata, controlPlaneEvent, AiPipelineMetadataKeys.CamelCasePipelineKey, AiPipelineMetadataKeys.Key);
            TryAddMetadataFromProperty(metadata, controlPlaneEvent, AiPipelineMetadataKeys.Key, AiPipelineMetadataKeys.Key);

            return metadata;
        }

        /// <summary>
        /// Adds a normalized ledger metadata value from a control-plane event property when available.
        /// </summary>
        /// <param name="metadata">The metadata dictionary to enrich.</param>
        /// <param name="controlPlaneEvent">The control-plane event.</param>
        /// <param name="propertyKey">The source property key.</param>
        /// <param name="metadataKey">The target metadata key.</param>
        private static void TryAddMetadataFromProperty(
            IDictionary<string, string?> metadata,
            AiControlPlaneEvent controlPlaneEvent,
            string propertyKey,
            string metadataKey)
        {
            if (!controlPlaneEvent.Properties.TryGetValue(propertyKey, out var value) ||
                value is null)
            {
                return;
            }

            var text = value.ToString();

            if (!string.IsNullOrWhiteSpace(text))
            {
                metadata[metadataKey] = text;
            }
        }

        /// <summary>
        /// Gets the execution identifier required by the decision ledger.
        /// </summary>
        /// <param name="controlPlaneEvent">The control-plane event.</param>
        /// <returns>
        /// The durable execution identifier when available, otherwise a synthetic control-plane identifier.
        /// </returns>
        private static string GetRequiredExecutionId(
            AiControlPlaneEvent controlPlaneEvent)
        {
            if (!string.IsNullOrWhiteSpace(controlPlaneEvent.Correlation.ExecutionId))
            {
                return controlPlaneEvent.Correlation.ExecutionId;
            }

            if (!string.IsNullOrWhiteSpace(controlPlaneEvent.Correlation.RunId))
            {
                return "control-plane-run:" + controlPlaneEvent.Correlation.RunId;
            }

            if (!string.IsNullOrWhiteSpace(controlPlaneEvent.Correlation.RuntimeInstanceId))
            {
                return "control-plane-runtime-instance:" + controlPlaneEvent.Correlation.RuntimeInstanceId;
            }

            return "control-plane-event:" + controlPlaneEvent.EventId;
        }

        /// <summary>
        /// Gets a structured event property as text when present.
        /// </summary>
        private static string? GetPropertyValue(
            AiControlPlaneEvent controlPlaneEvent,
            string key)
        {
            return controlPlaneEvent.Properties.TryGetValue(key, out var value)
                ? value?.ToString()
                : null;
        }

        /// <summary>
        /// Resolves the Decision Ledger reason without overloading canonical event failure semantics.
        /// </summary>
        private static string? GetLedgerReason(AiControlPlaneEvent controlPlaneEvent)
        {
            return !string.IsNullOrWhiteSpace(controlPlaneEvent.SemanticEventType)
                ? controlPlaneEvent.Message ?? controlPlaneEvent.FailureReason
                : controlPlaneEvent.FailureReason;
        }

        /// <summary>
        /// Builds the ledger event type for a structured control-plane event.
        /// </summary>
        /// <param name="controlPlaneEvent">The control-plane event.</param>
        /// <returns>The ledger event type.</returns>
        private static string GetEventType(
            AiControlPlaneEvent controlPlaneEvent)
        {
            if (!string.IsNullOrWhiteSpace(controlPlaneEvent.SemanticEventType))
            {
                return controlPlaneEvent.SemanticEventType;
            }

            var suffix =
                controlPlaneEvent.Outcome is null
                    ? controlPlaneEvent.EventType.ToString().ToLowerInvariant()
                    : controlPlaneEvent.Outcome.ToString()!.ToLowerInvariant();

            return "control." +
                controlPlaneEvent.Area.ToString().ToLowerInvariant() +
                "." +
                controlPlaneEvent.Operation.ToLowerInvariant() +
                "." +
                suffix;
        }

        /// <summary>
        /// Maps the control-plane operation outcome to a decision ledger outcome.
        /// </summary>
        /// <param name="controlPlaneEvent">The control-plane event.</param>
        /// <returns>The matching decision ledger outcome.</returns>
        private static AiDecisionLedgerOutcome GetLedgerOutcome(
            AiControlPlaneEvent controlPlaneEvent)
        {
            if (!string.IsNullOrWhiteSpace(controlPlaneEvent.SemanticEventType))
            {
                var semanticOutcome = ResolveSemanticLedgerOutcome(controlPlaneEvent.SemanticEventType);
                if (semanticOutcome is not null)
                {
                    return semanticOutcome.Value;
                }
            }

            if (controlPlaneEvent.EventType == AiControlPlaneEventType.OperationStarted)
            {
                return AiDecisionLedgerOutcome.Started;
            }

            return controlPlaneEvent.Outcome?.ToString() switch
            {
                "Succeeded" => AiDecisionLedgerOutcome.Succeeded,
                "Denied" => AiDecisionLedgerOutcome.Denied,
                "Failed" => AiDecisionLedgerOutcome.Failed,
                "CompletedWithIssues" => AiDecisionLedgerOutcome.CompletedWithIssues,
                _ => AiDecisionLedgerOutcome.None
            };
        }

        /// <summary>
        /// Resolves canonical semantic outcomes that are more precise than the generic control-plane envelope.
        /// </summary>
        /// <param name="semanticEventType">The canonical semantic event type.</param>
        /// <returns>The exact existing Ledger outcome when the semantic family requires one; otherwise <see langword="null" />.</returns>
        private static AiDecisionLedgerOutcome? ResolveSemanticLedgerOutcome(
            string semanticEventType)
        {
            if (string.Equals(semanticEventType, AiEngineEvents.Policy.Evaluated, StringComparison.Ordinal))
            {
                return AiDecisionLedgerOutcome.Started;
            }

            if (string.Equals(semanticEventType, AiEngineEvents.Policy.Allowed, StringComparison.Ordinal))
            {
                return AiDecisionLedgerOutcome.Allowed;
            }

            if (string.Equals(semanticEventType, AiEngineEvents.Policy.Denied, StringComparison.Ordinal))
            {
                return AiDecisionLedgerOutcome.Denied;
            }

            if (string.Equals(semanticEventType, AiEngineEvents.Policy.Skipped, StringComparison.Ordinal))
            {
                return AiDecisionLedgerOutcome.Skipped;
            }

            if (string.Equals(semanticEventType, AiEngineEvents.Policy.Failed, StringComparison.Ordinal))
            {
                return AiDecisionLedgerOutcome.Failed;
            }

            return null;
        }

        /// <summary>
        /// Maps the control-plane area to a decision ledger category.
        /// </summary>
        /// <param name="controlPlaneEvent">The control-plane event.</param>
        /// <returns>The matching decision ledger category.</returns>
        private static AiDecisionLedgerCategory GetLedgerCategory(
            AiControlPlaneEvent controlPlaneEvent)
        {
            return controlPlaneEvent.Area switch
            {
                AiControlPlaneArea.Replay => AiDecisionLedgerCategory.Replay,
                AiControlPlaneArea.ExecutionControl => AiDecisionLedgerCategory.Control,
                AiControlPlaneArea.RunControl => AiDecisionLedgerCategory.Run,
                AiControlPlaneArea.InstanceRegistry => AiDecisionLedgerCategory.RuntimeInstance,
                AiControlPlaneArea.Admission => AiDecisionLedgerCategory.Admission,
                AiControlPlaneArea.SharedQueue => AiDecisionLedgerCategory.Queue,
                AiControlPlaneArea.SharedController => AiDecisionLedgerCategory.SharedController,
                AiControlPlaneArea.Scaling => AiDecisionLedgerCategory.Scaling,
                AiControlPlaneArea.Recovery => AiDecisionLedgerCategory.Recovery,
                AiControlPlaneArea.ChildDag => AiDecisionLedgerCategory.Dag,
                AiControlPlaneArea.Policy => AiDecisionLedgerCategory.Policy,
                _ => AiDecisionLedgerCategory.Control
            };
        }
    }
}