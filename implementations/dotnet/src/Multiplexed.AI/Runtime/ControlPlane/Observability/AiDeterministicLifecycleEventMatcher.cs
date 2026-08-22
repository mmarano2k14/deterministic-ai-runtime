using System;
using System.Globalization;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;

namespace Multiplexed.AI.Runtime.ControlPlane.Observability
{
    /// <summary>
    /// Evaluates canonical lifecycle events against deterministic observation criteria.
    /// </summary>
    internal static class AiDeterministicLifecycleEventMatcher
    {
        /// <summary>
        /// Determines whether the supplied canonical event satisfies all requested identity and property filters.
        /// </summary>
        /// <param name="controlPlaneEvent">The canonical engine event to evaluate.</param>
        /// <param name="criteria">The deterministic observation criteria.</param>
        /// <returns><c>true</c> when every requested filter matches; otherwise, <c>false</c>.</returns>
        public static bool Matches(
            AiControlPlaneEvent controlPlaneEvent,
            AiDeterministicLifecycleEventCriteria criteria)
        {
            ArgumentNullException.ThrowIfNull(controlPlaneEvent);
            ArgumentNullException.ThrowIfNull(criteria);

            if (!string.Equals(
                    controlPlaneEvent.SemanticEventType,
                    criteria.SemanticEventType,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (!MatchesOptional(controlPlaneEvent.EventId, criteria.EventId) ||
                !MatchesOptional(controlPlaneEvent.Correlation.CorrelationId, criteria.CorrelationId) ||
                !MatchesOptional(controlPlaneEvent.Correlation.ExecutionId, criteria.ExecutionId) ||
                !MatchesOptional(controlPlaneEvent.Correlation.RunId, criteria.RunId) ||
                !MatchesOptional(controlPlaneEvent.Correlation.RuntimeInstanceId, criteria.RuntimeInstanceId) ||
                !MatchesOptional(controlPlaneEvent.Correlation.RunId, criteria.SharedRunId) ||
                !MatchesOptional(controlPlaneEvent.Correlation.CorrelationId, criteria.ForensicsId))
            {
                return false;
            }

            foreach (var property in criteria.Properties)
            {
                if (!controlPlaneEvent.Properties.TryGetValue(property.Key, out var value) ||
                    !string.Equals(
                        Convert.ToString(value, CultureInfo.InvariantCulture),
                        property.Value,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool MatchesOptional(string? actual, string? expected)
        {
            return string.IsNullOrWhiteSpace(expected) ||
                string.Equals(actual, expected, StringComparison.Ordinal);
        }
    }
}
