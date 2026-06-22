using System.Collections.Generic;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results
{
    /// <summary>
    /// Contains the execution result for one tenant inside a production runtime scenario.
    /// </summary>
    public sealed record ProductionTenantScenarioResult
    {
        /// <summary>
        /// Gets the tenant id.
        /// </summary>
        public required string TenantId { get; init; }

        /// <summary>
        /// Gets the optional tenant group id.
        /// </summary>
        public string? TenantGroupId { get; init; }

        /// <summary>
        /// Gets the pipeline key used for this tenant.
        /// </summary>
        public required string PipelineKey { get; init; }

        /// <summary>
        /// Gets the submitted shared run ids.
        /// </summary>
        public required IReadOnlyList<string> SharedRunIds { get; init; }

        /// <summary>
        /// Gets the runtime instance ids observed for this tenant.
        /// </summary>
        public required IReadOnlyList<string> RuntimeInstanceIds { get; init; }

        /// <summary>
        /// Gets the scale-out requests observed for this tenant.
        /// </summary>
        public IReadOnlyList<ProductionScaleOutScenarioResult> ScaleOutRequests { get; init; } =
            new List<ProductionScaleOutScenarioResult>();

        /// <summary>
        /// Gets the per-run results.
        /// </summary>
        public required IReadOnlyList<ProductionRunScenarioResult> Runs { get; init; }

        /// <summary>
        /// Gets a value indicating whether capacity overflow was observed for this tenant.
        /// </summary>
        public bool CapacityOverflowObserved { get; init; }

        /// <summary>
        /// Gets arbitrary tenant-level metadata captured by the runner.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();
    }
}