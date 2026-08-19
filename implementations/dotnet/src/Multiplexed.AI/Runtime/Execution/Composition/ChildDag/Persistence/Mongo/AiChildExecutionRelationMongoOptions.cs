namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Persistence.Mongo
{
    /// <summary>
    /// Configures MongoDB persistence for authoritative child execution relations.
    /// </summary>
    public sealed class AiChildExecutionRelationMongoOptions
    {
        /// <summary>
        /// Gets or sets the MongoDB collection name used for child execution relations.
        /// </summary>
        public string CollectionName { get; set; } = "ai_child_execution_relations";

        /// <summary>
        /// Gets or sets whether required relation indexes are created automatically.
        /// </summary>
        public bool EnsureIndexes { get; set; } = true;
    }
}
