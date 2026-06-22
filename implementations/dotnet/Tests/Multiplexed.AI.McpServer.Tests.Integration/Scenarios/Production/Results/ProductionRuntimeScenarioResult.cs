using System.Collections.Generic;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results
{
    /// <summary>
    /// Contains the result of a provider-specific execution of a provider-agnostic production runtime scenario.
    /// </summary>
    public sealed record ProductionRuntimeScenarioResult
    {
        /// <summary>
        /// Gets the scenario name.
        /// </summary>
        public required string ScenarioName { get; init; }

        /// <summary>
        /// Gets the logical control-plane id used by the scenario.
        /// </summary>
        public required string ControlPlaneId { get; init; }

        /// <summary>
        /// Gets the provider or runner label used to execute the scenario.
        /// </summary>
        public required string ProviderLabel { get; init; }

        /// <summary>
        /// Gets the tenant results.
        /// </summary>
        public required IReadOnlyList<ProductionTenantScenarioResult> Tenants { get; init; }

        /// <summary>
        /// Gets arbitrary metadata captured by the provider-specific runner.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();
    }
}