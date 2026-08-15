using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations.Persistence;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Identity;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support
{
    /// <summary>
    /// Provides a deterministic in-memory child relation store for composition unit tests.
    /// </summary>
    internal sealed class InMemoryAiChildExecutionRelationStore : IAiChildExecutionRelationStore
    {
        private readonly object gate = new();
        private readonly Dictionary<string, AiChildExecutionRelation> relations = new(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a new empty test relation store.
        /// </summary>
        public InMemoryAiChildExecutionRelationStore()
        {
        }

        /// <summary>
        /// Initializes the store with one or more authoritative test relations.
        /// </summary>
        /// <param name="relations">The relations to seed.</param>
        public InMemoryAiChildExecutionRelationStore(params AiChildExecutionRelation[] relations)
        {
            ArgumentNullException.ThrowIfNull(relations);

            foreach (var relation in relations)
            {
                Seed(relation);
            }
        }

        /// <summary>
        /// Seeds or replaces one relation for test arrangement only.
        /// </summary>
        /// <param name="relation">The relation to store.</param>
        public void Seed(AiChildExecutionRelation relation)
        {
            ArgumentNullException.ThrowIfNull(relation);

            lock (this.gate)
            {
                this.relations[GetIdentityKey(relation.ToInvocationIdentity())] = Clone(relation);
            }
        }

        /// <inheritdoc />
        public Task<AiChildExecutionRelation?> GetAsync(
            AiChildInvocationIdentity identity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = GetIdentityKey(identity);

            lock (this.gate)
            {
                return Task.FromResult(
                    this.relations.TryGetValue(key, out var relation)
                        ? Clone(relation)
                        : null);
            }
        }

        /// <inheritdoc />
        public Task<AiChildExecutionRelation?> GetByChildExecutionIdAsync(
            string childExecutionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(childExecutionId);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.gate)
            {
                var relation = this.relations.Values.FirstOrDefault(item =>
                    string.Equals(item.ChildExecutionId, childExecutionId, StringComparison.Ordinal));

                return Task.FromResult(relation is null ? null : Clone(relation));
            }
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiChildExecutionRelation>> ListIncompleteAsync(
            int maxCount,
            CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.gate)
            {
                return Task.FromResult<IReadOnlyList<AiChildExecutionRelation>>(
                    this.relations.Values
                        .Where(item => item.Status is AiChildExecutionRelationStatus.ChildAllocated or AiChildExecutionRelationStatus.Waiting)
                        .OrderBy(item => item.ChildAllocatedAtUtc)
                        .ThenBy(item => item.CreatedAtUtc)
                        .Take(maxCount)
                        .Select(Clone)
                        .ToArray());
            }
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiChildExecutionRelation>> ListContinuationCandidatesAsync(
            int maxCount,
            CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.gate)
            {
                return Task.FromResult<IReadOnlyList<AiChildExecutionRelation>>(
                    this.relations.Values
                        .Where(item =>
                            item.Status == AiChildExecutionRelationStatus.Completed &&
                            item.ContinuationStatus is AiChildContinuationStatus.Pending or AiChildContinuationStatus.Scheduled)
                        .OrderBy(item => item.CompletedAtUtc)
                        .ThenBy(item => item.CreatedAtUtc)
                        .Take(maxCount)
                        .Select(Clone)
                        .ToArray());
            }
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiChildExecutionRelation>> ListParkConsistencyCandidatesAsync(
            DateTimeOffset allocatedBeforeUtc,
            int maxCount,
            CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.gate)
            {
                return Task.FromResult<IReadOnlyList<AiChildExecutionRelation>>(
                    this.relations.Values
                        .Where(item =>
                            item.Status == AiChildExecutionRelationStatus.ChildAllocated &&
                            item.ChildAllocatedAtUtc <= allocatedBeforeUtc)
                        .OrderBy(item => item.ChildAllocatedAtUtc)
                        .ThenBy(item => item.CreatedAtUtc)
                        .Take(maxCount)
                        .Select(Clone)
                        .ToArray());
            }
        }

        /// <inheritdoc />
        public Task<AiChildExecutionRelation> GetOrCreateAsync(
            AiChildExecutionRelation relation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(relation);
            cancellationToken.ThrowIfCancellationRequested();
            var key = GetIdentityKey(relation.ToInvocationIdentity());

            lock (this.gate)
            {
                if (this.relations.TryGetValue(key, out var existing))
                {
                    return Task.FromResult(Clone(existing));
                }

                var stored = Clone(relation);
                this.relations[key] = stored;
                return Task.FromResult(Clone(stored));
            }
        }

        /// <inheritdoc />
        public Task<bool> TryReplaceAsync(
            AiChildExecutionRelation relation,
            AiChildExecutionRelationStatus expectedStatus,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(relation);
            cancellationToken.ThrowIfCancellationRequested();
            var key = GetIdentityKey(relation.ToInvocationIdentity());

            lock (this.gate)
            {
                if (!this.relations.TryGetValue(key, out var existing) || existing.Status != expectedStatus)
                {
                    return Task.FromResult(false);
                }

                this.relations[key] = Clone(relation);
                return Task.FromResult(true);
            }
        }

        /// <inheritdoc />
        public Task<bool> TryReplaceContinuationAsync(
            AiChildExecutionRelation relation,
            AiChildContinuationStatus expectedContinuationStatus,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(relation);
            cancellationToken.ThrowIfCancellationRequested();
            var key = GetIdentityKey(relation.ToInvocationIdentity());

            lock (this.gate)
            {
                if (!this.relations.TryGetValue(key, out var existing) ||
                    existing.Status != AiChildExecutionRelationStatus.Completed ||
                    existing.ContinuationStatus != expectedContinuationStatus)
                {
                    return Task.FromResult(false);
                }

                this.relations[key] = Clone(relation);
                return Task.FromResult(true);
            }
        }

        private static string GetIdentityKey(AiChildInvocationIdentity identity)
        {
            return AiChildInvocationKeyFactory.Create(identity);
        }

        private static AiChildExecutionRelation Clone(AiChildExecutionRelation relation)
        {
            return JsonSerializer.Deserialize<AiChildExecutionRelation>(
                       JsonSerializer.Serialize(relation))
                   ?? throw new InvalidOperationException("Child relation test clone could not be deserialized.");
        }
    }
}
