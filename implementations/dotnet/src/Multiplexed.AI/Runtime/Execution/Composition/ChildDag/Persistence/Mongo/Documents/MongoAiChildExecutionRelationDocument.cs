using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Persistence.Mongo.Documents
{
    /// <summary>
    /// Represents the MongoDB persistence envelope for one durable child execution relation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MongoDB-specific document identity remains inside the runtime persistence layer so the shared
    /// <see cref="AiChildExecutionRelation"/> contract does not need MongoDB attributes or storage-only members.
    /// </para>
    /// <para>
    /// The nested relation remains the business authority. MongoDB uniqueness is still enforced by the
    /// complete typed invocation tuple and never by this infrastructure document identifier.
    /// </para>
    /// </remarks>
    internal sealed class MongoAiChildExecutionRelationDocument
    {
        /// <summary>
        /// Gets the MongoDB infrastructure document identifier.
        /// </summary>
        [BsonId]
        public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

        /// <summary>
        /// Gets the durable parent-to-child execution relation stored by this document.
        /// </summary>
        public required AiChildExecutionRelation Relation { get; init; }
    }
}
