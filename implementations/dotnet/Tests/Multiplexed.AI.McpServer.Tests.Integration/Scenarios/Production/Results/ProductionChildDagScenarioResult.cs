using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results
{
    /// <summary>
    /// Captures one authoritative parent-to-child relation observed by a production scenario.
    /// </summary>
    public sealed record ProductionChildDagScenarioResult
    {
        /// <summary>
        /// Gets the one-based nesting level below the submitted parent execution.
        /// </summary>
        public required int Depth { get; init; }

        /// <summary>
        /// Gets the tenant that owns both parent and child execution.
        /// </summary>
        public required string TenantId { get; init; }

        /// <summary>
        /// Gets the durable parent execution identifier.
        /// </summary>
        public required string ParentExecutionId { get; init; }

        /// <summary>
        /// Gets the exact durable child execution identifier.
        /// </summary>
        public required string ChildExecutionId { get; init; }

        /// <summary>
        /// Gets the deterministic logical child invocation key.
        /// </summary>
        public required string ChildInvocationKey { get; init; }

        /// <summary>
        /// Gets the child pipeline identifier.
        /// </summary>
        public required string ChildDagId { get; init; }

        /// <summary>
        /// Gets the frozen child pipeline version.
        /// </summary>
        public required string ChildDagDefinitionVersion { get; init; }

        /// <summary>
        /// Gets the explicit durable invocation generation.
        /// </summary>
        public required int InvocationGeneration { get; init; }

        /// <summary>
        /// Gets the final authoritative relation status.
        /// </summary>
        public required AiChildExecutionRelationStatus RelationStatus { get; init; }

        /// <summary>
        /// Gets the final durable parent continuation status.
        /// </summary>
        public required AiChildContinuationStatus ContinuationStatus { get; init; }

        /// <summary>
        /// Gets the authoritative child result digest when available.
        /// </summary>
        public string? ChildResultDigest { get; init; }

        /// <summary>
        /// Gets the authoritative child failure reason when the child terminated unsuccessfully.
        /// </summary>
        public string? ChildFailureReason { get; init; }
    }
}
