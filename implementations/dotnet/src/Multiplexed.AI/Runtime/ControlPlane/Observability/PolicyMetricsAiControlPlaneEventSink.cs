using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Events;
using Multiplexed.Abstractions.AI.Observability.Metrics.Policy;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Policies;

namespace Multiplexed.AI.Runtime.ControlPlane.Observability
{
    /// <summary>
    /// Projects canonical policy decision events to the existing policy metrics implementation.
    /// </summary>
    /// <remarks>
    /// This sink does not implement policy metrics. It translates the canonical event payload back
    /// into the existing <see cref="IAiPolicyMetrics"/> contract so Metrics remains a projection of
    /// the same semantic fact recorded by the Decision Ledger.
    /// </remarks>
    public sealed class PolicyMetricsAiControlPlaneEventSink : IAiControlPlaneEventProjectionSink
    {
        private readonly IAiPolicyMetrics metrics;

        /// <summary>
        /// Initializes a new instance of the <see cref="PolicyMetricsAiControlPlaneEventSink"/> class.
        /// </summary>
        /// <param name="metrics">The existing policy metrics implementation.</param>
        public PolicyMetricsAiControlPlaneEventSink(IAiPolicyMetrics metrics)
        {
            this.metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        }

        /// <inheritdoc />
        public AiEngineEventProjectionTarget ProjectionTarget => AiEngineEventProjectionTarget.Metrics;

        /// <inheritdoc />
        public Task RecordAsync(
            AiControlPlaneEvent controlPlaneEvent,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(controlPlaneEvent);
            cancellationToken.ThrowIfCancellationRequested();

            var semanticEventType = controlPlaneEvent.SemanticEventType;
            if (string.IsNullOrWhiteSpace(semanticEventType))
            {
                return Task.CompletedTask;
            }

            if (!IsPolicyDecisionEvent(semanticEventType))
            {
                throw new InvalidOperationException(
                    $"Canonical metrics event '{semanticEventType}' is not supported by the Policy Metrics projection.");
            }

            var executionId = controlPlaneEvent.Correlation.ExecutionId;
            if (string.IsNullOrWhiteSpace(executionId))
            {
                throw new InvalidOperationException(
                    $"Canonical policy event '{semanticEventType}' does not contain an execution identifier.");
            }

            var policyName = GetRequiredProperty(controlPlaneEvent, AiPolicyMetadataKeys.Name);

            if (string.Equals(semanticEventType, AiEngineEvents.Policy.Failed, StringComparison.Ordinal) &&
                controlPlaneEvent.Properties.ContainsKey(AiExceptionMetadataKeys.ExceptionType) &&
                !controlPlaneEvent.Properties.ContainsKey(AiPolicyMetadataKeys.ResultKind))
            {
                this.metrics.RecordFailure(executionId, policyName);
                return Task.CompletedTask;
            }

            var success = GetRequiredBooleanProperty(controlPlaneEvent, AiPolicyMetadataKeys.ResultSuccess);
            var resultKind = GetRequiredPolicyResultKind(controlPlaneEvent);
            var duration = ResolveDuration(controlPlaneEvent);

            this.metrics.RecordExecution(
                executionId,
                policyName,
                success,
                duration);

            this.metrics.RecordDecision(
                executionId,
                policyName,
                resultKind);

            return Task.CompletedTask;
        }

        private static bool IsPolicyDecisionEvent(string semanticEventType)
        {
            return string.Equals(semanticEventType, AiEngineEvents.Policy.Allowed, StringComparison.Ordinal) ||
                string.Equals(semanticEventType, AiEngineEvents.Policy.Denied, StringComparison.Ordinal) ||
                string.Equals(semanticEventType, AiEngineEvents.Policy.Failed, StringComparison.Ordinal);
        }

        private static string GetRequiredProperty(
            AiControlPlaneEvent controlPlaneEvent,
            string key)
        {
            if (controlPlaneEvent.Properties.TryGetValue(key, out var value) &&
                value is not null &&
                !string.IsNullOrWhiteSpace(value.ToString()))
            {
                return value.ToString()!;
            }

            throw new InvalidOperationException(
                $"Canonical policy event '{controlPlaneEvent.SemanticEventType}' does not contain required property '{key}'.");
        }

        private static bool GetRequiredBooleanProperty(
            AiControlPlaneEvent controlPlaneEvent,
            string key)
        {
            var value = GetRequiredProperty(controlPlaneEvent, key);

            if (bool.TryParse(value, out var parsed))
            {
                return parsed;
            }

            throw new InvalidOperationException(
                $"Canonical policy event '{controlPlaneEvent.SemanticEventType}' contains invalid boolean property '{key}'.");
        }

        private static AiPolicyResultKind GetRequiredPolicyResultKind(
            AiControlPlaneEvent controlPlaneEvent)
        {
            var value = GetRequiredProperty(controlPlaneEvent, AiPolicyMetadataKeys.ResultKind);

            if (Enum.TryParse<AiPolicyResultKind>(value, ignoreCase: false, out var parsed))
            {
                return parsed;
            }

            throw new InvalidOperationException(
                $"Canonical policy event '{controlPlaneEvent.SemanticEventType}' contains invalid policy result kind '{value}'.");
        }

        private static TimeSpan ResolveDuration(AiControlPlaneEvent controlPlaneEvent)
        {
            if (controlPlaneEvent.Properties.TryGetValue(AiObservabilityMetadataKeys.DottedDurationMs, out var value) &&
                value is not null &&
                TryParseDurationMilliseconds(value.ToString(), out var durationMs))
            {
                return TimeSpan.FromMilliseconds(Math.Max(0, durationMs));
            }

            return TimeSpan.FromMilliseconds(Math.Max(0, controlPlaneEvent.DurationMs ?? 0));
        }

        private static bool TryParseDurationMilliseconds(
            string? value,
            out double durationMs)
        {
            return double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.CurrentCulture,
                    out durationMs) ||
                double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out durationMs);
        }
    }
}
