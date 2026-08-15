using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations.Persistence;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Identity;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Persistence.Mongo.Documents;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Snapshots;
using Multiplexed.AI.Runtime.Execution.Payloads.Serialization;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Persistence.Mongo
{
    /// <summary>
    /// MongoDB-backed authoritative store for parent-to-child DAG execution relations.
    /// </summary>
    /// <remarks>
    /// The typed invocation tuple is enforced by a unique compound MongoDB index. The derived invocation key
    /// is indexed only for lookup and integrity checks and is intentionally not the database uniqueness authority.
    /// </remarks>
    public sealed class MongoAiChildExecutionRelationStore : IAiChildExecutionRelationStore
    {
        private readonly IMongoCollection<MongoAiChildExecutionRelationDocument> collection;
        private readonly AiChildExecutionRelationMongoOptions options;
        private readonly SemaphoreSlim indexInitializationLock = new(1, 1);
        private bool indexesInitialized;

        /// <summary>
        /// Initializes a new instance of the <see cref="MongoAiChildExecutionRelationStore"/> class.
        /// </summary>
        /// <param name="database">The MongoDB database that owns the authoritative relation collection.</param>
        /// <param name="options">The relation MongoDB options.</param>
        public MongoAiChildExecutionRelationStore(
            IMongoDatabase database,
            IOptions<AiChildExecutionRelationMongoOptions> options)
        {
            ArgumentNullException.ThrowIfNull(database);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Value.CollectionName);

            this.options = options.Value;
            this.collection = database.GetCollection<MongoAiChildExecutionRelationDocument>(this.options.CollectionName);
        }

        /// <inheritdoc />
        public async Task<AiChildExecutionRelation?> GetAsync(
            AiChildInvocationIdentity identity,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(identity);
            ValidateIdentity(identity);
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var document = await this.collection
                .Find(BuildIdentityFilter(identity))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            return document?.Relation;
        }

        /// <inheritdoc />
        public async Task<AiChildExecutionRelation> GetOrCreateAsync(
            AiChildExecutionRelation relation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(relation);
            ValidateInitialRelation(relation);
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await this.collection
                    .InsertOneAsync(
                        new MongoAiChildExecutionRelationDocument
                        {
                            Relation = relation
                        },
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                return relation;
            }
            catch (MongoException exception) when (IsDuplicateKey(exception))
            {
                var existingDocument = await this.collection
                    .Find(BuildIdentityFilter(relation.ToInvocationIdentity()))
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                var existing = existingDocument?.Relation;
                if (existing is not null && AreCreationEquivalent(existing, relation))
                {
                    return existing;
                }

                throw new InvalidOperationException(
                    $"Child execution relation for invocation '{relation.ChildInvocationKey}' conflicts with already committed durable creation data.",
                    exception);
            }
        }

        /// <inheritdoc />
        public async Task<bool> TryReplaceAsync(
            AiChildExecutionRelation relation,
            AiChildExecutionRelationStatus expectedStatus,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(relation);
            ValidateDurableRelation(relation);

            if (relation.Status == expectedStatus)
            {
                throw new ArgumentException(
                    "Replacement relation status must differ from the expected compare-and-swap status.",
                    nameof(relation));
            }

            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var filter = Builders<MongoAiChildExecutionRelationDocument>.Filter.And(
                BuildIdentityFilter(relation.ToInvocationIdentity()),
                Builders<MongoAiChildExecutionRelationDocument>.Filter.Eq(
                    item => item.Relation.Status,
                    expectedStatus));

            var update = Builders<MongoAiChildExecutionRelationDocument>.Update
                .Set(item => item.Relation, relation);

            var result = await this.collection
                .UpdateOneAsync(
                    filter,
                    update,
                    new UpdateOptions { IsUpsert = false },
                    cancellationToken)
                .ConfigureAwait(false);

            return result.ModifiedCount == 1;
        }

        /// <summary>
        /// Builds the MongoDB filter for the authoritative typed invocation identity tuple.
        /// </summary>
        /// <param name="identity">The typed invocation identity.</param>
        /// <returns>The MongoDB identity filter.</returns>
        private static FilterDefinition<MongoAiChildExecutionRelationDocument> BuildIdentityFilter(
            AiChildInvocationIdentity identity)
        {
            var filters = Builders<MongoAiChildExecutionRelationDocument>.Filter;
            return filters.And(
                filters.Eq(item => item.Relation.TenantId, identity.TenantId),
                filters.Eq(item => item.Relation.ParentExecutionId, identity.ParentExecutionId),
                filters.Eq(item => item.Relation.ParentCallSiteId, identity.ParentCallSiteId),
                filters.Eq(item => item.Relation.ChildDagId, identity.ChildDagId),
                filters.Eq(item => item.Relation.ChildDagDefinitionVersion, identity.ChildDagDefinitionVersion),
                filters.Eq(item => item.Relation.CanonicalLogicalInvocationKey, identity.CanonicalLogicalInvocationKey),
                filters.Eq(item => item.Relation.InvocationGeneration, identity.InvocationGeneration));
        }

        /// <summary>
        /// Validates the stricter invariants required for a first durable relation write.
        /// </summary>
        /// <param name="relation">The candidate initial relation.</param>
        private static void ValidateInitialRelation(AiChildExecutionRelation relation)
        {
            ValidateDurableRelation(relation);

            if (relation.Status != AiChildExecutionRelationStatus.DelegationPolicyPending)
            {
                throw new InvalidOperationException(
                    "A newly created child execution relation must start in DelegationPolicyPending status.");
            }

            if (relation.ContinuationStatus != AiChildContinuationStatus.None ||
                relation.ChildExecutionId is not null ||
                relation.ChildResult is not null ||
                relation.ChildFailureReason is not null)
            {
                throw new InvalidOperationException(
                    "A newly created child execution relation cannot contain child allocation, child outcome, or continuation state.");
            }
        }

        /// <summary>
        /// Validates invariants that every persisted relation state must preserve.
        /// </summary>
        /// <param name="relation">The relation to validate.</param>
        private static void ValidateDurableRelation(AiChildExecutionRelation relation)
        {
            var identity = relation.ToInvocationIdentity();
            ValidateIdentity(identity);

            var expectedInvocationKey = AiChildInvocationKeyFactory.Create(identity);
            if (!string.Equals(expectedInvocationKey, relation.ChildInvocationKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Child execution relation invocation key does not match its authoritative typed invocation identity.");
            }

            ValidateSnapshot(relation.FrozenChildDagDefinition, nameof(relation.FrozenChildDagDefinition));
            ValidateSnapshot(relation.FrozenInvocationInput, nameof(relation.FrozenInvocationInput));
            ValidateSnapshot(relation.DelegationPolicyBindingSnapshot, nameof(relation.DelegationPolicyBindingSnapshot));
            ValidateDelegationDecisionState(relation);
            ValidateChildAllocationState(relation);

            if (relation.CreatedAtUtc == default)
            {
                throw new InvalidOperationException(
                    "Child execution relation creation timestamp must be set before persistence.");
            }
        }

        /// <summary>
        /// Validates durable delegation decision invariants without introducing child allocation semantics prematurely.
        /// </summary>
        /// <param name="relation">The relation to validate.</param>
        private static void ValidateDelegationDecisionState(AiChildExecutionRelation relation)
        {
            if (relation.Status == AiChildExecutionRelationStatus.DelegationPolicyPending)
            {
                if (relation.DelegationPolicyDecisionSnapshot is not null ||
                    relation.DelegationEvaluatedAtUtc is not null)
                {
                    throw new InvalidOperationException(
                        "A delegation-pending child relation cannot contain a committed policy decision snapshot or evaluation timestamp.");
                }

                return;
            }

            if (relation.DelegationPolicyDecisionSnapshot is null ||
                relation.DelegationEvaluatedAtUtc is null)
            {
                throw new InvalidOperationException(
                    "A child relation that progressed beyond delegation policy pending must preserve its durable policy decision snapshot and evaluation timestamp.");
            }

            ValidateSnapshot(
                relation.DelegationPolicyDecisionSnapshot,
                nameof(relation.DelegationPolicyDecisionSnapshot));
        }

        /// <summary>
        /// Validates the exact child execution allocation invariants for each durable relation state.
        /// </summary>
        /// <param name="relation">The relation to validate.</param>
        private static void ValidateChildAllocationState(AiChildExecutionRelation relation)
        {
            if (relation.Status is AiChildExecutionRelationStatus.DelegationPolicyPending or
                AiChildExecutionRelationStatus.DelegationDenied or
                AiChildExecutionRelationStatus.DelegationApproved)
            {
                if (relation.ChildExecutionId is not null || relation.ChildAllocatedAtUtc is not null)
                {
                    throw new InvalidOperationException(
                        $"Child relation status '{relation.Status}' cannot contain child execution allocation data.");
                }

                if (relation.WaitingAtUtc is not null)
                {
                    throw new InvalidOperationException(
                        $"Child relation status '{relation.Status}' cannot contain a waiting timestamp.");
                }

                return;
            }

            if (string.IsNullOrWhiteSpace(relation.ChildExecutionId) || relation.ChildAllocatedAtUtc is null)
            {
                throw new InvalidOperationException(
                    $"Child relation status '{relation.Status}' requires one durably allocated child execution identifier.");
            }

            if (relation.Status == AiChildExecutionRelationStatus.ChildAllocated && relation.WaitingAtUtc is not null)
            {
                throw new InvalidOperationException(
                    "A child-allocated relation cannot contain a waiting timestamp before entering the Waiting state.");
            }

            if (relation.Status == AiChildExecutionRelationStatus.Waiting && relation.WaitingAtUtc is null)
            {
                throw new InvalidOperationException(
                    "A waiting child relation must preserve its durable waiting timestamp.");
            }
        }

        /// <summary>
        /// Validates the complete typed invocation identity using the deterministic key factory rules.
        /// </summary>
        /// <param name="identity">The identity to validate.</param>
        private static void ValidateIdentity(AiChildInvocationIdentity identity)
        {
            _ = AiChildInvocationKeyFactory.Create(identity);
        }

        /// <summary>
        /// Validates that an immutable relation snapshot is complete enough for durable recovery.
        /// </summary>
        /// <param name="snapshot">The snapshot descriptor.</param>
        /// <param name="propertyName">The relation property name used in diagnostics.</param>
        private static void ValidateSnapshot(AiStoredPayload snapshot, string propertyName)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            if (string.IsNullOrWhiteSpace(snapshot.ContentHash))
            {
                throw new InvalidOperationException(
                    $"Immutable child relation snapshot '{propertyName}' must carry a stable content hash.");
            }

            if (snapshot.IsInline)
            {
                if (snapshot.InlineValue is not string inlineContent)
                {
                    throw new InvalidOperationException(
                        $"Immutable inline child relation snapshot '{propertyName}' must contain canonical serialized JSON text.");
                }

                var canonicalContent = AiCanonicalJson.Canonicalize(inlineContent);
                var actualHash = AiCanonicalJson.ComputeSha256(canonicalContent);
                if (!string.Equals(snapshot.ContentHash, actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Immutable inline child relation snapshot '{propertyName}' does not match its content hash.");
                }

                return;
            }

            if (string.IsNullOrWhiteSpace(snapshot.ArtifactId))
            {
                throw new InvalidOperationException(
                    $"Immutable artifact-backed child relation snapshot '{propertyName}' must carry an artifact id.");
            }
        }

        /// <summary>
        /// Determines whether two relation candidates carry the same immutable creation data.
        /// </summary>
        /// <param name="existing">The already committed relation.</param>
        /// <param name="candidate">The duplicate creation candidate.</param>
        /// <returns><see langword="true"/> when immutable creation data is equivalent.</returns>
        private static bool AreCreationEquivalent(
            AiChildExecutionRelation existing,
            AiChildExecutionRelation candidate)
        {
            return string.Equals(existing.ChildInvocationKey, candidate.ChildInvocationKey, StringComparison.Ordinal) &&
                   SnapshotEquals(existing.FrozenChildDagDefinition, candidate.FrozenChildDagDefinition) &&
                   SnapshotEquals(existing.FrozenInvocationInput, candidate.FrozenInvocationInput) &&
                   SnapshotEquals(existing.DelegationPolicyBindingSnapshot, candidate.DelegationPolicyBindingSnapshot) &&
                   string.Equals(
                       AiCanonicalJson.Serialize(existing.DelegatedExecutionContextSnapshot),
                       AiCanonicalJson.Serialize(candidate.DelegatedExecutionContextSnapshot),
                       StringComparison.Ordinal) &&
                   string.Equals(
                       AiCanonicalJson.Serialize(existing.DelegatedMetadata),
                       AiCanonicalJson.Serialize(candidate.DelegatedMetadata),
                       StringComparison.Ordinal);
        }

        /// <summary>
        /// Compares two immutable stored-payload descriptors for creation-time equivalence.
        /// </summary>
        /// <param name="left">The first snapshot.</param>
        /// <param name="right">The second snapshot.</param>
        /// <returns><see langword="true"/> when the descriptors are equivalent.</returns>
        private static bool SnapshotEquals(AiStoredPayload left, AiStoredPayload right)
        {
            return left.IsInline == right.IsInline &&
                   string.Equals(left.ContentHash, right.ContentHash, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(left.ArtifactId, right.ArtifactId, StringComparison.Ordinal) &&
                   string.Equals(left.ContentType, right.ContentType, StringComparison.OrdinalIgnoreCase) &&
                   left.SizeBytes == right.SizeBytes &&
                   string.Equals(left.InlineValue as string, right.InlineValue as string, StringComparison.Ordinal);
        }

        /// <summary>
        /// Ensures the relation uniqueness and lookup indexes required by this store.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
        {
            if (this.indexesInitialized || !this.options.EnsureIndexes)
            {
                return;
            }

            await this.indexInitializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (this.indexesInitialized)
                {
                    return;
                }

                var indexModels = new[]
                {
                    new CreateIndexModel<MongoAiChildExecutionRelationDocument>(
                        Builders<MongoAiChildExecutionRelationDocument>.IndexKeys
                            .Ascending(item => item.Relation.TenantId)
                            .Ascending(item => item.Relation.ParentExecutionId)
                            .Ascending(item => item.Relation.ParentCallSiteId)
                            .Ascending(item => item.Relation.ChildDagId)
                            .Ascending(item => item.Relation.ChildDagDefinitionVersion)
                            .Ascending(item => item.Relation.CanonicalLogicalInvocationKey)
                            .Ascending(item => item.Relation.InvocationGeneration),
                        new CreateIndexOptions
                        {
                            Name = "ux_child_relation_typed_invocation",
                            Unique = true
                        }),
                    new CreateIndexModel<MongoAiChildExecutionRelationDocument>(
                        Builders<MongoAiChildExecutionRelationDocument>.IndexKeys
                            .Ascending(item => item.Relation.ChildInvocationKey),
                        new CreateIndexOptions
                        {
                            Name = "ix_child_relation_invocation_key"
                        }),
                    new CreateIndexModel<MongoAiChildExecutionRelationDocument>(
                        Builders<MongoAiChildExecutionRelationDocument>.IndexKeys
                            .Ascending(item => item.Relation.ChildExecutionId),
                        new CreateIndexOptions
                        {
                            Name = "ix_child_relation_child_execution_id",
                            Sparse = true
                        })
                };

                await this.collection.Indexes
                    .CreateManyAsync(indexModels, cancellationToken)
                    .ConfigureAwait(false);

                this.indexesInitialized = true;
            }
            finally
            {
                this.indexInitializationLock.Release();
            }
        }

        /// <summary>
        /// Determines whether a MongoDB exception represents a duplicate-key conflict.
        /// </summary>
        /// <param name="exception">The MongoDB exception.</param>
        /// <returns><see langword="true"/> when the exception is a duplicate-key error.</returns>
        private static bool IsDuplicateKey(MongoException exception)
        {
            if (exception is MongoWriteException writeException)
            {
                return writeException.WriteError?.Category == ServerErrorCategory.DuplicateKey ||
                       writeException.WriteError?.Code == 11000 ||
                       writeException.Message.Contains("E11000", StringComparison.OrdinalIgnoreCase);
            }

            if (exception is MongoCommandException commandException)
            {
                return commandException.Code == 11000 ||
                       string.Equals(commandException.CodeName, "DuplicateKey", StringComparison.OrdinalIgnoreCase) ||
                       commandException.Message.Contains("E11000", StringComparison.OrdinalIgnoreCase);
            }

            return exception.Message.Contains("E11000", StringComparison.OrdinalIgnoreCase);
        }
    }
}
