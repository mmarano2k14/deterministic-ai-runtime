using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure
{
    /// <summary>
    /// Provides a deterministic thread-safe in-memory runtime-pool failure journal.
    /// </summary>
    public sealed class InMemoryAiRuntimePoolFailureJournal :
        IAiRuntimePoolFailureObserver,
        IAiRuntimePoolFailureReader
    {
        private readonly object syncRoot = new();
        private readonly Dictionary<string, AiRuntimePoolFailureObservation>
            observationsByFailureId =
            new(StringComparer.Ordinal);

        /// <inheritdoc />
        public Task<AiRuntimePoolFailureObservation> RecordAsync(
            AiRuntimePoolFailureObservation observation,
            CancellationToken cancellationToken = default)
        {
            ValidateObservation(observation);
            cancellationToken.ThrowIfCancellationRequested();

            var normalized =
                Normalize(observation);

            lock (this.syncRoot)
            {
                if (this.observationsByFailureId.TryGetValue(
                        normalized.FailureId,
                        out var existing))
                {
                    if (existing == normalized)
                    {
                        return Task.FromResult(existing);
                    }

                    throw new AiRuntimePoolFailureConflictException(
                        normalized.FailureId);
                }

                this.observationsByFailureId.Add(
                    normalized.FailureId,
                    normalized);

                return Task.FromResult(normalized);
            }
        }

        /// <inheritdoc />
        public Task<AiRuntimePoolFailureObservation?> GetByFailureIdAsync(
            string failureId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(failureId);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                this.observationsByFailureId.TryGetValue(
                    failureId.Trim(),
                    out var observation);

                return Task.FromResult(observation);
            }
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimePoolFailureObservation>>
            ListByHostIdAsync(
                string hostId,
                CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                IReadOnlyList<AiRuntimePoolFailureObservation> observations =
                    this.observationsByFailureId
                        .Values
                        .Where(
                            observation =>
                                StringComparer.Ordinal.Equals(
                                    observation.HostId,
                                    hostId.Trim()))
                        .OrderBy(
                            observation =>
                                observation.ObservedAtUtc)
                        .ThenBy(
                            observation =>
                                observation.FailureId,
                            StringComparer.Ordinal)
                        .ToArray();

                return Task.FromResult(observations);
            }
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimePoolFailureObservation>>
            ListByRuntimeInstanceIdAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                runtimeInstanceId);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                IReadOnlyList<AiRuntimePoolFailureObservation> observations =
                    this.observationsByFailureId
                        .Values
                        .Where(
                            observation =>
                                StringComparer.Ordinal.Equals(
                                    observation.RuntimeInstanceId,
                                    runtimeInstanceId.Trim()))
                        .OrderBy(
                            observation =>
                                observation.ObservedAtUtc)
                        .ThenBy(
                            observation =>
                                observation.FailureId,
                            StringComparer.Ordinal)
                        .ToArray();

                return Task.FromResult(observations);
            }
        }

        /// <summary>
        /// Validates one authoritative failure observation.
        /// </summary>
        private static void ValidateObservation(
            AiRuntimePoolFailureObservation observation)
        {
            ArgumentNullException.ThrowIfNull(observation);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                observation.FailureId);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                observation.PoolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                observation.HostId);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                observation.RuntimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                observation.RouteId);

            if (observation.Scope ==
                    AiRuntimePoolFailureScope.RuntimeInstance &&
                string.IsNullOrWhiteSpace(
                    observation.RuntimeInstanceId))
            {
                throw new ArgumentException(
                    "Runtime-instance failure scope requires RuntimeInstanceId.",
                    nameof(observation));
            }

            if (observation.ObservedAtUtc == default)
            {
                throw new ArgumentException(
                    "ObservedAtUtc is required.",
                    nameof(observation));
            }
        }

        /// <summary>
        /// Normalizes authoritative string identities before storage.
        /// </summary>
        private static AiRuntimePoolFailureObservation Normalize(
            AiRuntimePoolFailureObservation observation)
        {
            return observation with
            {
                FailureId = observation.FailureId.Trim(),
                PoolId = observation.PoolId.Trim(),
                HostId = observation.HostId.Trim(),
                RuntimeInstanceId =
                    observation.RuntimeInstanceId.Trim(),
                RouteId = observation.RouteId.Trim()
            };
        }
    }
}
