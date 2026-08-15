using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.Core.ExecutionContext;

namespace Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations
{
    /// <summary>
    /// Represents the durable authority that binds one logical parent invocation to one child DAG execution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This relation is the business source of truth for child DAG composition. It keeps the typed invocation
    /// identity, immutable definition and input snapshots, delegation lifecycle, exact child execution mapping,
    /// authoritative child outcome, and parent continuation lifecycle in one durable model.
    /// </para>
    /// <para>
    /// The relation does not introduce a second scheduler, recovery system, policy engine, or execution model.
    /// The child remains a normal execution identified by <see cref="ChildExecutionId"/> once allocated.
    /// </para>
    /// </remarks>
    public sealed class AiChildExecutionRelation
    {
        /// <summary>
        /// Gets the tenant identifier that owns the relation.
        /// </summary>
        public required string TenantId { get; init; }

        /// <summary>
        /// Gets the durable parent execution identifier.
        /// </summary>
        public required string ParentExecutionId { get; init; }

        /// <summary>
        /// Gets the stable logical parent call-site identifier.
        /// </summary>
        public required string ParentCallSiteId { get; init; }

        /// <summary>
        /// Gets the logical child DAG identifier.
        /// </summary>
        public required string ChildDagId { get; init; }

        /// <summary>
        /// Gets the exact declarative child DAG version frozen for this invocation.
        /// </summary>
        public required string ChildDagDefinitionVersion { get; init; }

        /// <summary>
        /// Gets the frozen declarative child DAG definition.
        /// </summary>
        /// <remarks>
        /// The existing <see cref="AiStoredPayload"/> contract is reused so the definition may be inline
        /// or artifact-backed without introducing a second payload/reference model. Its content hash is the
        /// integrity digest when present.
        /// </remarks>
        public required AiStoredPayload FrozenChildDagDefinition { get; init; }

        /// <summary>
        /// Gets the canonical logical invocation key derived from committed parent state.
        /// </summary>
        public required string CanonicalLogicalInvocationKey { get; init; }

        /// <summary>
        /// Gets the deterministic child invocation key derived from the authoritative typed identity tuple.
        /// </summary>
        /// <remarks>
        /// This key is a lookup and integrity aid. Database uniqueness must remain enforced by the typed tuple.
        /// </remarks>
        public required string ChildInvocationKey { get; init; }

        /// <summary>
        /// Gets the durable invocation generation.
        /// </summary>
        public int InvocationGeneration { get; init; }

        /// <summary>
        /// Gets the frozen invocation input supplied to the child execution.
        /// </summary>
        /// <remarks>
        /// The existing <see cref="AiStoredPayload"/> representation is reused so inline and artifact-backed
        /// payloads share the same storage contract already used by execution payloads.
        /// </remarks>
        public required AiStoredPayload FrozenInvocationInput { get; init; }

        /// <summary>
        /// Gets the optional durable execution context snapshot delegated from the parent boundary.
        /// </summary>
        public ExecutionContextSnapshot? DelegatedExecutionContextSnapshot { get; init; }

        /// <summary>
        /// Gets immutable adapter-neutral metadata delegated with the child invocation.
        /// </summary>
        /// <remarks>
        /// This reuses the string metadata shape already exposed by runtime pipeline run requests instead of
        /// introducing a second execution-envelope abstraction before one is required by runtime integration.
        /// </remarks>
        public IReadOnlyDictionary<string, string> DelegatedMetadata { get; init; } =
            new Dictionary<string, string>();

        /// <summary>
        /// Gets the immutable delegation policy binding resolved before the relation is first persisted.
        /// </summary>
        /// <remarks>
        /// The existing <see cref="AiStoredPayload"/> contract is reused so the exact policy definition may be
        /// recovered without re-resolving live step or pipeline configuration after a crash or redeployment.
        /// </remarks>
        public required AiStoredPayload DelegationPolicyBindingSnapshot { get; init; }

        /// <summary>
        /// Gets or sets the immutable historical delegation policy decision committed with approval or denial.
        /// </summary>
        /// <remarks>
        /// This snapshot is absent while policy evaluation is pending and becomes mandatory once the relation
        /// reaches <see cref="AiChildExecutionRelationStatus.DelegationApproved"/> or
        /// <see cref="AiChildExecutionRelationStatus.DelegationDenied"/>.
        /// </remarks>
        public AiStoredPayload? DelegationPolicyDecisionSnapshot { get; set; }

        /// <summary>
        /// Gets or sets the current durable business status of the relation.
        /// </summary>
        public AiChildExecutionRelationStatus Status { get; set; } =
            AiChildExecutionRelationStatus.DelegationPolicyPending;

        /// <summary>
        /// Gets or sets the exact child execution identifier allocated after durable delegation approval.
        /// </summary>
        public string? ChildExecutionId { get; set; }

        /// <summary>
        /// Gets or sets the authoritative child result after the child reaches a terminal outcome.
        /// </summary>
        /// <remarks>
        /// The existing payload contract carries the inline value or artifact reference and its content hash.
        /// </remarks>
        public AiStoredPayload? ChildResult { get; set; }

        /// <summary>
        /// Gets or sets the authoritative child failure reason when the child completes unsuccessfully.
        /// </summary>
        public string? ChildFailureReason { get; set; }

        /// <summary>
        /// Gets or sets the durable parent continuation lifecycle.
        /// </summary>
        public AiChildContinuationStatus ContinuationStatus { get; set; } =
            AiChildContinuationStatus.None;

        /// <summary>
        /// Gets the UTC timestamp at which the relation was created.
        /// </summary>
        public required DateTimeOffset CreatedAtUtc { get; init; }

        /// <summary>
        /// Gets or sets the UTC timestamp at which delegation policy reached a durable decision.
        /// </summary>
        public DateTimeOffset? DelegationEvaluatedAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the UTC timestamp at which the exact child execution identifier was allocated.
        /// </summary>
        public DateTimeOffset? ChildAllocatedAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the UTC timestamp at which the relation entered the waiting state.
        /// </summary>
        public DateTimeOffset? WaitingAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the UTC timestamp at which the authoritative child outcome was committed.
        /// </summary>
        public DateTimeOffset? CompletedAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the UTC timestamp at which parent continuation scheduling was durably recorded.
        /// </summary>
        public DateTimeOffset? ParentContinuationScheduledAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the durable parent call-site step version observed when continuation scheduling was committed.
        /// </summary>
        /// <remarks>
        /// This version is the monotonic proof boundary used to distinguish real continuation progress from a
        /// signal-before-wait race where the original parent invocation is still running.
        /// </remarks>
        public long? ParentContinuationScheduledStepVersion { get; set; }

        /// <summary>
        /// Gets or sets the UTC timestamp at which the parent durably demonstrated resumed progress.
        /// </summary>
        public DateTimeOffset? ParentResumedAtUtc { get; set; }

        /// <summary>
        /// Creates the authoritative typed invocation identity represented by this relation.
        /// </summary>
        /// <returns>The typed child invocation identity tuple.</returns>
        public AiChildInvocationIdentity ToInvocationIdentity()
        {
            return new AiChildInvocationIdentity
            {
                TenantId = TenantId,
                ParentExecutionId = ParentExecutionId,
                ParentCallSiteId = ParentCallSiteId,
                ChildDagId = ChildDagId,
                ChildDagDefinitionVersion = ChildDagDefinitionVersion,
                CanonicalLogicalInvocationKey = CanonicalLogicalInvocationKey,
                InvocationGeneration = InvocationGeneration
            };
        }
    }
}
