using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Verifies the focused one-Pod, one-runtime HTTP Kubernetes Runtime Pool Child DAG profile.
    /// </summary>
    public sealed class HttpKubernetesRuntimePoolChildDagScenarioRuntimeProfileTests
    {
        /// <summary>
        /// Verifies that the nominal Child DAG topology intentionally permits one Pod with one child runtime.
        /// </summary>
        [Fact]
        public void Profile_Should_Declare_One_Pod_One_Runtime_Nominal_Topology()
        {
            var profile =
                new HttpKubernetesRuntimePoolChildDagScenarioRuntimeProfile();

            Assert.Equal(
                AiRuntimeCapacityTopologyMode.KubernetesPool,
                profile.CapacityTopologyMode);
            Assert.Equal(
                AiRuntimeHostCreationMode.KubernetesPool,
                profile.HostCreationMode);
            Assert.Equal(1, profile.Topology.InitialPodCount);
            Assert.Equal(1, profile.Topology.MaximumPodCount);
            Assert.Equal(1, profile.Topology.InitialRuntimeCountPerPod);
            Assert.Equal(1, profile.Topology.MaximumRuntimeCountPerPod);
            Assert.False(profile.EnableDagExecutionResume);
        }

        /// <summary>
        /// Verifies that the nominal topology is projected into Kubernetes Runtime Pool settings
        /// without passing through the crash-recovery plan invariants.
        /// </summary>
        [Fact]
        public void BuildSettings_Should_Apply_One_Pod_One_Runtime_Child_Dag_Topology()
        {
            var profile =
                new HttpKubernetesRuntimePoolChildDagScenarioRuntimeProfile();
            var scenario =
                ProductionChildDagScenarioFactory.CreateDepthOneScenario();

            var settings =
                profile.BuildSettings(
                    scenario,
                    "control-plane-http-runtime-pool-child-dag",
                    "runtime-host.dll");

            Assert.Equal(
                "1",
                settings["AiRunAdmission:MaxInstanceCount"]);
            Assert.Equal(
                "1",
                settings["AiKubernetesRuntimePool:MaximumPodCount"]);
            Assert.Equal(
                "1",
                settings["AiKubernetesRuntimePool:InitialRuntimeInstanceCount"]);
            Assert.Equal(
                "1",
                settings["AiKubernetesRuntimePool:MinimumRuntimeInstanceCount"]);
            Assert.Equal(
                "1",
                settings["AiKubernetesRuntimePool:MaximumRuntimeInstanceCount"]);
            Assert.True(
                bool.Parse(settings["AiChildDagComposition:Enabled"]));
            Assert.Equal(
                "true",
                settings["AiKubernetesRuntimePoolHost:ChildEnvironmentVariables:AiChildDagComposition__Enabled"]);
            Assert.Equal(
                "False",
                settings["AiRuntimeExecutionRecoveryReconciliation:EnableDagExecutionResume"]);
        }

        /// <summary>
        /// Verifies that the final parked-parent failure proof can expose one additional Pod without changing the
        /// default one-Pod nominal topology.
        /// </summary>
        [Fact]
        public void BuildSettings_Should_Allow_Second_Pod_For_Parent_Failure_Proof()
        {
            var profile =
                new HttpKubernetesRuntimePoolChildDagScenarioRuntimeProfile(
                    maximumPodCount: 2);
            var scenario =
                ProductionChildDagScenarioFactory.CreateParentRuntimeCrashWhileChildRunsScenario();

            Assert.Equal(1, profile.Topology.InitialPodCount);
            Assert.Equal(2, profile.Topology.MaximumPodCount);
            Assert.Equal(1, profile.Topology.InitialRuntimeCountPerPod);
            Assert.Equal(1, profile.Topology.MaximumRuntimeCountPerPod);

            var settings =
                profile.BuildSettings(
                    scenario,
                    "control-plane-http-runtime-pool-parent-failure",
                    "runtime-host.dll");

            Assert.Equal(
                "2",
                settings["AiRunAdmission:MaxInstanceCount"]);
            Assert.Equal(
                "2",
                settings["AiKubernetesRuntimePool:MaximumPodCount"]);
            Assert.Equal(
                "1",
                settings["AiKubernetesRuntimePool:MaximumRuntimeInstanceCount"]);
        }
    }
}
