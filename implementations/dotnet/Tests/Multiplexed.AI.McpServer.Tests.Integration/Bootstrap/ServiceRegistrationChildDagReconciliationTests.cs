using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Multiplexed.AI.McpServer.Host.Bootstrap;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Reconciliation;

namespace Multiplexed.AI.McpServer.Tests.Integration.Bootstrap
{
    /// <summary>
    /// Verifies that the durable child-DAG reconciliation scan is owned by control-plane capable hosts,
    /// not multiplied across every RuntimeInstanceOnly process.
    /// </summary>
    public sealed class ServiceRegistrationChildDagReconciliationTests
    {
        [Fact]
        public void Configure_RuntimeInstanceOnly_DisablesGlobalChildDagReconciliationLoop()
        {
            var settings = GenericMcpServerTestSettings.CreateRuntimeInstanceSettings(
                overrides: new Dictionary<string, string?>
                {
                    ["AiChildDagComposition:Enabled"] = "true",
                    ["OpenAI:ApiKey"] = "test-key"
                });

            var options = ResolveReconciliationOptions(settings);

            Assert.False(options.Enabled);
        }

        [Fact]
        public void Configure_ControlPlaneCapableHost_KeepsGlobalChildDagReconciliationLoopEnabled()
        {
            var settings = GenericMcpServerTestSettings.CreateMcpSettings(
                controlPlaneId: GenericMcpServerTestSettings.CreateControlPlaneId("perf2-child-reconcile"),
                overrides: new Dictionary<string, string?>
                {
                    ["AiChildDagComposition:Enabled"] = "true",
                    ["OpenAI:ApiKey"] = "test-key"
                });

            var options = ResolveReconciliationOptions(settings);

            Assert.True(options.Enabled);
        }

        private static AiChildContinuationReconciliationOptions ResolveReconciliationOptions(
            IReadOnlyDictionary<string, string?> settings)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(settings)
                .Build();

            var services = new ServiceCollection();
            ServiceRegistration.Configure(services, configuration);

            using var provider = services.BuildServiceProvider();
            return provider
                .GetRequiredService<IOptions<AiChildContinuationReconciliationOptions>>()
                .Value;
        }
    }
}
