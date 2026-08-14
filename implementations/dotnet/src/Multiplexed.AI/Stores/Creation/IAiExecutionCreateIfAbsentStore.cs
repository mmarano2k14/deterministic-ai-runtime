using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.AI.Runtime.Execution;
using Multiplexed.AI.Stores;

namespace Multiplexed.AI.Stores.Creation
{
    /// <summary>
    /// Defines the narrow persistence capability required to create one exact execution identity at most once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This capability is intentionally separate from <see cref="IAiExecutionStore.CreateAsync"/> because the
    /// historical create path is overwrite-oriented. Deterministic child execution composition requires a
    /// fail-closed create-if-absent boundary for a preallocated execution identifier.
    /// </para>
    /// <para>
    /// Implementations must create the execution record and state as one logical operation and must never replace
    /// an execution that already exists under the supplied identifier.
    /// </para>
    /// </remarks>
    public interface IAiExecutionCreateIfAbsentStore
    {
        /// <summary>
        /// Attempts to create the supplied execution record and state only when the execution identifier is absent.
        /// </summary>
        /// <param name="record">The execution record carrying the exact preallocated identifier.</param>
        /// <param name="state">The execution state carrying the same exact identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// <c>true</c> when this caller created the execution; otherwise <c>false</c> when an execution already
        /// existed under the same identifier.
        /// </returns>
        Task<bool> TryCreateIfAbsentAsync(
            AiExecutionRecord record,
            AiExecutionState state,
            CancellationToken cancellationToken = default);
    }
}
