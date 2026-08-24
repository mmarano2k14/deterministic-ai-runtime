using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// MongoDB persistence document for runtime recovery forensics records.
    /// </summary>
    /// <remarks>
    /// This document keeps MongoDB persistence concerns outside of the shared
    /// runtime recovery forensics domain contract. The MongoDB document identity
    /// is the stable recovery forensics identifier, while the domain record remains
    /// stored unchanged in <see cref="Record"/>.
    /// </remarks>
    internal sealed record MongoAiRuntimeRecoveryForensicsDocument
    {
        /// <summary>
        /// Gets the MongoDB document identifier.
        /// </summary>
        /// <remarks>
        /// This value must be equal to <see cref="AiRuntimeRecoveryForensicsIdentity.ForensicsId"/>.
        /// </remarks>
        [BsonId]
        [BsonRepresentation(BsonType.String)]
        public string Id { get; init; } = string.Empty;

        /// <summary>
        /// Gets the optimistic-concurrency version of this persistence document.
        /// </summary>
        /// <remarks>
        /// The value is persistence-only and is incremented on every successful
        /// mutation so concurrent read/merge/replace writers cannot silently
        /// overwrite recovery-forensics events appended by another writer.
        /// Documents created before this field existed deserialize with version zero.
        /// </remarks>
        public long Version { get; init; }

        /// <summary>
        /// Gets the persisted runtime recovery forensics domain record.
        /// </summary>
        public AiRuntimeRecoveryForensicsRecord Record { get; init; } = default!;

        /// <summary>
        /// Creates a MongoDB document from a runtime recovery forensics record.
        /// </summary>
        /// <param name="record">The runtime recovery forensics record.</param>
        /// <returns>The MongoDB persistence document.</returns>
        public static MongoAiRuntimeRecoveryForensicsDocument FromRecord(
            AiRuntimeRecoveryForensicsRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentException.ThrowIfNullOrWhiteSpace(record.Identity.ForensicsId);

            return new MongoAiRuntimeRecoveryForensicsDocument
            {
                Id = record.Identity.ForensicsId,
                Record = record
            };
        }
    }
}
