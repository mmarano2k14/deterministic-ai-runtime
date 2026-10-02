using MongoDB.Bson;
using MongoDB.Driver;

namespace Multiplexed.AI.Runtime.Invocation.Durable.Mongo
{
    /// <summary>
    /// Low-level MongoDB replace-CAS plumbing shared by durable invocation stores.
    /// Domain state machines remain responsible for transition validation and outcome semantics.
    /// </summary>
    internal static class MongoDurableCasInfrastructure
    {
        public static async Task<bool> ReplaceOneAsync(
            IMongoCollection<BsonDocument> collection,
            FilterDefinition<BsonDocument> filter,
            BsonDocument replacement,
            Collation collation,
            string unacknowledgedMessage,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(collection);
            ArgumentNullException.ThrowIfNull(filter);
            ArgumentNullException.ThrowIfNull(replacement);
            ArgumentNullException.ThrowIfNull(collation);
            ArgumentException.ThrowIfNullOrWhiteSpace(unacknowledgedMessage);

            var result = await collection.ReplaceOneAsync(
                filter,
                replacement,
                new ReplaceOptions { IsUpsert = false, Collation = collation },
                cancellationToken).ConfigureAwait(false);

            if (!result.IsAcknowledged)
                throw new InvalidOperationException(unacknowledgedMessage);

            return result.ModifiedCount == 1;
        }
    }
}
