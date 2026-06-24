namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership
{
    /// <summary>
    /// Resolves shared run ownership from runtime execution identifiers.
    /// </summary>
    /// <remarks>
    /// This resolver is read-only.
    /// It must not requeue shared queue items, mutate shared run records,
    /// change runtime execution index entries, or perform execution recovery.
    /// </remarks>
    public interface IAiSharedRunOwnershipResolver
    {
        /// <summary>
        /// Resolves shared run ownership from runtime execution identifiers.
        /// </summary>
        /// <param name="request">The ownership resolution request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The ownership resolution result.</returns>
        Task<AiSharedRunOwnershipResolutionResult> ResolveAsync(
            AiSharedRunOwnershipResolutionRequest request,
            CancellationToken cancellationToken = default);
    }
}