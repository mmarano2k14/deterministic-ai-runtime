using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle
{
    /// <summary>
    /// MongoDB persistence document for one runtime lifecycle event.
    /// </summary>
    internal sealed record MongoAiRuntimeLifecycleEventDocument
    {
        /// <summary>
        /// Gets the MongoDB document identifier.
        /// </summary>
        [BsonId]
        [BsonRepresentation(BsonType.String)]
        public string Id { get; init; } = string.Empty;

        /// <summary>
        /// Gets the immutable runtime lifecycle event.
        /// </summary>
        public AiRuntimeLifecycleEvent Event { get; init; } = default!;

        /// <summary>
        /// Creates a persistence document from a lifecycle event.
        /// </summary>
        public static MongoAiRuntimeLifecycleEventDocument FromEvent(
            AiRuntimeLifecycleEvent lifecycleEvent)
        {
            ArgumentNullException.ThrowIfNull(lifecycleEvent);
            ArgumentException.ThrowIfNullOrWhiteSpace(lifecycleEvent.EventId);

            return new MongoAiRuntimeLifecycleEventDocument
            {
                Id = lifecycleEvent.EventId,
                Event = lifecycleEvent
            };
        }
    }
}
