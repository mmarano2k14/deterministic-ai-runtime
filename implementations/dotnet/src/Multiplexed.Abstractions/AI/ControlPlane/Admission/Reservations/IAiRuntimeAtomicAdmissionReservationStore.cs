namespace Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations
{
    /// <summary>
    /// Extends runtime admission reservations with a bounded atomic acquisition
    /// operation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The existing unbounded reservation operations remain available for backward
    /// compatibility. Hierarchical capacity selection uses
    /// <see cref="TryReserveAsync" /> so concurrent selectors cannot consume more run
    /// slots than one authoritative capacity snapshot exposed.
    /// </para>
    /// <para>
    /// Distributed implementations must perform the count check and reservation
    /// mutation in one atomic storage operation.
    /// </para>
    /// </remarks>
    public interface IAiRuntimeAtomicAdmissionReservationStore :
        IAiRuntimeAdmissionReservationStore
    {
        /// <summary>
        /// Attempts to atomically reserve run slots without exceeding one supplied
        /// total reservation boundary.
        /// </summary>
        /// <param name="runtimeInstanceId">
        /// The runtime instance identifier.
        /// </param>
        /// <param name="maximumReservedRunCount">
        /// The maximum total number of reservations allowed after acquisition.
        /// </param>
        /// <param name="runCount">
        /// The number of run slots requested by the caller.
        /// </param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The bounded atomic reservation result.</returns>
        Task<AiRuntimeAdmissionReservationAttemptResult> TryReserveAsync(
            string runtimeInstanceId,
            int maximumReservedRunCount,
            int runCount = 1,
            CancellationToken cancellationToken = default);
    }
}
