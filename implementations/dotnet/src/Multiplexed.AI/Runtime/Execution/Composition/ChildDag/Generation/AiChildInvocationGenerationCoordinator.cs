using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations.Persistence;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Identity;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Generation
{
    /// <summary>
    /// Coordinates explicit durable advancement from one child invocation generation to the next.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Generic parent-step retry or infrastructure recovery must never call this component implicitly. Re-entering the
    /// same logical invocation generation remains bound to the same relation, child execution, and terminal outcome.
    /// </para>
    /// <para>
    /// A genuinely new child attempt requires this explicit boundary. The next generation is first committed on the
    /// terminal current relation, then the next relation is created idempotently. If the process crashes between those
    /// writes, recovery re-derives the same next-generation identity from the durable decision and creates exactly one
    /// eventual relation.
    /// </para>
    /// </remarks>
    public sealed class AiChildInvocationGenerationCoordinator
    {
        private readonly IAiChildExecutionRelationStore relationStore;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiChildInvocationGenerationCoordinator"/> class.
        /// </summary>
        /// <param name="relationStore">The authoritative parent-child relation store.</param>
        public AiChildInvocationGenerationCoordinator(
            IAiChildExecutionRelationStore relationStore)
        {
            this.relationStore = relationStore ?? throw new ArgumentNullException(nameof(relationStore));
        }

        /// <summary>
        /// Commits an explicit retry decision and returns the authoritative next-generation relation.
        /// </summary>
        /// <param name="currentIdentity">The authoritative identity of the terminal current generation.</param>
        /// <param name="reason">The durable reason for explicitly requesting a new child attempt.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// The exactly-once next-generation relation. Concurrent callers and recovery after the decision-to-relation
        /// crash window converge on the same typed identity and derived child invocation key.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the current generation is not eligible for an explicit retry, when generation arithmetic would
        /// overflow, or when durable generation state is inconsistent.
        /// </exception>
        public async Task<AiChildExecutionRelation> PrepareNextGenerationAsync(
            AiChildInvocationIdentity currentIdentity,
            string reason,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(currentIdentity);
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);

            var current = await this.relationStore
                .GetAsync(currentIdentity, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "A next child invocation generation cannot be prepared before the current durable relation exists.");

            EnsureRetryEligible(current);

            if (current.InvocationGeneration == int.MaxValue)
            {
                throw new InvalidOperationException(
                    "The child invocation generation cannot advance beyond Int32.MaxValue.");
            }

            var expectedNextGeneration = current.InvocationGeneration + 1;

            if (!current.NextInvocationGeneration.HasValue)
            {
                current.NextInvocationGeneration = expectedNextGeneration;
                current.NextInvocationGenerationDecidedAtUtc = DateTimeOffset.UtcNow;
                current.NextInvocationGenerationDecisionReason = reason;

                var committed = await this.relationStore
                    .TryCommitNextInvocationGenerationAsync(current, cancellationToken)
                    .ConfigureAwait(false);

                if (!committed)
                {
                    current = await this.relationStore
                        .GetAsync(currentIdentity, cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidOperationException(
                            "Next-generation retry decision CAS lost and the authoritative current relation could not be reloaded.");
                }
            }

            EnsureCommittedNextGeneration(current, expectedNextGeneration);
            return await EnsureNextGenerationRelationAsync(current, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Validates whether one durable relation is allowed to authorize a genuinely new child attempt.
        /// </summary>
        /// <param name="relation">The current authoritative relation.</param>
        private static void EnsureRetryEligible(AiChildExecutionRelation relation)
        {
            if (relation.Status == AiChildExecutionRelationStatus.DelegationDenied)
            {
                return;
            }

            if (relation.Status == AiChildExecutionRelationStatus.Completed &&
                !string.IsNullOrWhiteSpace(relation.ChildFailureReason) &&
                relation.ContinuationStatus == AiChildContinuationStatus.Resumed)
            {
                return;
            }

            throw new InvalidOperationException(
                "A new child invocation generation requires durable delegation denial or a failed child outcome whose parent continuation has durably resumed. " +
                $"RelationStatus='{relation.Status}', ContinuationStatus='{relation.ContinuationStatus}', " +
                $"HasFailure={!string.IsNullOrWhiteSpace(relation.ChildFailureReason)}.");
        }

        /// <summary>
        /// Verifies that a durable next-generation decision advances exactly one generation.
        /// </summary>
        /// <param name="relation">The relation carrying the committed retry decision.</param>
        /// <param name="expectedNextGeneration">The exact next generation derived from the current identity.</param>
        private static void EnsureCommittedNextGeneration(
            AiChildExecutionRelation relation,
            int expectedNextGeneration)
        {
            if (relation.NextInvocationGeneration != expectedNextGeneration ||
                relation.NextInvocationGenerationDecidedAtUtc is null ||
                string.IsNullOrWhiteSpace(relation.NextInvocationGenerationDecisionReason))
            {
                throw new InvalidOperationException(
                    $"Child relation '{relation.ChildInvocationKey}' contains an inconsistent durable next-generation retry decision.");
            }
        }

        /// <summary>
        /// Creates or reloads the complete next-generation relation from immutable current-generation inputs.
        /// </summary>
        /// <param name="current">The current relation carrying the durable generation decision.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The authoritative next-generation relation.</returns>
        private async Task<AiChildExecutionRelation> EnsureNextGenerationRelationAsync(
            AiChildExecutionRelation current,
            CancellationToken cancellationToken)
        {
            var nextGeneration = current.NextInvocationGeneration
                ?? throw new InvalidOperationException(
                    "A next-generation relation cannot be created before the durable generation decision exists.");

            var nextIdentity = new AiChildInvocationIdentity
            {
                TenantId = current.TenantId,
                ParentExecutionId = current.ParentExecutionId,
                ParentCallSiteId = current.ParentCallSiteId,
                ChildDagId = current.ChildDagId,
                ChildDagDefinitionVersion = current.ChildDagDefinitionVersion,
                CanonicalLogicalInvocationKey = current.CanonicalLogicalInvocationKey,
                InvocationGeneration = nextGeneration
            };

            var nextRelation = new AiChildExecutionRelation
            {
                TenantId = current.TenantId,
                ParentExecutionId = current.ParentExecutionId,
                ParentCallSiteId = current.ParentCallSiteId,
                ChildDagId = current.ChildDagId,
                ChildDagDefinitionVersion = current.ChildDagDefinitionVersion,
                FrozenChildDagDefinition = current.FrozenChildDagDefinition,
                CanonicalLogicalInvocationKey = current.CanonicalLogicalInvocationKey,
                ChildInvocationKey = AiChildInvocationKeyFactory.Create(nextIdentity),
                InvocationGeneration = nextGeneration,
                FrozenInvocationInput = current.FrozenInvocationInput,
                DelegatedExecutionContextSnapshot = current.DelegatedExecutionContextSnapshot,
                DelegatedMetadata = current.DelegatedMetadata.ToDictionary(
                    item => item.Key,
                    item => item.Value,
                    StringComparer.Ordinal),
                DelegationPolicyBindingSnapshot = current.DelegationPolicyBindingSnapshot,
                Status = AiChildExecutionRelationStatus.DelegationPolicyPending,
                ContinuationStatus = AiChildContinuationStatus.None,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            return await this.relationStore
                .GetOrCreateAsync(nextRelation, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
