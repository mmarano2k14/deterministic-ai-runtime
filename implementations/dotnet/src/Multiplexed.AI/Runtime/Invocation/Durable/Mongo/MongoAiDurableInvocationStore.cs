using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Invocation.Durable;

namespace Multiplexed.AI.Runtime.Invocation.Durable.Mongo
{
    /// <summary>
    /// Single-document durable CAS store. Uses the existing Mongo database/client,
    /// majority journaled writes and primary majority reads; no cross-store transaction,
    /// hosted service, worker dispatch, TTL deletion or implicit registration is added.
    /// </summary>
    public sealed class MongoAiDurableInvocationStore : IAiDurableInvocationStore
    {
        private readonly IMongoCollection<BsonDocument> _collection;
        private readonly SemaphoreSlim _indexLock = new(1, 1);
        private volatile bool _indexesReady;
        private static readonly Collation Ordinal = new("simple");

        public MongoAiDurableInvocationStore(IMongoDatabase database, IOptions<AiDurableInvocationMongoOptions> options)
        {
            ArgumentNullException.ThrowIfNull(database);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Value.CollectionName);
            _collection = database.GetCollection<BsonDocument>(options.Value.CollectionName)
                .WithReadPreference(ReadPreference.Primary)
                .WithReadConcern(ReadConcern.Majority)
                .WithWriteConcern(WriteConcern.WMajority.With(journal: true));
        }

        /// <inheritdoc />
        public async Task<AiDurableInvocationRecord?> GetAsync(
            AiDurableInvocationScope scope, AiDurableInvocationIdentity identity, CancellationToken cancellationToken = default)
        {
            AiDurableInvocationValidation.ValidateAddress(scope, identity);
            var filter = AiDurableInvocationMongoCodec.IdentityFilter(identity);
            filter.Add("tenantGroupId", scope.TenantGroupId);
            filter.Add("controlPlaneId", scope.ControlPlaneId);
            return await ReadAsync(filter, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<AiDurableInvocationRecord> GetOrCreateAsync(
            AiDurableInvocationRecord prepared, CancellationToken cancellationToken = default)
        {
            AiDurableInvocationValidation.ValidateRecord(prepared);
            AiDurableInvocationValidation.Require(prepared.Status == AiDurableInvocationStatus.Prepared,
                "Only initial preparation can be inserted.");
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _collection.InsertOneAsync(AiDurableInvocationMongoCodec.Encode(prepared), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return prepared;
            }
            catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                // Read by the full typed identity, without a supplied scope that could hide
                // conflicting ownership. This internal check never returns foreign content.
                // A competing insert can win uniqueness before it is majority-readable.
                // Reconcile that visibility window; do not misreport it as a content conflict.
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    var existing = await ReadAsync(AiDurableInvocationMongoCodec.IdentityFilter(prepared.Definition.Identity), cancellationToken)
                        .ConfigureAwait(false);
                    if (existing is not null)
                    {
                        if (existing.Definition != prepared.Definition)
                            throw new InvalidOperationException("Invocation identity already exists with conflicting frozen preparation.", exception);
                        return existing;
                    }
                    if (attempt < 7)
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(25 * (1 << attempt), 250)), cancellationToken)
                            .ConfigureAwait(false);
                }
                throw new IOException("Duplicate invocation identity is not yet authoritatively readable; reconcile the same identity before dispatch.", exception);
            }
        }

        /// <inheritdoc />
        public async Task<bool> TryReplaceAsync(
            AiDurableInvocationRecord expected, AiDurableInvocationRecord replacement, CancellationToken cancellationToken = default)
        {
            AiDurableInvocationValidation.ValidateTransition(expected, replacement);
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
            var result = await _collection.ReplaceOneAsync(
                AiDurableInvocationMongoCodec.ReplacementFilter(expected, replacement),
                AiDurableInvocationMongoCodec.Encode(replacement),
                new ReplaceOptions { IsUpsert = false, Collation = Ordinal }, cancellationToken).ConfigureAwait(false);
            if (!result.IsAcknowledged) throw new InvalidOperationException("Invocation replacement was not acknowledged.");
            return result.ModifiedCount == 1;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiDurableInvocationRecord>> ListDispatchCandidatesAsync(
            AiDurableInvocationScope scope, string executionLanguage, DateTimeOffset nowUtc, int maxCount,
            CancellationToken cancellationToken = default)
        {
            ValidateQuery(scope, maxCount);
            AiDurableInvocationValidation.ValidateLanguage(executionLanguage);
            var filter = AiDurableInvocationMongoCodec.ScopeFilter(scope);
            filter.Add("language", executionLanguage);
            filter.Add("$or", new BsonArray
            {
                new BsonDocument("status", (int)AiDurableInvocationStatus.Prepared),
                new BsonDocument
                {
                    { "status", (int)AiDurableInvocationStatus.Leased },
                    { "leaseExpiresAt", new BsonDocument("$lte", nowUtc.ToUnixTimeMilliseconds()) }
                }
            });
            return await ListAsync(filter, maxCount, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiDurableInvocationRecord>> ListContinuationCandidatesAsync(
            AiDurableInvocationScope scope, int maxCount, CancellationToken cancellationToken = default)
        {
            ValidateQuery(scope, maxCount);
            var filter = AiDurableInvocationMongoCodec.ScopeFilter(scope);
            filter.Add("status", new BsonDocument("$in", new BsonArray
                { (int)AiDurableInvocationStatus.Succeeded, (int)AiDurableInvocationStatus.Failed }));
            filter.Add("continuationStatus", new BsonDocument("$in", new BsonArray
                { (int)AiDurableInvocationContinuationStatus.Pending, (int)AiDurableInvocationContinuationStatus.Scheduled }));
            return await ListAsync(filter, maxCount, cancellationToken).ConfigureAwait(false);
        }

        private async Task<AiDurableInvocationRecord?> ReadAsync(BsonDocument filter, CancellationToken cancellationToken)
        {
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
            var document = await _collection.Find(filter, new FindOptions { Collation = Ordinal })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return document is null ? null : AiDurableInvocationMongoCodec.Decode(document);
        }

        private async Task<IReadOnlyList<AiDurableInvocationRecord>> ListAsync(
            BsonDocument filter, int maxCount, CancellationToken cancellationToken)
        {
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
            var documents = await _collection.Find(filter, new FindOptions { Collation = Ordinal })
                .Sort(new BsonDocument { { "updatedAt", 1 }, { "_id", 1 } }).Limit(maxCount)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return documents.Select(AiDurableInvocationMongoCodec.Decode).ToArray();
        }

        private static void ValidateQuery(AiDurableInvocationScope scope, int maxCount)
        {
            AiDurableInvocationValidation.ValidateScope(scope);
            if (maxCount is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maxCount));
        }

        private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_indexesReady) return;
            await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_indexesReady) return;
                var identity = new BsonDocument { { "tenantId", 1 }, { "executionId", 1 }, { "stepName", 1 }, { "generation", 1 } };
                var ready = new BsonDocument
                {
                    { "controlPlaneId", 1 }, { "tenantId", 1 }, { "tenantGroupId", 1 },
                    { "language", 1 }, { "status", 1 }, { "leaseExpiresAt", 1 }, { "updatedAt", 1 }
                };
                var continuation = new BsonDocument
                {
                    { "controlPlaneId", 1 }, { "tenantId", 1 }, { "tenantGroupId", 1 },
                    { "continuationStatus", 1 }, { "updatedAt", 1 }
                };
                await _collection.Indexes.CreateManyAsync(new[]
                {
                    new CreateIndexModel<BsonDocument>(identity,
                        new CreateIndexOptions { Name = "uq_durable_invocation_identity", Unique = true, Collation = Ordinal }),
                    new CreateIndexModel<BsonDocument>(ready,
                        new CreateIndexOptions { Name = "ix_durable_invocation_dispatch", Collation = Ordinal }),
                    new CreateIndexModel<BsonDocument>(continuation,
                        new CreateIndexOptions { Name = "ix_durable_invocation_continuation", Collation = Ordinal })
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
                _indexesReady = true;
            }
            finally { _indexLock.Release(); }
        }
    }
}
