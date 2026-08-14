namespace Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity
{
    /// <summary>
    /// Represents the complete durable identity of one logical child DAG invocation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tuple represented by this type is the authoritative uniqueness boundary for
    /// child DAG composition. The derived child invocation key is an integrity and lookup
    /// aid; it does not replace the typed tuple as the source of truth.
    /// </para>
    /// <para>
    /// <see cref="ParentCallSiteId"/> must be stable across retry, recovery, process
    /// restart, runtime replacement, and queue redelivery. <see cref="CanonicalLogicalInvocationKey"/>
    /// must be derived from already committed parent state rather than newly fetched live state.
    /// </para>
    /// </remarks>
    public sealed record AiChildInvocationIdentity
    {
        /// <summary>
        /// Gets the tenant identifier that owns both the parent invocation and the child relation.
        /// </summary>
        public required string TenantId { get; init; }

        /// <summary>
        /// Gets the durable execution identifier of the parent DAG.
        /// </summary>
        public required string ParentExecutionId { get; init; }

        /// <summary>
        /// Gets the stable logical call-site identifier within the parent DAG definition.
        /// </summary>
        public required string ParentCallSiteId { get; init; }

        /// <summary>
        /// Gets the logical child DAG identifier.
        /// </summary>
        /// <remarks>
        /// For the current pipeline model this value maps to the logical pipeline name.
        /// </remarks>
        public required string ChildDagId { get; init; }

        /// <summary>
        /// Gets the exact declarative child DAG definition version used by this invocation.
        /// </summary>
        /// <remarks>
        /// The current pipeline definition exposes version as a string. Child DAG composition
        /// requires a non-empty version at the composition boundary without changing the global
        /// optional-version contract of existing pipelines.
        /// </remarks>
        public required string ChildDagDefinitionVersion { get; init; }

        /// <summary>
        /// Gets the canonical logical invocation key supplied by the parent call site.
        /// </summary>
        /// <remarks>
        /// Fan-out callers should derive this value from stable business identity already stored
        /// in committed parent state, for example portfolio, instrument, and analysis type.
        /// </remarks>
        public required string CanonicalLogicalInvocationKey { get; init; }

        /// <summary>
        /// Gets the durable invocation generation.
        /// </summary>
        /// <remarks>
        /// Generation zero is sticky across ordinary retry and recovery. A higher generation is
        /// reserved for an explicit durable decision to create a new logical child attempt.
        /// </remarks>
        public int InvocationGeneration { get; init; }
    }
}
