namespace Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations
{
    /// <summary>
    /// Represents the durable business lifecycle of one parent-to-child DAG execution relation.
    /// </summary>
    public enum AiChildExecutionRelationStatus
    {
        /// <summary>
        /// The complete relation exists and is waiting for the existing policy engine to evaluate delegation.
        /// </summary>
        DelegationPolicyPending = 0,

        /// <summary>
        /// Delegation was durably denied and no child execution may be created.
        /// </summary>
        DelegationDenied = 1,

        /// <summary>
        /// Delegation was durably approved but the child execution may not yet have been allocated.
        /// </summary>
        DelegationApproved = 2,

        /// <summary>
        /// The exact child execution identifier has been durably allocated to this relation.
        /// </summary>
        ChildAllocated = 3,

        /// <summary>
        /// The child is active or otherwise nonterminal and the parent call site is waiting on its durable outcome.
        /// </summary>
        Waiting = 4,

        /// <summary>
        /// The authoritative child outcome has been durably committed to the relation.
        /// </summary>
        Completed = 5
    }
}
