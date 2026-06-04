namespace Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations
{
    /// <summary>
    /// Stores temporary admission reservations for runtime instances.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Prevents repeated admission decisions from selecting the same runtime instance
    ///   before heartbeat data reflects newly assigned runs.
    /// - Provides a lightweight bridge between admission and dispatch.
    ///
    /// IMPORTANT:
    /// - A reservation is not a run.
    /// - A reservation is temporary capacity accounting.
    /// - Distributed implementations should use atomic operations and TTL.
    /// </remarks>
    public interface IAiRuntimeAdmissionReservationStore
    {
        /// <summary>
        /// Reserves run capacity for a runtime instance.
        /// </summary>
        Task ReserveAsync(
            string runtimeInstanceId,
            int runCount = 1,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Releases reserved run capacity for a runtime instance.
        /// </summary>
        Task ReleaseAsync(
            string runtimeInstanceId,
            int runCount = 1,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the currently reserved run count for a runtime instance.
        /// </summary>
        Task<int> GetReservedRunCountAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default);
    }
}