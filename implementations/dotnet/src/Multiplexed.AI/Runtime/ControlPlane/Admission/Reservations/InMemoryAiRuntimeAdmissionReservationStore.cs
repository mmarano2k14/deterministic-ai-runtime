using System.Collections.Concurrent;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;

namespace Multiplexed.AI.Runtime.ControlPlane.Admission.Reservations
{
    /// <summary>
    /// In-memory implementation of runtime admission reservations.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Provides local-process admission reservation tracking.
    /// - Helps distribute rapid consecutive admissions across available runtime instances.
    ///
    /// IMPORTANT:
    /// - This implementation is process-local.
    /// - It is suitable for local MCP control-plane tests and demos.
    /// - Kubernetes or multi-node deployments should use a Redis-backed implementation.
    /// </remarks>
    public sealed class InMemoryAiRuntimeAdmissionReservationStore :
        IAiRuntimeAdmissionReservationStore
    {
        private readonly ConcurrentDictionary<string, int> reservations =
            new(StringComparer.Ordinal);

        /// <inheritdoc />
        public Task ReserveAsync(
            string runtimeInstanceId,
            int runCount = 1,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runCount);

            cancellationToken.ThrowIfCancellationRequested();

            reservations.AddOrUpdate(
                runtimeInstanceId,
                runCount,
                (_, current) => current + runCount);

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task ReleaseAsync(
            string runtimeInstanceId,
            int runCount = 1,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runCount);

            cancellationToken.ThrowIfCancellationRequested();

            reservations.AddOrUpdate(
                runtimeInstanceId,
                0,
                (_, current) =>
                {
                    var next = current - runCount;

                    return next > 0
                        ? next
                        : 0;
                });

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<int> GetReservedRunCountAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            reservations.TryGetValue(
                runtimeInstanceId,
                out var reservedRunCount);

            return Task.FromResult(reservedRunCount);
        }
    }
}