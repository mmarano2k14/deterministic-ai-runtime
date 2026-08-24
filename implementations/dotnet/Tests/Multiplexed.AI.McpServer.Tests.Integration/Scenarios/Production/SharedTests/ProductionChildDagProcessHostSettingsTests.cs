using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.ProcessHostPool;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Runners;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.SharedTests
{
    /// <summary>
    /// Verifies that process-host child DAG runtime wiring remains strictly opt-in by scenario depth.
    /// </summary>
    public sealed class ProductionChildDagProcessHostSettingsTests
    {
        private const string ParentChildDagEnabledSetting =
            "AiChildDagComposition:Enabled";

        private const string ProcessChildDagEnabledSetting =
            "AiRuntimeProcessHostCreation:EnvironmentVariables:AiChildDagComposition__Enabled";

        private const string KubernetesPoolChildDagEnabledSetting =
            "AiKubernetesRuntimePoolHost:ChildEnvironmentVariables:AiChildDagComposition__Enabled";

        private const string ProcessHostPoolChildDagEnabledSetting =
            "AiRuntimeProcessPoolRuntimeInstance:EnvironmentVariables:AiChildDagComposition__Enabled";

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
            Assert.False(settings.ContainsKey(ParentChildDagEnabledSetting));
            Assert.False(settings.ContainsKey(ProcessChildDagEnabledSetting));
        }

        /// <summary>
        /// Verifies that a positive child depth enables child DAG composition in the control plane and spawned runtimes.
        /// </summary>
        [Fact]
        public void Build_Should_Enable_Child_Dag_Composition_When_ChildDepth_Is_Positive()
        {
            var scenario = ProductionChildDagScenarioFactory.CreateDepthOneScenario();

            var settings = HttpProcessHostProductionScenarioSettingsBuilder.Build(
                scenario,
                "child-depth-one-settings-test",
                "runtime-host.dll");

            Assert.Equal("true", settings[ParentChildDagEnabledSetting]);
            Assert.Equal("true", settings[ProcessChildDagEnabledSetting]);
        }

        /// <summary>
        /// Verifies that zero-depth Kubernetes Runtime Pool scenarios do not project Child DAG composition into in-Pod children.
        /// </summary>
        [Fact]
        public void KubernetesPool_Build_Should_Not_Enable_Child_Dag_Composition_When_ChildDepth_Is_Zero()
        {
            var scenario = ProductionRuntimeScenarioFactory.CreateSingleTenantSharedRuntimeModeScenario();
            var profile = new HttpKubernetesRuntimePoolChildDagScenarioRuntimeProfile();

            var settings = HttpKubernetesRuntimePoolProductionScenarioSettingsBuilder.Build(
                scenario,
                "kubernetes-child-depth-zero-settings-test",
                "runtime-host.dll",
                profile);

            Assert.False(settings.ContainsKey(KubernetesPoolChildDagEnabledSetting));
        }

        /// <summary>
        /// Verifies that Kubernetes Runtime Pool children receive the same opt-in Child DAG feature environment as Process Host children.
        /// </summary>
        [Fact]
        public void KubernetesPool_Build_Should_Project_Child_Dag_Composition_To_InPod_Children()
        {
            var scenario = ProductionChildDagScenarioFactory.CreateDepthOneScenario();
            var profile = new HttpKubernetesRuntimePoolChildDagScenarioRuntimeProfile();

            var settings = HttpKubernetesRuntimePoolProductionScenarioSettingsBuilder.Build(
                scenario,
                "kubernetes-child-depth-one-settings-test",
                "runtime-host.dll",
                profile);

            Assert.Equal("true", settings[ParentChildDagEnabledSetting]);
            Assert.Equal("true", settings[ProcessChildDagEnabledSetting]);
            Assert.Equal("true", settings[KubernetesPoolChildDagEnabledSetting]);
        }

        /// <summary>
        /// Verifies that the focused real child-runtime kill scenario enables normal DAG execution recovery.
        /// </summary>
        [Fact]
        public void Build_Should_Enable_Dag_Resume_For_Real_Child_Runtime_Crash_Recovery_Scenario()
        {
            var scenario = ProductionChildDagScenarioFactory.CreateDepthOneRuntimeCrashRecoveryScenario();

            var settings = HttpProcessHostProductionScenarioSettingsBuilder.Build(
                scenario,
                "child-depth-one-recovery-settings-test",
                "runtime-host.dll");

            Assert.Equal(
                "True",
                settings["AiRuntimeExecutionRecoveryReconciliation:EnableDagExecutionResume"]);
            Assert.Equal("true", settings[ParentChildDagEnabledSetting]);
            Assert.Equal("true", settings[ProcessChildDagEnabledSetting]);
        }

        /// <summary>
        /// Verifies that historical zero-depth ProcessHostPool children remain opted out of Child DAG composition.
        /// </summary>
        [Fact]
        public void ProcessHostPool_Build_Should_Not_Enable_Child_Dag_Composition_When_ChildDepth_Is_Zero()
        {
            var scenario =
                ProductionRuntimeScenarioFactory
                    .CreateSingleTenantSharedRuntimeModeScenario();
            var tenant = Assert.Single(scenario.Tenants);
            var profile = ProcessHostPoolProductionScenarioProfile.CreateHttp();
            var controlPlaneSettings =
                ProcessHostPoolProductionScenarioSettingsComposer
                    .BuildControlPlaneSettings(
                        profile,
                        scenario,
                        "process-host-pool-child-depth-zero",
                        "runtime-host.dll",
                        totalRuntimeCount: 5);

            var settings =
                ProcessHostPoolProductionScenarioSettingsComposer
                    .BuildProcessHostSettings(
                        profile,
                        controlPlaneSettings,
                        "process-host-pool-child-depth-zero",
                        "process-host-pool-child-depth-zero-pool",
                        "runtime-host.dll",
                        "http://127.0.0.1:5900",
                        childBasePort: 5910,
                        processHostOrdinal: 1,
                        runtimeCountPerHost: 5,
                        tenant: tenant);

            Assert.False(settings.ContainsKey(ParentChildDagEnabledSetting));
            Assert.False(settings.ContainsKey(ProcessHostPoolChildDagEnabledSetting));
        }

        /// <summary>
        /// Verifies that positive-depth ProcessHostPool children receive the same Child DAG opt-in as their external parent Process Host.
        /// </summary>
        [Fact]
        public void ProcessHostPool_Build_Should_Project_Child_Dag_Composition_To_RuntimeInstanceOnly_Children()
        {
            var scenario = ProductionChildDagScenarioFactory.CreateDepthOneScenario();
            var tenant = Assert.Single(scenario.Tenants);
            var profile = ProcessHostPoolProductionScenarioProfile.CreateHttp();
            var controlPlaneSettings =
                ProcessHostPoolProductionScenarioSettingsComposer
                    .BuildControlPlaneSettings(
                        profile,
                        scenario,
                        "process-host-pool-child-depth-one",
                        "runtime-host.dll",
                        totalRuntimeCount: 5);

            var settings =
                ProcessHostPoolProductionScenarioSettingsComposer
                    .BuildProcessHostSettings(
                        profile,
                        controlPlaneSettings,
                        "process-host-pool-child-depth-one",
                        "process-host-pool-child-depth-one-pool",
                        "runtime-host.dll",
                        "http://127.0.0.1:5900",
                        childBasePort: 5910,
                        processHostOrdinal: 1,
                        runtimeCountPerHost: 5,
                        tenant: tenant);

            Assert.Equal("true", settings[ParentChildDagEnabledSetting]);
            Assert.Equal("true", settings[ProcessHostPoolChildDagEnabledSetting]);
        }

    }
}
