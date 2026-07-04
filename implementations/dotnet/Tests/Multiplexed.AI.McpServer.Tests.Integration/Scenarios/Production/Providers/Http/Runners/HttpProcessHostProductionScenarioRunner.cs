using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Runners;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Runners;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Runners
{
    /// <summary>
    /// Runs provider-agnostic production runtime scenarios against the HTTP provider using process-based runtime host creation.
    /// </summary>
    public sealed class HttpProcessHostProductionScenarioRunner : IProductionRuntimeScenarioRunner
    {
        private readonly ProcessHostProductionScenarioRunner inner;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpProcessHostProductionScenarioRunner"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public HttpProcessHostProductionScenarioRunner(
            ITestOutputHelper output)
        {
            inner =
                new ProcessHostProductionScenarioRunner(
                    "http-process-host",
                    "HTTP PROCESS PRODUCTION",
                    "http",
                    HttpProcessHostProductionScenarioSettingsBuilder.Build,
                    output);
        }

        /// <inheritdoc />
        public string ProviderLabel => "http-process-host";

        /// <inheritdoc />
        public Task<ProductionRuntimeScenarioResult> RunAsync(
            ProductionRuntimeScenarioDefinition scenario,
            CancellationToken cancellationToken = default)
        {
            return inner.RunAsync(
                scenario,
                cancellationToken);
        }
    }
}