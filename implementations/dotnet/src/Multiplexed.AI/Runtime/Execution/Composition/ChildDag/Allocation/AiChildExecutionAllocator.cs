using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations.Persistence;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Snapshots;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Allocation
{
    /// <summary>
    /// Allocates and durably binds the exact execution identifier for an approved child DAG invocation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Allocation is relation-driven and idempotent. A retry, recovery, or concurrent allocator using the same
    /// typed invocation identity converges on the already committed <see cref="AiChildExecutionRelation.ChildExecutionId"/>.
    /// </para>
    /// <para>
    /// This component does not create or dispatch the child execution. It only verifies the frozen declarative
    /// definition and input, allocates one candidate execution identifier, and persists that mapping before any
    /// child-side observable effect is permitted.
    /// </para>
    /// </remarks>
    public sealed class AiChildExecutionAllocator
    {
        private readonly IAiChildExecutionRelationStore relationStore;
        private readonly AiChildDagSnapshotService snapshotService;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiChildExecutionAllocator"/> class.
        /// </summary>
        /// <param name="relationStore">The authoritative durable child relation store.</param>
        /// <param name="snapshotService">The immutable child DAG snapshot service.</param>
        public AiChildExecutionAllocator(
            IAiChildExecutionRelationStore relationStore,
            AiChildDagSnapshotService snapshotService)
        {
            this.relationStore = relationStore ?? throw new ArgumentNullException(nameof(relationStore));
            this.snapshotService = snapshotService ?? throw new ArgumentNullException(nameof(snapshotService));
        }

        /// <summary>
        /// Allocates the exact child execution identifier for one approved logical invocation.
        /// </summary>
        /// <param name="identity">The authoritative typed child invocation identity.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// The authoritative relation containing the committed child execution identifier. If another allocator
        /// wins the compare-and-swap race, the winning durable relation is reloaded and returned.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the relation does not exist, delegation is not durably approved, the frozen definition does
        /// not match the relation identity, the frozen definition is not a DAG, or an allocated relation is invalid.
        /// </exception>
        public async Task<AiChildExecutionRelation> AllocateAsync(
            AiChildInvocationIdentity identity,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(identity);

            var relation = await this.relationStore
                .GetAsync(identity, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "A child execution identifier cannot be allocated before the authoritative relation exists.");

            if (relation.Status is AiChildExecutionRelationStatus.ChildAllocated or
                AiChildExecutionRelationStatus.Waiting or
                AiChildExecutionRelationStatus.Completed)
            {
                EnsureCommittedAllocation(relation);
                return relation;
            }

            if (relation.Status == AiChildExecutionRelationStatus.DelegationDenied)
            {
                throw new InvalidOperationException(
                    "A child execution identifier cannot be allocated after delegation was denied.");
            }

            if (relation.Status != AiChildExecutionRelationStatus.DelegationApproved)
            {
                throw new InvalidOperationException(
                    $"A child execution identifier cannot be allocated from relation status '{relation.Status}'.");
            }

            if (relation.ChildExecutionId is not null || relation.ChildAllocatedAtUtc is not null)
            {
                throw new InvalidOperationException(
                    "A delegation-approved relation cannot expose child allocation data before the ChildAllocated transition.");
            }

            await ValidatePinnedCreationInputsAsync(relation, cancellationToken).ConfigureAwait(false);

            relation.ChildExecutionId = Guid.NewGuid().ToString("N");
            relation.ChildAllocatedAtUtc = DateTimeOffset.UtcNow;
            relation.Status = AiChildExecutionRelationStatus.ChildAllocated;

            var committed = await this.relationStore
                .TryReplaceAsync(
                    relation,
                    AiChildExecutionRelationStatus.DelegationApproved,
                    cancellationToken)
                .ConfigureAwait(false);

            if (committed)
            {
                return relation;
            }

            var winner = await this.relationStore
                .GetAsync(identity, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Child execution allocation CAS lost and the committed relation could not be reloaded.");

            EnsureCommittedAllocation(winner);
            return winner;
        }

        /// <summary>
        /// Verifies that the exact frozen definition and invocation input remain durable and match the relation.
        /// </summary>
        /// <param name="relation">The approved relation being prepared for allocation.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task ValidatePinnedCreationInputsAsync(
            AiChildExecutionRelation relation,
            CancellationToken cancellationToken)
        {
            var definition = await this.snapshotService
                .LoadDefinitionAsync(
                    relation.FrozenChildDagDefinition,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!string.Equals(definition.Name, relation.ChildDagId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Frozen child DAG definition name '{definition.Name}' does not match relation child DAG id '{relation.ChildDagId}'.");
            }

            if (!string.Equals(
                    definition.Version,
                    relation.ChildDagDefinitionVersion,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Frozen child DAG definition version '{definition.Version ?? string.Empty}' does not match relation version '{relation.ChildDagDefinitionVersion}'.");
            }

            if (definition.ExecutionMode != AiExecutionMode.Dag)
            {
                throw new InvalidOperationException(
                    $"Frozen child pipeline '{definition.Name}' is configured for mode '{definition.ExecutionMode}' and cannot be allocated as a child DAG execution.");
            }

            _ = await this.snapshotService
                .LoadAndVerifyAsync(
                    relation.FrozenInvocationInput,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Validates the durable allocation invariant for an already progressed relation.
        /// </summary>
        /// <param name="relation">The authoritative relation.</param>
        private static void EnsureCommittedAllocation(AiChildExecutionRelation relation)
        {
            if (string.IsNullOrWhiteSpace(relation.ChildExecutionId) || relation.ChildAllocatedAtUtc is null)
            {
                throw new InvalidOperationException(
                    $"Child relation status '{relation.Status}' requires one durably allocated child execution identifier.");
            }
        }
    }
}
