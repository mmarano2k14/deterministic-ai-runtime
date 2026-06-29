using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;

namespace Multiplexed.AI.Runtime.ControlPlane.Observability
{
    /// <summary>
    /// Records structured control-plane events through the central runtime observability facade.
    /// </summary>
    /// <remarks>
    /// This sink forwards control-plane events to the runtime decision ledger through
    /// <see cref="IAiRuntimeObservability"/>.
    ///
    /// It is intentionally best-effort so ledger failures do not break control-plane execution.
    /// </remarks>
    public sealed class RuntimeObservabilityAiControlPlaneEventSink : IAiControlPlaneEventSink
    {
        private readonly IAiRuntimeObservability observability;

        /// <summary>
        /// Initializes a new instance of the <see cref="RuntimeObservabilityAiControlPlaneEventSink"/> class.
        /// </summary>
        /// <param name="observability">The central runtime observability facade.</param>
        public RuntimeObservabilityAiControlPlaneEventSink(
            IAiRuntimeObservability observability)
        {
            this.observability = observability ?? throw new ArgumentNullException(nameof(observability));
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
                Operation = controlPlaneEvent.Operation,
                CorrelationId = controlPlaneEvent.Correlation.CorrelationId,
                RuntimeInstanceId = controlPlaneEvent.Correlation.RuntimeInstanceId,
                WorkerId = controlPlaneEvent.Correlation.WorkerId,
                RunId = controlPlaneEvent.Correlation.RunId
            };

            try
            {
                await this.observability.Ledger
                    .RecordAsync(
                        context,
                        GetLedgerCategory(controlPlaneEvent),
                        GetEventType(controlPlaneEvent),
                        GetLedgerOutcome(controlPlaneEvent),
                        controlPlaneEvent.FailureReason,
                        metadata,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Control-plane observability must not break control-plane execution.
                // Ledger strict/failure behavior can be hardened later behind explicit options.
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
                ["event.type"] = controlPlaneEvent.EventType.ToString(),
                ["eventType"] = controlPlaneEvent.EventType.ToString(),

                ["area"] = controlPlaneEvent.Area.ToString(),
                ["operation"] = controlPlaneEvent.Operation,
                ["outcome"] = controlPlaneEvent.Outcome?.ToString(),
                ["duration.ms"] = controlPlaneEvent.DurationMs?.ToString(),
                ["durationMs"] = controlPlaneEvent.DurationMs?.ToString(),
                ["failure.reason"] = controlPlaneEvent.FailureReason,
                ["failureReason"] = controlPlaneEvent.FailureReason,

                ["correlation.id"] = controlPlaneEvent.Correlation.CorrelationId,
                ["correlationId"] = controlPlaneEvent.Correlation.CorrelationId,

                ["run.id"] = controlPlaneEvent.Correlation.RunId,
                ["runId"] = controlPlaneEvent.Correlation.RunId,

                ["execution.id"] = controlPlaneEvent.Correlation.ExecutionId,
                ["executionId"] = controlPlaneEvent.Correlation.ExecutionId,

                ["pipeline.name"] = controlPlaneEvent.Correlation.PipelineName,
                ["pipelineName"] = controlPlaneEvent.Correlation.PipelineName,

                ["pipeline.version"] = controlPlaneEvent.Correlation.PipelineVersion,
                ["pipelineVersion"] = controlPlaneEvent.Correlation.PipelineVersion,

                ["pipeline.key"] = controlPlaneEvent.Correlation.PipelineKey,
                ["pipelineKey"] = controlPlaneEvent.Correlation.PipelineKey,

                ["runtime.instance.id"] = controlPlaneEvent.Correlation.RuntimeInstanceId,
                ["runtimeInstanceId"] = controlPlaneEvent.Correlation.RuntimeInstanceId,

                ["worker.id"] = controlPlaneEvent.Correlation.WorkerId,
                ["workerId"] = controlPlaneEvent.Correlation.WorkerId
            };

            foreach (var property in controlPlaneEvent.Properties)
            {
                metadata[property.Key] = property.Value?.ToString();
                metadata["property." + property.Key] = property.Value?.ToString();
            }

            TryAddMetadataFromProperty(metadata, controlPlaneEvent, "tenantId", "tenant.id");
            TryAddMetadataFromProperty(metadata, controlPlaneEvent, "tenant.id", "tenant.id");
            TryAddMetadataFromProperty(metadata, controlPlaneEvent, "controlPlaneId", "control.plane.id");
            TryAddMetadataFromProperty(metadata, controlPlaneEvent, "control.plane.id", "control.plane.id");
            TryAddMetadataFromProperty(metadata, controlPlaneEvent, "runtimeInstanceId", "runtime.instance.id");
            TryAddMetadataFromProperty(metadata, controlPlaneEvent, "runtime.instance.id", "runtime.instance.id");
            TryAddMetadataFromProperty(metadata, controlPlaneEvent, "workerId", "worker.id");
            TryAddMetadataFromProperty(metadata, controlPlaneEvent, "worker.id", "worker.id");
            TryAddMetadataFromProperty(metadata, controlPlaneEvent, "pipelineKey", "pipeline.key");
            TryAddMetadataFromProperty(metadata, controlPlaneEvent, "pipeline.key", "pipeline.key");

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

            return "control-plane-event:" + Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// Builds the ledger event type for a structured control-plane event.
        /// </summary>
        /// <param name="controlPlaneEvent">The control-plane event.</param>
        /// <returns>The ledger event type.</returns>
        private static string GetEventType(
            AiControlPlaneEvent controlPlaneEvent)
        {
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
                _ => AiDecisionLedgerCategory.Control
            };
        }
    }
}