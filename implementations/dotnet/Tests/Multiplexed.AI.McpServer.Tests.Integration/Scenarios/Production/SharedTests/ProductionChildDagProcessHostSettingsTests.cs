using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Runners;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.SharedTests
{
    /// <summary>
    /// Verifies that process-host child DAG runtime wiring remains strictly opt-in by scenario depth.
    /// </summary>
    public sealed class ProductionChildDagProcessHostSettingsTests
    {
        private const string ChildDagEnabledSetting =
            "AiRuntimeProcessHostCreation:EnvironmentVariables:AiChildDagComposition__Enabled";

        /// <summary>
        /// Verifies that the historical zero-depth scenario does not enable child DAG composition in spawned runtimes.
        /// </summary>
        [Fact]
        public void Build_Should_Not_Enable_Child_Dag_Composition_When_ChildDepth_Is_Zero()
        {
            var scenario = ProductionRuntimeScenarioFactory.CreateSingleTenantSharedRuntimeModeScenario();

            var settings = HttpProcessHostProductionScenarioSettingsBuilder.Build(
                scenario,
                "child-depth-zero-settings-test",
                "runtime-host.dll");

            Assert.Equal(0, Assert.Single(scenario.Tenants).Run.ChildDepth);
            Assert.False(settings.ContainsKey(ChildDagEnabledSetting));
        }

        /// <summary>
        /// Verifies that a positive child depth enables child DAG composition only in spawned runtime processes.
        /// </summary>
        [Fact]
        public void Build_Should_Enable_Child_Dag_Composition_When_ChildDepth_Is_Positive()
        {
            var baseScenario = ProductionRuntimeScenarioFactory.CreateSingleTenantSharedRuntimeModeScenario();
            var tenant = Assert.Single(baseScenario.Tenants);
            var scenario =
                baseScenario with
                {
                    Tenants = new[]
                    {
                        tenant with
                        {
                            Run = tenant.Run with
                            {
                                ChildDepth = 1
                            }
                        }
                    }
                };

            var settings = HttpProcessHostProductionScenarioSettingsBuilder.Build(
                scenario,
                "child-depth-one-settings-test",
                "runtime-host.dll");

            Assert.Equal("true", settings[ChildDagEnabledSetting]);
            Assert.False(settings.ContainsKey("AiChildDagComposition:Enabled"));
        }
    }
}
