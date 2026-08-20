namespace Multiplexed.Abstractions.AI.Execution.Composition.ChildDag
{
    /// <summary>
    /// Defines canonical metadata keys used to preserve deterministic Child DAG identity and frozen invocation context.
    /// </summary>
    /// <remarks>
    /// These keys carry existing Child DAG correlation and immutable snapshot metadata through the shared runtime path.
    /// They do not define Child DAG scheduling, continuation, or recovery behavior.
    /// </remarks>
    public static class AiChildDagMetadataKeys
    {
        /// <summary>
        /// Gets the metadata key containing the deterministic child invocation key.
        /// </summary>
        public const string InvocationKey = "child.invocation.key";

        /// <summary>
        /// Gets the metadata key containing the child invocation generation.
        /// </summary>
        public const string InvocationGeneration = "child.invocation.generation";

        /// <summary>
        /// Gets the metadata key containing the allocated child execution identifier.
        /// </summary>
        public const string ExecutionId = "child.execution.id";

        /// <summary>
        /// Gets the metadata key containing the frozen child DAG definition version.
        /// </summary>
        public const string DefinitionVersion = "child.definition.version";

        /// <summary>
        /// Gets the metadata key containing the frozen child DAG definition digest.
        /// </summary>
        public const string DefinitionDigest = "child.definition.digest";

        /// <summary>
        /// Gets the metadata key containing the frozen child invocation input digest.
        /// </summary>
        public const string InputDigest = "child.input.digest";

        /// <summary>
        /// Gets the metadata key containing the parent execution identifier.
        /// </summary>
        public const string ParentExecutionId = "parent.execution.id";

        /// <summary>
        /// Gets the metadata key containing the parent Child DAG call-site identifier.
        /// </summary>
        public const string ParentCallSiteId = "parent.callsite.id";

        /// <summary>
        /// Gets the camel-case child execution identifier metadata key used by compatibility and diagnostic payloads.
        /// </summary>
        public const string CamelCaseChildExecutionId = "childExecutionId";
    }
}
