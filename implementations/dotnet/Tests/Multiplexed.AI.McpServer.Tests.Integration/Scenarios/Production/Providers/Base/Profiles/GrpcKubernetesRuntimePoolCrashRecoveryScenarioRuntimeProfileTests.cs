using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Profiles;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Verifies the first-class topology and physical host creation contract used by the gRPC Kubernetes Runtime Pool scenario.
    /// </summary>
    public sealed class GrpcKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfileTests
    {
        /// <summary>
        /// Verifies that the profile independently declares KubernetesPool topology and host materialization.
        /// </summary>
        [Fact]
        public void Profile_Should_Declare_KubernetesPool_Topology_And_HostCreationMode()
        {
            var profile =
                new GrpcKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfile();

            Assert.Equal(
                AiRuntimeCapacityTopologyMode.KubernetesPool,
                profile.CapacityTopologyMode);
            Assert.Equal(
                AiRuntimeHostCreationMode.KubernetesPool,
                profile.HostCreationMode);
            Assert.Equal(
                3,
                profile.CrashRecoveryPlan.InitialPodCount);
            Assert.Equal(
                3,
                profile.CrashRecoveryPlan.MaximumPodCount);
        }

        /// <summary>
        /// Verifies that the Runtime Pool Pod and its Process children use the same Mongo database
        /// as the parent MCP host for snapshots, replay metadata, ledger, and trace queries.
        /// </summary>
        [Fact]
        public void BuildSettings_Should_Keep_Parent_And_RuntimePool_Mongo_Database_Aligned()
        {
            var profile =
                new GrpcKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfile();

            var scenario =
                ProductionRuntimeScenarioFactory
                    .CreateMultiTenantCapacityReplayLedgerScenario();

            var settings =
                profile.BuildSettings(
                    scenario,
                    "control-plane-01",
                    "runtime-host.dll");

            var parentDatabaseName =
                Assert.IsType<string>(
                    settings["Mongo:DatabaseName"]);

            Assert.False(
                string.IsNullOrWhiteSpace(parentDatabaseName));
            Assert.Equal(
                parentDatabaseName,
                settings[
                    "AiEngine:Snapshots:Mongo:DatabaseName"]);
            Assert.Equal(
                parentDatabaseName,
                settings[
                    "AiRuntimeProcessHostCreation:EnvironmentVariables:Mongo__DatabaseName"]);
            Assert.Equal(
                parentDatabaseName,
                settings[
                    "AiRuntimeProcessHostCreation:EnvironmentVariables:AiEngine__Snapshots__Mongo__DatabaseName"]);
            Assert.Equal(
                parentDatabaseName,
                settings[
                    "AiKubernetesRuntimePoolHost:MongoDatabaseName"]);
        }
    }
}
