using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity;

namespace Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations.Persistence
{
    /// <summary>
    /// Defines durable persistence for authoritative parent-to-child execution relations.
    /// </summary>
    /// <remarks>
    /// Database uniqueness is owned by the complete typed invocation identity. The derived child invocation key
    /// remains a lookup and integrity aid and must not replace the typed tuple as the uniqueness authority.
    /// </remarks>
    public interface IAiChildExecutionRelationStore
    {
        /// <summary>
        /// Gets the durable relation for one complete logical child invocation identity.
        /// </summary>
        /// <param name="identity">The authoritative typed invocation identity.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The relation when present; otherwise, <see langword="null"/>.</returns>
        Task<AiChildExecutionRelation?> GetAsync(
            AiChildInvocationIdentity identity,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the durable relation that owns one exact child execution identifier.
        /// </summary>
        /// <param name="childExecutionId">The exact child execution identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The authoritative relation when present; otherwise <see langword="null"/>.</returns>
        Task<AiChildExecutionRelation?> GetByChildExecutionIdAsync(
            string childExecutionId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists allocated or waiting child relations whose child execution may still reach a terminal outcome.
        /// </summary>
        /// <param name="maxCount">The maximum number of relations to return.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="controlPlaneId">Optional logical control-plane authority used to scope background reconciliation.</param>
        /// <returns>The oldest incomplete child relations first.</returns>
        Task<IReadOnlyList<AiChildExecutionRelation>> ListIncompleteAsync(
            int maxCount,
            CancellationToken cancellationToken = default,
            string? controlPlaneId = null);

        /// <summary>
        /// Lists completed child relations whose parent continuation is pending or scheduled but not yet resumed.
        /// </summary>
        /// <param name="maxCount">The maximum number of relations to return.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="controlPlaneId">Optional logical control-plane authority used to scope background reconciliation.</param>
        /// <returns>The oldest continuation candidates first.</returns>
        Task<IReadOnlyList<AiChildExecutionRelation>> ListContinuationCandidatesAsync(
            int maxCount,
            CancellationToken cancellationToken = default,
            string? controlPlaneId = null);

        /// <summary>
        /// Lists child-allocated relations old enough to require defensive parent-park consistency checking.
        /// </summary>
        /// <param name="allocatedBeforeUtc">Only relations allocated on or before this UTC boundary are returned.</param>
        /// <param name="maxCount">The maximum number of relations to return.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="controlPlaneId">Optional logical control-plane authority used to scope background reconciliation.</param>
        /// <returns>The oldest child-allocated relations first.</returns>
        Task<IReadOnlyList<AiChildExecutionRelation>> ListParkConsistencyCandidatesAsync(
            DateTimeOffset allocatedBeforeUtc,
            int maxCount,
            CancellationToken cancellationToken = default,
            string? controlPlaneId = null);

        /// <summary>
        /// Creates a complete initial relation or returns the already committed equivalent relation.
        /// </summary>
        /// <param name="relation">The complete initial relation to persist.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The authoritative durable relation.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the same typed invocation identity already exists with conflicting immutable creation data.
        /// </exception>
        Task<AiChildExecutionRelation> GetOrCreateAsync(
            AiChildExecutionRelation relation,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Atomically replaces a relation only while its persisted status matches the expected status.
        /// </summary>
        /// <param name="relation">The complete replacement relation.</param>
        /// <param name="expectedStatus">The status that must still be authoritative in durable storage.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><see langword="true"/> when the compare-and-swap succeeds; otherwise, <see langword="false"/>.</returns>
        Task<bool> TryReplaceAsync(
            AiChildExecutionRelation relation,
            AiChildExecutionRelationStatus expectedStatus,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Atomically replaces a completed relation only while its continuation status matches the expected value.
        /// </summary>
        /// <param name="relation">The complete replacement relation.</param>
        /// <param name="expectedContinuationStatus">The continuation status that must still be authoritative.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><see langword="true"/> when the compare-and-swap succeeds; otherwise <see langword="false"/>.</returns>
        Task<bool> TryReplaceContinuationAsync(
            AiChildExecutionRelation relation,
            AiChildContinuationStatus expectedContinuationStatus,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Atomically commits the explicit next-generation retry decision for one terminal relation.
        /// </summary>
        /// <param name="relation">The complete relation containing the next-generation decision.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// <see langword="true"/> when this caller commits the decision; otherwise <see langword="false"/> when
        /// another caller already committed the durable generation advance.
        /// </returns>
        Task<bool> TryCommitNextInvocationGenerationAsync(
            AiChildExecutionRelation relation,
            CancellationToken cancellationToken = default);
    }
}
