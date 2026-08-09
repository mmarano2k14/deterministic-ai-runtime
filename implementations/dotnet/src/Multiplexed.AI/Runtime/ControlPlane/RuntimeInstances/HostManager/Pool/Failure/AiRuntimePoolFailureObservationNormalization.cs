using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure
{
    /// <summary>
    /// Provides the single validation and normalization contract used by every runtime-pool
    /// failure-journal implementation.
    /// </summary>
    internal static class AiRuntimePoolFailureObservationNormalization
    {
        public static AiRuntimePoolFailureObservation Normalize(
            AiRuntimePoolFailureObservation observation)
        {
            Validate(observation);

            return observation with
            {
                FailureId = observation.FailureId.Trim(),
                PoolId = observation.PoolId.Trim(),
                HostId = observation.HostId.Trim(),
                RuntimeInstanceId =
                    string.IsNullOrWhiteSpace(observation.RuntimeInstanceId)
                        ? null
                        : observation.RuntimeInstanceId.Trim(),
                RouteId =
                    string.IsNullOrWhiteSpace(observation.RouteId)
                        ? null
                        : observation.RouteId.Trim()
            };
        }

        public static bool AreEquivalent(
            AiRuntimePoolFailureObservation left,
            AiRuntimePoolFailureObservation right)
        {
            ArgumentNullException.ThrowIfNull(left);
            ArgumentNullException.ThrowIfNull(right);

            return Normalize(left) == Normalize(right);
        }

        private static void Validate(
            AiRuntimePoolFailureObservation observation)
        {
            ArgumentNullException.ThrowIfNull(observation);
            ArgumentException.ThrowIfNullOrWhiteSpace(observation.FailureId);
            ArgumentException.ThrowIfNullOrWhiteSpace(observation.PoolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(observation.HostId);

            if (observation.Scope == AiRuntimePoolFailureScope.RuntimeInstance &&
                (string.IsNullOrWhiteSpace(observation.RuntimeInstanceId) ||
                 string.IsNullOrWhiteSpace(observation.RouteId)))
            {
                throw new ArgumentException(
                    "Runtime-instance failure scope requires RuntimeInstanceId and RouteId.",
                    nameof(observation));
            }

            if (observation.Scope == AiRuntimePoolFailureScope.Host &&
                (!string.IsNullOrWhiteSpace(observation.RuntimeInstanceId) ||
                 !string.IsNullOrWhiteSpace(observation.RouteId)))
            {
                throw new ArgumentException(
                    "Host failure scope must not carry one child runtime or local route identity.",
                    nameof(observation));
            }

            if (observation.ObservedAtUtc == default)
            {
                throw new ArgumentException(
                    "ObservedAtUtc is required.",
                    nameof(observation));
            }
        }
    }
}
