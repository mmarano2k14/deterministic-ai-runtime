using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations.Persistence;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Suspension
{
    /// <summary>
    /// Commits the authoritative child relation waiting state before a parent DAG step is parked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This coordinator owns only the business-state transition from
    /// <see cref="AiChildExecutionRelationStatus.ChildAllocated"/> to
    /// <see cref="AiChildExecutionRelationStatus.Waiting"/>. It does not mutate DAG step state,
    /// release claims, release concurrency leases, or schedule parent continuation.
    /// </para>
    /// <para>
    /// Callers must complete this durable relation transition before returning a Park step outcome.
    /// This ordering ensures that <c>WaitingForExternal</c> can never become authoritative before
    /// the parent-child relation records the corresponding external wait.
    /// </para>
    /// </remarks>
    public sealed class AiChildExecutionWaitingCoordinator
    {
        private readonly IAiChildExecutionRelationStore relationStore;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiChildExecutionWaitingCoordinator"/> class.
        /// </summary>
        /// <param name="relationStore">The authoritative durable parent-child relation store.</param>
        public AiChildExecutionWaitingCoordinator(
            IAiChildExecutionRelationStore relationStore)
        {
            this.relationStore = relationStore ?? throw new ArgumentNullException(nameof(relationStore));
        }

        /// <summary>
        /// Ensures that the authoritative relation has entered its durable waiting state.
        /// </summary>
        /// <param name="identity">The complete typed child invocation identity.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The authoritative relation after convergence.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the relation is missing or is in a state that cannot enter the waiting phase.
        /// </exception>
        public async Task<AiChildExecutionRelation> EnsureWaitingAsync(
            AiChildInvocationIdentity identity,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(identity);

            var relation = await relationStore
                .GetAsync(identity, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "A child execution relation must exist before the parent can enter an external wait.");

            if (relation.Status is AiChildExecutionRelationStatus.Waiting or
                AiChildExecutionRelationStatus.Completed)
            {
                return relation;
            }

            if (relation.Status != AiChildExecutionRelationStatus.ChildAllocated)
            {
                throw new InvalidOperationException(
                    $"Child relation status '{relation.Status}' cannot enter the waiting state.");
            }

            if (string.IsNullOrWhiteSpace(relation.ChildExecutionId) || relation.ChildAllocatedAtUtc is null)
            {
                throw new InvalidOperationException(
                    "A child relation cannot enter the waiting state before exact child execution allocation is durable.");
            }

            relation.Status = AiChildExecutionRelationStatus.Waiting;
            relation.WaitingAtUtc = DateTimeOffset.UtcNow;

            var committed = await relationStore
                .TryReplaceAsync(
                    relation,
                    AiChildExecutionRelationStatus.ChildAllocated,
                    cancellationToken)
                .ConfigureAwait(false);

            if (committed)
            {
                return relation;
            }

            var winner = await relationStore
                .GetAsync(identity, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Child relation waiting CAS lost and the authoritative relation could not be reloaded.");

            if (winner.Status is AiChildExecutionRelationStatus.Waiting or
                AiChildExecutionRelationStatus.Completed)
            {
                return winner;
            }

            throw new InvalidOperationException(
                $"Child relation waiting CAS lost to unexpected status '{winner.Status}'.");
        }
    }
}
