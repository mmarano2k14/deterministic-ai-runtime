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
    /// - Provides bounded atomic acquisition for hierarchical capacity selection.
    ///
    /// IMPORTANT:
    /// - This implementation is process-local.
    /// - It is suitable for local MCP control-plane tests and demos.
    /// - Kubernetes or multi-node deployments should use a Redis-backed implementation.
    /// </remarks>
    public sealed class InMemoryAiRuntimeAdmissionReservationStore :
        IAiRuntimeAtomicAdmissionReservationStore
    {
        private readonly Dictionary<string, int> reservations =
            new(StringComparer.Ordinal);

        private readonly object synchronizationRoot = new();

        /// <inheritdoc />
        public Task ReserveAsync(
            string runtimeInstanceId,
            int runCount = 1,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runCount);

            cancellationToken.ThrowIfCancellationRequested();

            lock (this.synchronizationRoot)
            {
                this.reservations.TryGetValue(
                    runtimeInstanceId,
                    out var current);

                this.reservations[runtimeInstanceId] =
                    current + runCount;
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<AiRuntimeAdmissionReservationAttemptResult>
            TryReserveAsync(
                string runtimeInstanceId,
                int maximumReservedRunCount,
                int runCount = 1,
                CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                maximumReservedRunCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runCount);

            cancellationToken.ThrowIfCancellationRequested();

            lock (this.synchronizationRoot)
            {
                this.reservations.TryGetValue(
                    runtimeInstanceId,
                    out var current);

                var canReserve =
                    current >= 0 &&
                    runCount <= maximumReservedRunCount &&
                    current <= maximumReservedRunCount - runCount;

                if (!canReserve)
                {
                    return Task.FromResult(
                        CreateAttemptResult(
                            runtimeInstanceId,
                            maximumReservedRunCount,
                            runCount,
                            current,
                            AiRuntimeAdmissionReservationAttemptStatus
                                .CapacityUnavailable));
                }

                var next =
                    current + runCount;

                this.reservations[runtimeInstanceId] = next;

                return Task.FromResult(
                    CreateAttemptResult(
                        runtimeInstanceId,
                        maximumReservedRunCount,
                        runCount,
                        next,
                        AiRuntimeAdmissionReservationAttemptStatus
                            .Acquired));
            }
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

            lock (this.synchronizationRoot)
            {
                if (!this.reservations.TryGetValue(
                        runtimeInstanceId,
                        out var current))
                {
                    return Task.CompletedTask;
                }

                var next = current - runCount;

                if (next > 0)
                {
                    this.reservations[runtimeInstanceId] = next;
                }
                else
                {
                    this.reservations.Remove(runtimeInstanceId);
                }
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<int> GetReservedRunCountAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            lock (this.synchronizationRoot)
            {
                this.reservations.TryGetValue(
                    runtimeInstanceId,
                    out var reservedRunCount);

                return Task.FromResult(reservedRunCount);
            }
        }

        /// <summary>
        /// Creates one bounded admission reservation attempt result.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="maximumReservedRunCount">
        /// The maximum total reservation count.
        /// </param>
        /// <param name="requestedRunCount">The requested run count.</param>
        /// <param name="reservedRunCount">The resulting reserved run count.</param>
        /// <param name="status">The reservation attempt status.</param>
        /// <returns>The reservation attempt result.</returns>
        private static AiRuntimeAdmissionReservationAttemptResult
            CreateAttemptResult(
                string runtimeInstanceId,
                int maximumReservedRunCount,
                int requestedRunCount,
                int reservedRunCount,
                AiRuntimeAdmissionReservationAttemptStatus status)
        {
            return new AiRuntimeAdmissionReservationAttemptResult
            {
                Status = status,
                RuntimeInstanceId = runtimeInstanceId,
                RequestedRunCount = requestedRunCount,
                ReservedRunCount = reservedRunCount,
                MaximumReservedRunCount = maximumReservedRunCount
            };
        }
    }
}
