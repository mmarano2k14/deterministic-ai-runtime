using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Runners
{
    /// <summary>
    /// Runs a provider-agnostic production runtime scenario against a concrete runtime provider or host mode.
    /// </summary>
    public interface IProductionRuntimeScenarioRunner
    {
        /// <summary>
        /// Gets the provider-specific runner label.
        /// </summary>
        string ProviderLabel { get; }

        /// <summary>
        /// Runs the production runtime scenario.
        /// </summary>
        /// <param name="scenario">The provider-agnostic scenario definition.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The scenario result.</returns>
        Task<ProductionRuntimeScenarioResult> RunAsync(
            ProductionRuntimeScenarioDefinition scenario,
            CancellationToken cancellationToken = default);
    }
}