using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.Observability
{
    /// <summary>
    /// Records structured control-plane events through the central runtime observability facade.
    /// </summary>
    public sealed class RuntimeObservabilityAiControlPlaneEventSink : IAiControlPlaneEventSink
    {
        private readonly IAiRuntimeObservability observability;

        /// <summary>
        /// Initializes a new instance of the <see cref="RuntimeObservabilityAiControlPlaneEventSink"/> class.
        /// </summary>
        /// <param name="observability">The central runtime observability facade.</param>
        public RuntimeObservabilityAiControlPlaneEventSink(IAiRuntimeObservability observability)
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
                CorrelationId = controlPlaneEvent.Correlation?.CorrelationId,
                RuntimeInstanceId = controlPlaneEvent.Correlation?.RuntimeInstanceId,
                WorkerId = controlPlaneEvent.Correlation?.WorkerId,
                RunId = controlPlaneEvent.Correlation?.RunId
            };

            try
            {
                await this.observability.Ledger.RecordAsync(
                    context,
                    AiDecisionLedgerCategory.Execution,
                    GetEventType(controlPlaneEvent),
                    GetLedgerOutcome(controlPlaneEvent),
                    controlPlaneEvent.FailureReason,
                    metadata,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Control-plane observability must not break control-plane execution.
                // Ledger strict/failure behavior can be hardened later behind explicit options.
            }
        }

        private static IReadOnlyDictionary<string, string?> BuildMetadata(
            AiControlPlaneEvent controlPlaneEvent)
        {
            var metadata = new Dictionary<string, string?>
            {
                ["area"] = controlPlaneEvent.Area.ToString(),
                ["operation"] = controlPlaneEvent.Operation,
                ["outcome"] = controlPlaneEvent.Outcome.ToString(),
                ["durationMs"] = controlPlaneEvent.DurationMs?.ToString(),
                ["failureReason"] = controlPlaneEvent.FailureReason,
                ["correlationId"] = controlPlaneEvent.Correlation?.CorrelationId,
                ["runId"] = controlPlaneEvent.Correlation?.RunId,
                ["executionId"] = controlPlaneEvent.Correlation?.ExecutionId,
                ["pipelineName"] = controlPlaneEvent.Correlation?.PipelineName,
                ["pipelineVersion"] = controlPlaneEvent.Correlation?.PipelineVersion,
                ["pipelineKey"] = controlPlaneEvent.Correlation?.PipelineKey,
                ["runtimeInstanceId"] = controlPlaneEvent.Correlation?.RuntimeInstanceId,
                ["workerId"] = controlPlaneEvent.Correlation?.WorkerId
            };

            if (controlPlaneEvent.Properties is not null)
            {
                foreach (var property in controlPlaneEvent.Properties)
                {
                    metadata[property.Key] = property.Value?.ToString();
                }
            }

            return metadata;
        }

        private static string GetRequiredExecutionId(
            AiControlPlaneEvent controlPlaneEvent)
        {
            if (!string.IsNullOrWhiteSpace(controlPlaneEvent.Correlation?.ExecutionId))
            {
                return controlPlaneEvent.Correlation.ExecutionId;
            }

            if (!string.IsNullOrWhiteSpace(controlPlaneEvent.Correlation?.RunId))
            {
                return "control-plane-run:" + controlPlaneEvent.Correlation.RunId;
            }

            if (!string.IsNullOrWhiteSpace(controlPlaneEvent.Correlation?.RuntimeInstanceId))
            {
                return "control-plane-runtime-instance:" + controlPlaneEvent.Correlation.RuntimeInstanceId;
            }

            return "control-plane-event:" + Guid.NewGuid().ToString("N");
        }

        private static string GetEventType(
            AiControlPlaneEvent controlPlaneEvent)
        {
            return "control." +
                controlPlaneEvent.Area.ToString().ToLowerInvariant() +
                "." +
                controlPlaneEvent.Operation.ToLowerInvariant() +
                "." +
                controlPlaneEvent.Outcome.ToString().ToLowerInvariant();
        }

        private static AiDecisionLedgerOutcome GetLedgerOutcome(
            AiControlPlaneEvent controlPlaneEvent)
        {
            return controlPlaneEvent.Outcome.ToString() switch
            {
                "Started" => AiDecisionLedgerOutcome.Started,
                "Completed" => AiDecisionLedgerOutcome.Completed,
                "Succeeded" => AiDecisionLedgerOutcome.Succeeded,
                "Failed" => AiDecisionLedgerOutcome.Failed,
                "Denied" => AiDecisionLedgerOutcome.Denied,
                "Cancelled" => AiDecisionLedgerOutcome.Cancelled,
                "Expired" => AiDecisionLedgerOutcome.Expired,
                _ => AiDecisionLedgerOutcome.None
            };
        }
    }
}