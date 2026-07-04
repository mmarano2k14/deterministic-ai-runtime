using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Runners;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Runners;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Runners
{
    /// <summary>
    /// Runs provider-agnostic production runtime scenarios against the gRPC provider using process-based runtime host creation.
    /// </summary>
    public sealed class GrpcProcessHostProductionScenarioRunner : IProductionRuntimeScenarioRunner
    {
        private readonly ProcessHostProductionScenarioRunner inner;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrpcProcessHostProductionScenarioRunner"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public GrpcProcessHostProductionScenarioRunner(
            ITestOutputHelper output)
        {
            inner =
                new ProcessHostProductionScenarioRunner(
                    "grpc-process-host",
                    "GRPC PROCESS PRODUCTION",
                    "grpc",
                    GrpcProcessHostProductionScenarioSettingsBuilder.Build,
                    output);
        }

        /// <inheritdoc />
        public string ProviderLabel => "grpc-process-host";

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