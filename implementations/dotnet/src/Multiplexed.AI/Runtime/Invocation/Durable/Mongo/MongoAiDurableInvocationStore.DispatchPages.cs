using MongoDB.Bson;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;

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
            var filter = AiDurableInvocationMongoCodec.ScopeFilter(scope);
            filter.Add("language", executionLanguage);
            var ready = new BsonDocument("$or", new BsonArray
            {
                new BsonDocument("status", (int)AiDurableInvocationStatus.Prepared),
                new BsonDocument { { "status", (int)AiDurableInvocationStatus.Leased },
                    { "leaseExpiresAt", new BsonDocument("$lte", nowUtc.ToUnixTimeMilliseconds()) } }
            });
            var conditions = new BsonArray { ready };
            if (after is not null)
            {
                AiDurableInvocationValidation.Text(after.OperationId, nameof(after.OperationId));
                if (after.UpdatedAtUtc.Offset != TimeSpan.Zero || after.UpdatedAtUtc.Ticks % TimeSpan.TicksPerMillisecond != 0)
                    throw new ArgumentException("Worker dispatch cursor time must be UTC with millisecond precision.", nameof(after));
                var at = after.UpdatedAtUtc.ToUnixTimeMilliseconds();
                conditions.Add(new BsonDocument("$or", new BsonArray
                {
                    new BsonDocument("updatedAt", new BsonDocument("$gt", at)),
                    new BsonDocument { { "updatedAt", at }, { "_id", new BsonDocument("$gt", after.OperationId) } }
                }));
            }
            filter.Add("$and", conditions);
            return await ListAsync(filter, maxCount, cancellationToken).ConfigureAwait(false);
        }
    }
}
