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
    }
}
