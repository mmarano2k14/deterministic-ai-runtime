using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations.Persistence;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Suspension;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Suspension
{
    /// <summary>
    /// Unit tests for durable child relation suspension before parent step parking.
    /// </summary>
    public sealed class AiChildExecutionWaitingCoordinatorTests
    {
        /// <summary>
        /// Verifies that an allocated child relation enters Waiting durably before the caller can park its parent step.
        /// </summary>
        [Fact]
        public async Task EnsureWaitingAsync_Should_Commit_Allocated_Relation_As_Waiting()
        {
            var relation = CreateRelation(AiChildExecutionRelationStatus.ChildAllocated);
            var store = new InMemoryRelationStore(relation);
            var coordinator = new AiChildExecutionWaitingCoordinator(store);

            var result = await coordinator.EnsureWaitingAsync(relation.ToInvocationIdentity());

            Assert.Equal(AiChildExecutionRelationStatus.Waiting, result.Status);
            Assert.NotNull(result.WaitingAtUtc);
            Assert.Equal(relation.ChildExecutionId, result.ChildExecutionId);
            Assert.Equal(1, store.SuccessfulReplaceCount);
        }

        /// <summary>
        /// Verifies that concurrent waiting attempts converge on the same authoritative relation.
        /// </summary>
        [Fact]
        public async Task EnsureWaitingAsync_Should_Converge_Concurrent_Callers()
        {
            var relation = CreateRelation(AiChildExecutionRelationStatus.ChildAllocated);
            var store = new InMemoryRelationStore(relation);
            var coordinator = new AiChildExecutionWaitingCoordinator(store);

            var results = await Task.WhenAll(
                Enumerable.Range(0, 8)
                    .Select(_ => coordinator.EnsureWaitingAsync(relation.ToInvocationIdentity())));

            Assert.All(results, item =>
            {
                Assert.Equal(AiChildExecutionRelationStatus.Waiting, item.Status);
                Assert.NotNull(item.WaitingAtUtc);
                Assert.Equal(relation.ChildExecutionId, item.ChildExecutionId);
            });

            Assert.Equal(1, store.SuccessfulReplaceCount);
        }

        /// <summary>
        /// Verifies that a child that completed before parent parking is never regressed back to Waiting.
        /// </summary>
        [Fact]
        public async Task EnsureWaitingAsync_Should_Not_Regress_Already_Completed_Relation()
        {
            var relation = CreateRelation(AiChildExecutionRelationStatus.Completed);
            relation.CompletedAtUtc = DateTimeOffset.UtcNow;

            var store = new InMemoryRelationStore(relation);
            var coordinator = new AiChildExecutionWaitingCoordinator(store);

            var result = await coordinator.EnsureWaitingAsync(relation.ToInvocationIdentity());

            Assert.Equal(AiChildExecutionRelationStatus.Completed, result.Status);
            Assert.Equal(0, store.SuccessfulReplaceCount);
        }

        private static AiChildExecutionRelation CreateRelation(
            AiChildExecutionRelationStatus status)
        {
            var now = DateTimeOffset.UtcNow;

            return new AiChildExecutionRelation
            {
                TenantId = "tenant-waiting",
                ParentExecutionId = "parent-execution-waiting",
                ParentCallSiteId = "child-call-site",
                ChildDagId = "child-pipeline",
                ChildDagDefinitionVersion = "v1",
                FrozenChildDagDefinition = AiStoredPayload.Inline(
                    "{}",
                    contentHash: "definition-digest"),
                CanonicalLogicalInvocationKey = "portfolio-42|MSFT|analysis",
                ChildInvocationKey = "child-invocation-key",
                InvocationGeneration = 0,
                FrozenInvocationInput = AiStoredPayload.Inline(
                    "{}",
                    contentHash: "input-digest"),
                DelegationPolicyBindingSnapshot = AiStoredPayload.Inline(
                    "{}",
                    contentHash: "policy-binding-digest"),
                DelegationPolicyDecisionSnapshot = AiStoredPayload.Inline(
                    "{}",
                    contentHash: "policy-decision-digest"),
                Status = status,
                ChildExecutionId = "child-execution-1",
                ChildAllocatedAtUtc = now,
                CreatedAtUtc = now
            };
        }

        private sealed class InMemoryRelationStore : IAiChildExecutionRelationStore
        {
            private readonly object sync = new();
            private AiChildExecutionRelation relation;

            public InMemoryRelationStore(
                AiChildExecutionRelation relation)
            {
                this.relation = Clone(relation);
            }

            public int SuccessfulReplaceCount { get; private set; }

            public Task<AiChildExecutionRelation?> GetAsync(
                AiChildInvocationIdentity identity,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                lock (sync)
                {
                    return Task.FromResult<AiChildExecutionRelation?>(Clone(relation));
                }
            }

            public Task<AiChildExecutionRelation?> GetByChildExecutionIdAsync(
                string childExecutionId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (sync)
                {
                    return Task.FromResult<AiChildExecutionRelation?>(
                        string.Equals(relation.ChildExecutionId, childExecutionId, StringComparison.Ordinal)
                            ? Clone(relation)
                            : null);
                }
            }

            public Task<IReadOnlyList<AiChildExecutionRelation>> ListIncompleteAsync(
                int maxCount,
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<AiChildExecutionRelation>>(Array.Empty<AiChildExecutionRelation>());

            public Task<IReadOnlyList<AiChildExecutionRelation>> ListContinuationCandidatesAsync(
                int maxCount,
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<AiChildExecutionRelation>>(Array.Empty<AiChildExecutionRelation>());

            public Task<IReadOnlyList<AiChildExecutionRelation>> ListParkConsistencyCandidatesAsync(
                DateTimeOffset allocatedBeforeUtc,
                int maxCount,
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<AiChildExecutionRelation>>(Array.Empty<AiChildExecutionRelation>());

            public Task<AiChildExecutionRelation> GetOrCreateAsync(
                AiChildExecutionRelation relation,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<bool> TryReplaceAsync(
                AiChildExecutionRelation replacement,
                AiChildExecutionRelationStatus expectedStatus,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                lock (sync)
                {
                    if (relation.Status != expectedStatus)
                    {
                        return Task.FromResult(false);
                    }

                    relation = Clone(replacement);
                    SuccessfulReplaceCount++;
                    return Task.FromResult(true);
                }
            }

            public Task<bool> TryReplaceContinuationAsync(
                AiChildExecutionRelation replacement,
                AiChildContinuationStatus expectedContinuationStatus,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (sync)
                {
                    if (relation.ContinuationStatus != expectedContinuationStatus)
                    {
                        return Task.FromResult(false);
                    }

                    relation = Clone(replacement);
                    return Task.FromResult(true);
                }
            }

            private static AiChildExecutionRelation Clone(
                AiChildExecutionRelation source)
            {
                return new AiChildExecutionRelation
                {
                    TenantId = source.TenantId,
                    ParentExecutionId = source.ParentExecutionId,
                    ParentCallSiteId = source.ParentCallSiteId,
                    ChildDagId = source.ChildDagId,
                    ChildDagDefinitionVersion = source.ChildDagDefinitionVersion,
                    FrozenChildDagDefinition = source.FrozenChildDagDefinition,
                    CanonicalLogicalInvocationKey = source.CanonicalLogicalInvocationKey,
                    ChildInvocationKey = source.ChildInvocationKey,
                    InvocationGeneration = source.InvocationGeneration,
                    FrozenInvocationInput = source.FrozenInvocationInput,
                    DelegatedExecutionContextSnapshot = source.DelegatedExecutionContextSnapshot,
                    DelegatedMetadata = source.DelegatedMetadata,
                    DelegationPolicyBindingSnapshot = source.DelegationPolicyBindingSnapshot,
                    DelegationPolicyDecisionSnapshot = source.DelegationPolicyDecisionSnapshot,
                    Status = source.Status,
                    ChildExecutionId = source.ChildExecutionId,
                    ChildResult = source.ChildResult,
                    ChildFailureReason = source.ChildFailureReason,
                    ContinuationStatus = source.ContinuationStatus,
                    CreatedAtUtc = source.CreatedAtUtc,
                    DelegationEvaluatedAtUtc = source.DelegationEvaluatedAtUtc,
                    ChildAllocatedAtUtc = source.ChildAllocatedAtUtc,
                    WaitingAtUtc = source.WaitingAtUtc,
                    CompletedAtUtc = source.CompletedAtUtc,
                    ParentContinuationScheduledAtUtc = source.ParentContinuationScheduledAtUtc,
                    ParentContinuationScheduledStepVersion = source.ParentContinuationScheduledStepVersion,
                    ParentResumedAtUtc = source.ParentResumedAtUtc
                };
            }
        }
    }
}
