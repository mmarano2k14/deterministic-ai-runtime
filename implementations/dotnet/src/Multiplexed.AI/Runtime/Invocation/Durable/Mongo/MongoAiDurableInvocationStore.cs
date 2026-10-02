using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.Runtime.Observability.Performance;

namespace Multiplexed.AI.Runtime.Invocation.Durable.Mongo
{
    /// <summary>
    /// Single-document durable CAS store. Uses the existing Mongo database/client,
    /// majority journaled writes and primary majority reads; no cross-store transaction,
    /// hosted service, worker dispatch, TTL deletion or implicit registration is added.
    /// </summary>
    public sealed partial class MongoAiDurableInvocationStore : IAiDurableInvocationStore,
        IAiDurableInvocationClassifiedCasStore,
        Multiplexed.Abstractions.AI.Invocation.Workers.IAiDurableInvocationDispatchPageStore,
        IAiDurableInvocationContinuationPageStore
    {
        private readonly IMongoCollection<BsonDocument> _collection;
        private readonly SemaphoreSlim _indexLock = new(1, 1);
        private volatile bool _indexesReady;
        private static readonly Collation Ordinal = new("simple");
        private const string DispatchIndexName = "ix_durable_invocation_dispatch";
        private const string PreparedDispatchIndexName = "ix_durable_invocation_dispatch_prepared_v2";
        private const string LegacyContinuationIndexName = "ix_durable_invocation_continuation";
        private const string ContinuationIndexName = "ix_durable_invocation_continuation_v2";

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
            return await ReadAsync(filter, AiMongoAttributionOperations.InvocationGet, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<AiDurableInvocationRecord> GetOrCreateAsync(
            AiDurableInvocationRecord prepared, CancellationToken cancellationToken = default)
        {
            AiDurableInvocationValidation.ValidateRecord(prepared);
            AiDurableInvocationValidation.Require(prepared.Status == AiDurableInvocationStatus.Prepared,
                "Only initial preparation can be inserted.");
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
            var insertMeasurement = AiMongoAttributionDiagnostics.StartOperation(
                AiMongoAttributionOperations.InvocationPrepareInsert,
                AiMongoAttributionCommands.Insert,
                requestedDocuments: 1);
            try
            {
                await _collection.InsertOneAsync(AiDurableInvocationMongoCodec.Encode(prepared), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                insertMeasurement.Succeed();
                return prepared;
            }
            catch (OperationCanceledException)
            {
                insertMeasurement.Cancel();
                throw;
            }
            catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                insertMeasurement.Fail(duplicateKeyRetry: true);
                // Read by the full typed identity, without a supplied scope that could hide
                // conflicting ownership. This internal check never returns foreign content.
                // A competing insert can win uniqueness before it is majority-readable.
                // Reconcile that visibility window; do not misreport it as a content conflict.
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    var existing = await ReadAsync(AiDurableInvocationMongoCodec.IdentityFilter(prepared.Definition.Identity), AiMongoAttributionOperations.InvocationGet, cancellationToken)
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
            catch
            {
                insertMeasurement.Fail();
                throw;
            }
        }

        /// <inheritdoc />
        public Task<bool> TryReplaceAsync(
            AiDurableInvocationRecord expected, AiDurableInvocationRecord replacement,
            CancellationToken cancellationToken = default) =>
            TryReplaceCoreAsync(expected, replacement, cancellationToken);

        /// <inheritdoc />
        public async Task<AiDurableInvocationCasOutcome> TryReplaceClassifiedAsync(
            AiDurableInvocationRecord expected, AiDurableInvocationRecord replacement,
            CancellationToken cancellationToken = default)
        {
            if (await TryReplaceCoreAsync(expected, replacement, cancellationToken).ConfigureAwait(false))
                return AiDurableInvocationCasOutcome.Applied();

            // ReplacementFilter contains both the exact expected snapshot/revision and any
            // authoritative server-time lease predicates. If the exact expected snapshot is
            // still durable after a rejected replace, the rejection came from the authority
            // predicate, not from competing state mutation. Otherwise the expected revision
            // lost contention and the durable snapshot is safe to reuse as the next candidate.
            var current = await GetByExpectedAddressAsync(expected, CasOperation(expected, replacement), cancellationToken).ConfigureAwait(false);
            return current == expected
                ? AiDurableInvocationCasOutcome.AuthorityPredicateRejected()
                : AiDurableInvocationCasOutcome.RevisionConflict(current);
        }

        private async Task<bool> TryReplaceCoreAsync(
            AiDurableInvocationRecord expected, AiDurableInvocationRecord replacement,
            CancellationToken cancellationToken)
        {
            AiDurableInvocationValidation.ValidateTransition(expected, replacement);
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
            var operation = CasOperation(expected, replacement);
            var measurement = AiMongoAttributionDiagnostics.StartOperation(
                operation,
                AiMongoAttributionCommands.Update,
                requestedDocuments: 1);
            try
            {
                var applied = await MongoDurableCasInfrastructure.ReplaceOneAsync(
                    _collection,
                    AiDurableInvocationMongoCodec.ReplacementFilter(expected, replacement),
                    AiDurableInvocationMongoCodec.Encode(replacement),
                    Ordinal,
                    "Invocation replacement was not acknowledged.",
                    cancellationToken).ConfigureAwait(false);
                measurement.Succeed(applied ? 1 : 0);
                return applied;
            }
            catch (OperationCanceledException) { measurement.Cancel(); throw; }
            catch { measurement.Fail(); throw; }
        }

        private static string CasOperation(
            AiDurableInvocationRecord expected,
            AiDurableInvocationRecord replacement) =>
            expected.Result is null && replacement.Result is not null
                ? AiMongoAttributionOperations.InvocationResultAcceptance
                : AiMongoAttributionOperations.InvocationCas;

        /// <inheritdoc />
        public Task<IReadOnlyList<AiDurableInvocationRecord>> ListDispatchCandidatesAsync(
            AiDurableInvocationScope scope, string executionLanguage, DateTimeOffset nowUtc, int maxCount,
            CancellationToken cancellationToken = default) =>
            ListDispatchPageAsync(scope, executionLanguage, nowUtc, maxCount, null, cancellationToken);

        /// <inheritdoc />
        public Task<IReadOnlyList<AiDurableInvocationRecord>> ListContinuationCandidatesAsync(
            AiDurableInvocationScope scope, int maxCount, CancellationToken cancellationToken = default) =>
            ListContinuationPageAsync(scope, maxCount, null, cancellationToken);

        private Task<AiDurableInvocationRecord?> GetByExpectedAddressAsync(
            AiDurableInvocationRecord expected, string operation, CancellationToken cancellationToken)
        {
            var filter = AiDurableInvocationMongoCodec.IdentityFilter(expected.Definition.Identity);
            filter.Add("tenantGroupId", expected.Definition.Scope.TenantGroupId);
            filter.Add("controlPlaneId", expected.Definition.Scope.ControlPlaneId);
            return ReadAsync(filter, operation, cancellationToken);
        }

        private async Task<AiDurableInvocationRecord?> ReadAsync(
            BsonDocument filter, string operation, CancellationToken cancellationToken)
        {
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
            var measurement = AiMongoAttributionDiagnostics.StartOperation(
                operation,
                AiMongoAttributionCommands.Find);
            try
            {
                var document = await _collection.Find(filter, new FindOptions { Collation = Ordinal })
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                measurement.Succeed(document is null ? 0 : 1);
                return document is null ? null : AiDurableInvocationMongoCodec.Decode(document);
            }
            catch (OperationCanceledException) { measurement.Cancel(); throw; }
            catch { measurement.Fail(); throw; }
        }

        private async Task<IReadOnlyList<AiDurableInvocationRecord>> ListAsync(
            BsonDocument filter, int maxCount, string operation, CancellationToken cancellationToken, string? hint = null)
        {
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
            var measurement = AiMongoAttributionDiagnostics.StartOperation(
                operation,
                AiMongoAttributionCommands.Find,
                requestedDocuments: maxCount);
            try
            {
                var documents = await _collection.Find(filter, new FindOptions { Collation = Ordinal, Hint = hint is null ? null : new BsonString(hint) })
                    .Sort(new BsonDocument { { "updatedAt", 1 }, { "_id", 1 } }).Limit(maxCount)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                measurement.Succeed(documents.Count);
                return documents.Select(AiDurableInvocationMongoCodec.Decode).ToArray();
            }
            catch (OperationCanceledException) { measurement.Cancel(); throw; }
            catch { measurement.Fail(); throw; }
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
                var dispatch = new BsonDocument
                {
                    { "controlPlaneId", 1 }, { "tenantId", 1 }, { "tenantGroupId", 1 },
                    { "language", 1 }, { "status", 1 }, { "leaseExpiresAt", 1 }, { "updatedAt", 1 }
                };
                var preparedDispatch = new BsonDocument
                {
                    { "controlPlaneId", 1 }, { "tenantId", 1 }, { "tenantGroupId", 1 },
                    { "language", 1 }, { "status", 1 }, { "updatedAt", 1 }, { "_id", 1 }
                };
                var legacyContinuation = new BsonDocument
                {
                    { "controlPlaneId", 1 }, { "tenantId", 1 }, { "tenantGroupId", 1 },
                    { "continuationStatus", 1 }, { "updatedAt", 1 }
                };
                var continuation = new BsonDocument
                {
                    { "controlPlaneId", 1 }, { "tenantId", 1 }, { "tenantGroupId", 1 },
                    { "continuationStatus", 1 }, { "status", 1 }, { "updatedAt", 1 }, { "_id", 1 }
                };
                await _collection.Indexes.CreateManyAsync(new[]
                {
                    new CreateIndexModel<BsonDocument>(identity,
                        new CreateIndexOptions { Name = "uq_durable_invocation_identity", Unique = true, Collation = Ordinal }),
                    new CreateIndexModel<BsonDocument>(dispatch,
                        new CreateIndexOptions { Name = DispatchIndexName, Collation = Ordinal }),
                    new CreateIndexModel<BsonDocument>(preparedDispatch,
                        new CreateIndexOptions { Name = PreparedDispatchIndexName, Collation = Ordinal }),
                    // Retain the legacy continuation index during the compatibility window. Existing
                    // deployments may already own it, while the v2 index is additive and can be
                    // adopted without an index-name/key-spec conflict during rolling upgrades.
                    new CreateIndexModel<BsonDocument>(legacyContinuation,
                        new CreateIndexOptions { Name = LegacyContinuationIndexName, Collation = Ordinal }),
                    new CreateIndexModel<BsonDocument>(continuation,
                        new CreateIndexOptions { Name = ContinuationIndexName, Collation = Ordinal })
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
                _indexesReady = true;
            }
            finally { _indexLock.Release(); }
        }
    }
}
