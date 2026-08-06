using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Verifies the HTTP transport composition of the shared Kubernetes Runtime Pool production harness.
    /// </summary>
    public sealed class HttpKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfileTests
    {
        /// <summary>
        /// Verifies first-class KubernetesPool topology and bounded capacity settings.
        /// </summary>
        [Fact]
        public void Profile_Should_Declare_Http_KubernetesPool_Topology()
        {
            var profile =
                new HttpKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfile(
                    maximumPodCount: 3,
                    runtimeCountPerPod: 5);

            Assert.Equal(
                AiRuntimeCapacityTopologyMode.KubernetesPool,
                profile.CapacityTopologyMode);
            Assert.Equal(
                AiRuntimeHostCreationMode.KubernetesPool,
                profile.HostCreationMode);
            Assert.Equal(
                "http",
                profile.ProviderName);
            Assert.Equal(
                3,
                profile.CrashRecoveryPlan.MaximumPodCount);
            Assert.Equal(
                5,
                profile.CrashRecoveryPlan.MaximumRuntimeCountPerPod);
        }

        /// <summary>
        /// Verifies that HTTP-specific scale-out and child transport settings are applied
        /// while the shared Runtime Pool capacity and persistence contract remains intact.
        /// </summary>
        [Fact]
        public void BuildSettings_Should_Apply_Http_RuntimePool_Transport_Without_Changing_Common_Contract()
        {
            var profile =
                new HttpKubernetesRuntimePoolCrashRecoveryScenarioRuntimeProfile(
                    maximumPodCount: 3,
                    runtimeCountPerPod: 5);

            var scenario =
                ProductionRuntimeScenarioFactory
                    .CreateMultiTenantCapacityReplayLedgerScenario();

            var settings =
                profile.BuildSettings(
                    scenario,
                    "control-plane-http-runtime-pool",
                    "runtime-host.dll");

            Assert.Equal(
                "false",
                settings["AiGrpcRuntimeScaleOut:Enabled"]);
            Assert.Equal(
                AiRuntimeCapacityTopologyMode.KubernetesPool.ToString(),
                settings["AiHttpRuntimeScaleOut:CapacityTopologyMode"]);
            Assert.Equal(
                AiRuntimeHostCreationMode.KubernetesPool.ToString(),
                settings["AiHttpRuntimeScaleOut:HostCreationMode"]);
            Assert.Equal(
                "http",
                settings["AiKubernetesRuntimePool:ProviderName"]);
            Assert.Equal(
                "http",
                settings["AiKubernetesRuntimePool:TransportName"]);
            Assert.Equal(
                "18080",
                settings["AiKubernetesRuntimePool:FirstChildTransportPort"]);
            Assert.Equal(
                "15",
                settings["AiRunAdmission:MaxInstanceCount"]);
            Assert.Equal(
                "true",
                settings["AiRuntimeExecutionRecoveryReconciliation:EnableDagExecutionResume"]);

            var parentDatabaseName =
                Assert.IsType<string>(
                    settings["Mongo:DatabaseName"]);

            Assert.Equal(
                parentDatabaseName,
                settings["AiKubernetesRuntimePoolHost:MongoDatabaseName"]);
        }
    }
}
