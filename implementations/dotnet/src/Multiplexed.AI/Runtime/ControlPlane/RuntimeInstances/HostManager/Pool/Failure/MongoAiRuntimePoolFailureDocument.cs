using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure
{
    /// <summary>
    /// MongoDB persistence document for one immutable runtime-pool failure observation.
    /// </summary>
    internal sealed record MongoAiRuntimePoolFailureDocument
    {
        [BsonId]
        [BsonRepresentation(BsonType.String)]
        public string Id { get; init; } = string.Empty;

        public AiRuntimePoolFailureObservation Observation { get; init; } = default!;

        public static MongoAiRuntimePoolFailureDocument FromObservation(
            AiRuntimePoolFailureObservation observation)
        {
            ArgumentNullException.ThrowIfNull(observation);
            ArgumentException.ThrowIfNullOrWhiteSpace(observation.FailureId);

            return new MongoAiRuntimePoolFailureDocument
            {
                Id = observation.FailureId,
                Observation = observation
            };
        }
    }
}
