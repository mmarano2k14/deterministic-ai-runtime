namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions
{
    /// <summary>
    /// Describes one tenant participating in a production runtime scenario.
    /// </summary>
    /// <remarks>
    /// A tenant scenario definition describes the tenant identity, runtime isolation
    /// mode, expected runtime capacity, workload, and isolation assertions used by
    /// provider-specific production scenario runners.
    /// </remarks>
    public sealed record ProductionTenantScenarioDefinition
    {
        /// <summary>
        /// Gets the tenant id used by MCP RBAC, admission, registry visibility, and runtime policy.
        /// </summary>
        public required string TenantId { get; init; }

        /// <summary>
        /// Gets the optional tenant group id.
        /// </summary>
        /// <remarks>
        /// Tenant group id is useful for policy grouping, but dedicated and hybrid
        /// runtime visibility must still be validated carefully so tenants do not
        /// accidentally consume another tenant's dedicated runtime capacity.
        /// </remarks>
        public string? TenantGroupId { get; init; }

        /// <summary>
        /// Gets the runtime mode expected for this tenant.
        /// </summary>
        /// <remarks>
        /// The runtime mode controls whether this tenant is expected to use dedicated,
        /// shared, or hybrid runtime capacity during the scenario.
        /// </remarks>
        public ProductionTenantRuntimeMode RuntimeMode { get; init; } =
            ProductionTenantRuntimeMode.Dedicated;

        /// <summary>
        /// Gets the expected runtime instance id prefix for this tenant.
        /// </summary>
        /// <remarks>
        /// This prefix is used by tenant isolation assertions to validate that runs
        /// were dispatched to runtime instances compatible with the tenant policy.
        /// </remarks>
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
        /// <remarks>
        /// Dedicated tenants should normally keep this enabled. Shared or hybrid
        /// scenarios may disable it when they intentionally validate shared fallback
        /// or shared pool routing.
        /// </remarks>
        public bool ExpectDedicatedRuntimePrefix { get; init; } = true;

        /// <summary>
        /// Gets a value indicating whether the scenario should observe temporary overflow before all runs dispatch.
        /// </summary>
        /// <remarks>
        /// This is useful for scenarios that intentionally submit more work than
        /// the initially available tenant capacity can dispatch immediately.
        /// </remarks>
        public bool ExpectCapacityOverflow { get; init; } = true;
    }
}