using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Invocation.Mcp.Durable;

namespace Multiplexed.AI.Runtime.Invocation.Mcp.Durable.Mongo
{
    /// <summary>
    /// Single-document authoritative MCP effect evidence store. It reuses the host Mongo
    /// lifetime and majority/journaled persistence. It never invokes tools or mutates DAGs.
    /// </summary>
    public sealed class MongoAiMcpEffectEvidenceStore : IAiMcpEffectEvidenceStore
    {
        private static readonly Collation Ordinal = new("simple");
        private readonly IMongoCollection<BsonDocument> _collection;
        private readonly SemaphoreSlim _indexLock = new(1, 1);
        private volatile bool _indexesReady;

        public MongoAiMcpEffectEvidenceStore(
            IMongoDatabase database,
            IOptions<AiMcpEffectEvidenceMongoOptions> options)
        {
            ArgumentNullException.ThrowIfNull(database);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Value.CollectionName);
            _collection = database.GetCollection<BsonDocument>(options.Value.CollectionName)
                .WithReadPreference(ReadPreference.Primary)
                .WithReadConcern(ReadConcern.Majority)
                .WithWriteConcern(WriteConcern.WMajority.With(journal: true));
        }

        public async Task<AiMcpEffectEvidenceRecord?> GetAsync(
            AiMcpEffectEvidenceScope scope,
            string effectId,
            CancellationToken cancellationToken = default)
        {
            AiMcpEffectEvidenceValidation.ValidateAddress(scope, effectId);
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
            var document = await _collection.Find(
                    AiMcpEffectEvidenceMongoCodec.IdentityFilter(scope, effectId),
                    new FindOptions { Collation = Ordinal })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return document is null ? null : AiMcpEffectEvidenceMongoCodec.Decode(document);
        }

        public async Task<AiMcpEffectEvidenceRecord> GetOrCreateAsync(
            AiMcpEffectEvidenceRecord prepared,
            CancellationToken cancellationToken = default)
        {
            AiMcpEffectEvidenceValidation.ValidateRecord(prepared);
            AiMcpEffectEvidenceValidation.Require(prepared.Status == AiMcpEffectEvidenceStatus.Prepared,
                "Only Prepared MCP effect evidence can be inserted.");
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _collection.InsertOneAsync(
                    AiMcpEffectEvidenceMongoCodec.Encode(prepared),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return prepared;
            }
            catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    var existing = await ReadWithoutIndexSetupAsync(
                        prepared.Scope,
                        prepared.Intent.Effect.EffectId,
                        cancellationToken).ConfigureAwait(false);
                    if (existing is not null)
                    {
                        if (!AiMcpEffectEvidenceValidation.EquivalentIntent(existing.Intent, prepared.Intent))
                            throw new InvalidOperationException(
                                "MCP effect identity already exists with conflicting frozen intent.", exception);
                        return existing;
                    }
                    if (attempt < 7)
                    {
                        await Task.Delay(
                            TimeSpan.FromMilliseconds(Math.Min(25 * (1 << attempt), 250)),
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                throw new IOException(
                    "Duplicate MCP effect identity is not yet authoritatively readable; reconcile the same effect before dispatch.",
                    exception);
            }
        }

        public async Task<bool> TryReplaceAsync(
            AiMcpEffectEvidenceRecord expected,
            AiMcpEffectEvidenceRecord replacement,
            CancellationToken cancellationToken = default)
        {
            AiMcpEffectEvidenceValidation.ValidateTransition(expected, replacement);
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
            var result = await _collection.ReplaceOneAsync(
                AiMcpEffectEvidenceMongoCodec.ReplacementFilter(expected),
                AiMcpEffectEvidenceMongoCodec.Encode(replacement),
                new ReplaceOptions { IsUpsert = false, Collation = Ordinal },
                cancellationToken).ConfigureAwait(false);
            if (!result.IsAcknowledged)
                throw new InvalidOperationException("MCP effect evidence replacement was not acknowledged.");
            return result.ModifiedCount == 1;
        }

        public async Task<IReadOnlyList<AiMcpEffectEvidenceRecord>> ListReconciliationCandidatesAsync(
            AiMcpEffectEvidenceScope scope,
            DateTimeOffset dispatchStartedBeforeUtc,
            int maxCount,
            CancellationToken cancellationToken = default)
        {
            AiMcpEffectEvidenceValidation.ValidateScope(scope);
            if (maxCount is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maxCount));
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
            var filter = new BsonDocument
            {
                { "tenantId", scope.TenantId },
                { "tenantGroupId", scope.TenantGroupId },
                { "$or", new BsonArray
                    {
                        new BsonDocument("status", (int)AiMcpEffectEvidenceStatus.Uncertain),
                        new BsonDocument
                        {
                            { "status", (int)AiMcpEffectEvidenceStatus.Dispatching },
                            { "attemptStartedAt", new BsonDocument("$lte", dispatchStartedBeforeUtc.ToUnixTimeMilliseconds()) }
                        }
                    }
                }
            };
            var documents = await _collection.Find(filter, new FindOptions { Collation = Ordinal })
                .Sort(new BsonDocument { { "updatedAt", 1 }, { "_id", 1 } })
                .Limit(maxCount)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return documents.Select(AiMcpEffectEvidenceMongoCodec.Decode).ToArray();
        }

        private async Task<AiMcpEffectEvidenceRecord?> ReadWithoutIndexSetupAsync(
            AiMcpEffectEvidenceScope scope,
            string effectId,
            CancellationToken cancellationToken)
        {
            var document = await _collection.Find(
                    AiMcpEffectEvidenceMongoCodec.IdentityFilter(scope, effectId),
                    new FindOptions { Collation = Ordinal })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return document is null ? null : AiMcpEffectEvidenceMongoCodec.Decode(document);
        }

        private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_indexesReady) return;
            await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_indexesReady) return;
                var identity = new BsonDocument
                {
                    { "tenantId", 1 }, { "tenantGroupId", 1 }, { "effectId", 1 }
                };
                var reconciliation = new BsonDocument
                {
                    { "tenantId", 1 }, { "tenantGroupId", 1 }, { "status", 1 },
                    { "attemptStartedAt", 1 }, { "updatedAt", 1 }
                };
                await _collection.Indexes.CreateManyAsync(new[]
                {
                    new CreateIndexModel<BsonDocument>(identity,
                        new CreateIndexOptions
                        {
                            Name = "uq_mcp_effect_identity",
                            Unique = true,
                            Collation = Ordinal
                        }),
                    new CreateIndexModel<BsonDocument>(reconciliation,
                        new CreateIndexOptions
                        {
                            Name = "ix_mcp_effect_reconciliation",
                            Collation = Ordinal
                        })
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
                _indexesReady = true;
            }
            finally
            {
                _indexLock.Release();
            }
        }
    }
}
