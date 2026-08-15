using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.Core.ExecutionContext;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Snapshots
{
    /// <summary>
    /// Represents the immutable pre-relation preparation manifest for one typed child DAG invocation generation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The manifest is persisted under a deterministic key derived from the child invocation identity before the
    /// authoritative parent-child relation is created. It closes the crash window where immutable definition/input
    /// artifacts are already durable but no relation exists yet to reference them.
    /// </para>
    /// <para>
    /// This object is not the business authority for child execution state. Once the relation exists, the relation
    /// remains authoritative and the manifest is used only as a pre-relation recovery aid.
    /// </para>
    /// </remarks>
    public sealed class AiChildInvocationPreparationSnapshot
    {
        /// <summary>
        /// Gets the exact typed invocation identity represented by this preparation.
        /// </summary>
        public required AiChildInvocationIdentity Identity { get; init; }

        /// <summary>
        /// Gets the deterministic child invocation key derived from <see cref="Identity"/>.
        /// </summary>
        public required string ChildInvocationKey { get; init; }

        /// <summary>
        /// Gets the immutable declarative child DAG definition snapshot.
        /// </summary>
        public required AiStoredPayload FrozenChildDagDefinition { get; init; }

        /// <summary>
        /// Gets the immutable child invocation input snapshot.
        /// </summary>
        public required AiStoredPayload FrozenInvocationInput { get; init; }

        /// <summary>
        /// Gets the durable execution context delegated from the parent boundary.
        /// </summary>
        public required ExecutionContextSnapshot DelegatedExecutionContextSnapshot { get; init; }

        /// <summary>
        /// Gets adapter-neutral string metadata delegated with the child invocation.
        /// </summary>
        public IReadOnlyDictionary<string, string> DelegatedMetadata { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Gets the immutable delegation policy binding frozen for the parent call site.
        /// </summary>
        public required AiStoredPayload DelegationPolicyBindingSnapshot { get; init; }
    }
}
