using MongoDB.Bson;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.Runtime.Observability.Performance;

namespace Multiplexed.AI.Runtime.Invocation.Durable.Mongo
{
    public sealed partial class MongoAiDurableInvocationStore
    {
        /// <inheritdoc />
        public async Task<IReadOnlyList<AiDurableInvocationRecord>> ListContinuationPageAsync(
            AiDurableInvocationScope scope, int maxCount, AiDurableInvocationContinuationCursor? after = null,
            CancellationToken cancellationToken = default)
        {
            ValidateQuery(scope, maxCount);
            var filter = AiDurableInvocationMongoCodec.ScopeFilter(scope);
            filter.Add("status", new BsonDocument("$in", new BsonArray
                { (int)AiDurableInvocationStatus.Succeeded, (int)AiDurableInvocationStatus.Failed }));
            filter.Add("continuationStatus", new BsonDocument("$in", new BsonArray
                { (int)AiDurableInvocationContinuationStatus.Pending, (int)AiDurableInvocationContinuationStatus.Scheduled }));
            if (after is not null)
            {
                AiDurableInvocationValidation.Text(after.OperationId, nameof(after.OperationId));
                if (after.UpdatedAtUtc.Offset != TimeSpan.Zero ||
                    after.UpdatedAtUtc.Ticks % TimeSpan.TicksPerMillisecond != 0)
                    throw new ArgumentException(
                        "Continuation cursor time must be UTC with millisecond precision.", nameof(after));
                var at = after.UpdatedAtUtc.ToUnixTimeMilliseconds();
                filter.Add("$or", new BsonArray
                {
                    new BsonDocument("updatedAt", new BsonDocument("$gt", at)),
                    new BsonDocument { { "updatedAt", at }, { "_id", new BsonDocument("$gt", after.OperationId) } }
                });
            }
            return await ListAsync(filter, maxCount, AiMongoAttributionOperations.InvocationContinuationScan,
                cancellationToken, ContinuationIndexName).ConfigureAwait(false);
        }
    }
}
