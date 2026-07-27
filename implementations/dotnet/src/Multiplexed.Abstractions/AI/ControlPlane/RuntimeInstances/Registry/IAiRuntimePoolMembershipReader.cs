namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry
{
    /// <summary>
    /// Defines first-class runtime pool and host membership queries.
    /// </summary>
    /// <remarks>
    /// Membership is resolved exclusively from the typed <see cref="AiRuntimeInstanceSnapshot.PoolId"/>
    /// and <see cref="AiRuntimeInstanceSnapshot.HostId"/> properties. Optional metadata is never an
    /// authoritative source for routing, lifecycle, draining, capacity selection, or recovery.
    ///
    /// The reader returns currently registered members. Runtime instances with the
    /// <see cref="AiRuntimeInstanceStatus.Stopped"/> status are excluded, while draining and unhealthy
    /// members remain visible so lifecycle and recovery components can reason about them explicitly.
    /// </remarks>
    public interface IAiRuntimePoolMembershipReader
    {
        /// <summary>
        /// Lists the runtime instances that belong to a logical runtime pool.
        /// </summary>
        /// <param name="poolId">The logical runtime pool identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The runtime instances that currently belong to the pool.</returns>
        Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListByPoolIdAsync(
            string poolId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists the runtime instances that belong to an exact host incarnation.
        /// </summary>
        /// <param name="hostId">The immutable host-incarnation identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The runtime instances that currently belong to the host.</returns>
        Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListByHostIdAsync(
            string hostId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists the exact host incarnations that currently contribute runtime instances to a pool.
        /// </summary>
        /// <param name="poolId">The logical runtime pool identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The distinct immutable host-incarnation identifiers for the pool.</returns>
        Task<IReadOnlyList<string>> ListHostIdsByPoolIdAsync(
            string poolId,
            CancellationToken cancellationToken = default);
    }
}
