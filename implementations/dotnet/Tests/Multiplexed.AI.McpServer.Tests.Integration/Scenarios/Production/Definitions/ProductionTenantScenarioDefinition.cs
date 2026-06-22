namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions
{
    /// <summary>
    /// Describes one tenant participating in a production runtime scenario.
    /// </summary>
    public sealed record ProductionTenantScenarioDefinition
    {
        /// <summary>
        /// Gets the tenant id used by MCP RBAC, admission, registry visibility, and runtime policy.
        /// </summary>
        public required string TenantId { get; init; }

        /// <summary>
        /// Gets the optional tenant group id.
        /// </summary>
        public string? TenantGroupId { get; init; }

        /// <summary>
        /// Gets the expected runtime instance id prefix for this tenant.
        /// </summary>
        public required string RuntimeInstanceIdPrefix { get; init; }

        /// <summary>
        /// Gets the maximum number of runtime instances allowed for this tenant.
        /// </summary>
        public required int MaxRuntimeInstances { get; init; }

        /// <summary>
        /// Gets the expected worker count per runtime instance.
        /// </summary>
        public required int WorkerCountPerInstance { get; init; }

        /// <summary>
        /// Gets the maximum concurrent runs expected per runtime instance.
        /// </summary>
        public required int MaxConcurrentRunsPerInstance { get; init; }

        /// <summary>
        /// Gets the local runtime queue capacity expected per runtime instance.
        /// </summary>
        public int LocalQueueCapacity { get; init; } = 100;

        /// <summary>
        /// Gets the run workload submitted for this tenant.
        /// </summary>
        public required ProductionRunScenarioDefinition Run { get; init; }

        /// <summary>
        /// Gets a value indicating whether assigned runtime instances must match the tenant runtime prefix.
        /// </summary>
        public bool ExpectDedicatedRuntimePrefix { get; init; } = true;

        /// <summary>
        /// Gets a value indicating whether the scenario should observe temporary overflow before all runs dispatch.
        /// </summary>
        public bool ExpectCapacityOverflow { get; init; } = true;
    }
}