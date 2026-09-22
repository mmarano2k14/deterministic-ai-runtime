using MongoDB.Bson;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Observability.Performance;

namespace Multiplexed.AI.Runtime.Invocation.Durable.Mongo
{
    public sealed partial class MongoAiDurableInvocationStore
    {
        /// <inheritdoc />
        public async Task<IReadOnlyList<AiDurableInvocationRecord>> ListDispatchPageAsync(
            AiDurableInvocationScope scope, string executionLanguage, DateTimeOffset nowUtc, int maxCount,
            AiWorkerDispatchCursor? after = null, CancellationToken cancellationToken = default)
        {
            ValidateQuery(scope, maxCount);
            AiDurableInvocationValidation.ValidateLanguage(executionLanguage);
            ValidateDispatchCursor(after);

            var preparedFilter = DispatchBranchFilter(
                scope, executionLanguage, AiDurableInvocationStatus.Prepared, nowUtc, after, includeExpiryPredicate: false);
            var expiredLeaseFilter = DispatchBranchFilter(
                scope, executionLanguage, AiDurableInvocationStatus.Leased, nowUtc, after, includeExpiryPredicate: true);

            // Discovery is intentionally split into two bounded reads because the two populations
            // have different useful index orderings. The union remains a hint only; worker lease
            // CAS is still the dispatch authority.
            var preparedTask = ListDispatchBranchAsync(
                preparedFilter, maxCount, PreparedDispatchIndexName, cancellationToken);
            var expiredLeaseTask = ListDispatchBranchAsync(
                expiredLeaseFilter, maxCount, DispatchIndexName, cancellationToken);
            await Task.WhenAll(preparedTask, expiredLeaseTask).ConfigureAwait(false);

            return MergeDispatchPages(preparedTask.Result, expiredLeaseTask.Result, maxCount);
        }

        private async Task<IReadOnlyList<AiDurableInvocationRecord>> ListDispatchBranchAsync(
            BsonDocument filter, int maxCount, string indexName, CancellationToken cancellationToken)
        {
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
            var measurement = AiMongoAttributionDiagnostics.StartOperation(
                AiMongoAttributionOperations.InvocationDispatchScan,
                AiMongoAttributionCommands.Find,
                requestedDocuments: maxCount);
            try
            {
                var documents = await _collection.Find(filter, new FindOptions
                    { Collation = Ordinal, Hint = new BsonString(indexName) })
                    .Sort(new BsonDocument { { "updatedAt", 1 }, { "_id", 1 } })
                    .Limit(maxCount)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                measurement.Succeed(documents.Count);
                return documents.Select(AiDurableInvocationMongoCodec.Decode).ToArray();
            }
            catch (OperationCanceledException) { measurement.Cancel(); throw; }
            catch { measurement.Fail(); throw; }
        }

        private static BsonDocument DispatchBranchFilter(
            AiDurableInvocationScope scope,
            string executionLanguage,
            AiDurableInvocationStatus status,
            DateTimeOffset nowUtc,
            AiWorkerDispatchCursor? after,
            bool includeExpiryPredicate)
        {
            var filter = AiDurableInvocationMongoCodec.ScopeFilter(scope);
            filter.Add("language", executionLanguage);
            filter.Add("status", (int)status);
            if (includeExpiryPredicate)
                filter.Add("leaseExpiresAt", new BsonDocument("$lte", nowUtc.ToUnixTimeMilliseconds()));
            if (after is not null)
            {
                var at = after.UpdatedAtUtc.ToUnixTimeMilliseconds();
                filter.Add("$or", new BsonArray
                {
                    new BsonDocument("updatedAt", new BsonDocument("$gt", at)),
                    new BsonDocument { { "updatedAt", at }, { "_id", new BsonDocument("$gt", after.OperationId) } }
                });
            }
            return filter;
        }

        private static void ValidateDispatchCursor(AiWorkerDispatchCursor? after)
        {
            if (after is null) return;
            AiDurableInvocationValidation.Text(after.OperationId, nameof(after.OperationId));
            if (after.UpdatedAtUtc.Offset != TimeSpan.Zero ||
                after.UpdatedAtUtc.Ticks % TimeSpan.TicksPerMillisecond != 0)
                throw new ArgumentException(
                    "Worker dispatch cursor time must be UTC with millisecond precision.", nameof(after));
        }

        private static IReadOnlyList<AiDurableInvocationRecord> MergeDispatchPages(
            IReadOnlyList<AiDurableInvocationRecord> prepared,
            IReadOnlyList<AiDurableInvocationRecord> expiredLeases,
            int maxCount)
        {
            var byOperation = new Dictionary<string, AiDurableInvocationRecord>(StringComparer.Ordinal);
            foreach (var record in prepared.Concat(expiredLeases))
            {
                if (!byOperation.TryGetValue(record.OperationId, out var current) || record.Revision > current.Revision)
                    byOperation[record.OperationId] = record;
            }

            return byOperation.Values
                .OrderBy(record => record.UpdatedAtUtc)
                .ThenBy(record => record.OperationId, StringComparer.Ordinal)
                .Take(maxCount)
                .ToArray();
        }
    }
}
