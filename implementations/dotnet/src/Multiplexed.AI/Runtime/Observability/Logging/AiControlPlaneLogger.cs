using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;

namespace Multiplexed.AI.Runtime.Observability.Logging
{
    /// <summary>
    /// Structured logger for AI control-plane events.
    ///
    /// These logs are designed to be exportable to Kibana, OpenSearch,
    /// Elasticsearch, Grafana Loki, or OpenTelemetry logging pipelines.
    /// </summary>
    public sealed class AiControlPlaneLogger : IAiControlPlaneLogger
    {
        private readonly ILogger<AiControlPlaneLogger> _logger;

        public AiControlPlaneLogger(
            ILogger<AiControlPlaneLogger> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public void LogControlPlaneEvent(
            AiControlPlaneEvent controlPlaneEvent)
        {
            ArgumentNullException.ThrowIfNull(controlPlaneEvent);

            var logLevel = GetLogLevel(controlPlaneEvent);

            _logger.Log(
                logLevel,
                "AI control-plane event {EventType} semanticEventType={SemanticEventType} eventId={EventId} causationId={CausationId} area={Area} operation={Operation} outcome={Outcome} executionId={ExecutionId} runId={RunId} correlationId={CorrelationId} runtimeInstanceId={RuntimeInstanceId} workerId={WorkerId} durationMs={DurationMs} failureReason={FailureReason} message={Message} properties={Properties}",
                controlPlaneEvent.EventType,
                controlPlaneEvent.SemanticEventType,
                controlPlaneEvent.EventId,
                controlPlaneEvent.CausationId,
                controlPlaneEvent.Area.ToStableName(),
                controlPlaneEvent.Operation,
                controlPlaneEvent.Outcome,
                controlPlaneEvent.Correlation.ExecutionId,
                controlPlaneEvent.Correlation.RunId,
                controlPlaneEvent.Correlation.CorrelationId,
                controlPlaneEvent.Correlation.RuntimeInstanceId,
                controlPlaneEvent.Correlation.WorkerId,
                controlPlaneEvent.DurationMs,
                controlPlaneEvent.FailureReason,
                controlPlaneEvent.Message,
                controlPlaneEvent.Properties);
        }

        private static LogLevel GetLogLevel(
            AiControlPlaneEvent controlPlaneEvent)
        {
            return controlPlaneEvent.EventType switch
            {
                AiControlPlaneEventType.OperationFailed => LogLevel.Error,
                AiControlPlaneEventType.OperationDenied => LogLevel.Warning,
                AiControlPlaneEventType.OperationDiagnostic => LogLevel.Warning,
                AiControlPlaneEventType.OperationCompleted => LogLevel.Information,
                AiControlPlaneEventType.OperationStarted => LogLevel.Information,
                AiControlPlaneEventType.OperationRequested => LogLevel.Information,
                _ => LogLevel.Information
            };
        }
    }
}