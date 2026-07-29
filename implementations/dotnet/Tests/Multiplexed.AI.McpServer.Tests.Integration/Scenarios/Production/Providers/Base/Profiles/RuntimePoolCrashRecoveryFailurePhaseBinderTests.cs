using System;
using System.Collections.Generic;
using System.Linq;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.Profiles;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Scenarios;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Verifies non-breaking Runtime Pool failure-phase binding for the shared production scenario harness.
    /// </summary>
    public sealed class RuntimePoolCrashRecoveryFailurePhaseBinderTests
    {
        /// <summary>
        /// Verifies that historical HTTP and gRPC process-host profiles remain on the unchanged process-kill path.
        /// </summary>
        [Fact]
        public void Bind_Should_Return_Empty_Map_For_Historical_ProcessHost_Profiles()
        {
            var impactedTenants =
                CreateImpactedTenants();

            Assert.Empty(
                RuntimePoolCrashRecoveryFailurePhaseBinder.Bind(
                    new HttpProcessHostScenarioRuntimeProfile(),
                    impactedTenants));

            Assert.Empty(
                RuntimePoolCrashRecoveryFailurePhaseBinder.Bind(
                    new GrpcProcessHostScenarioRuntimeProfile(),
                    impactedTenants));
        }

        /// <summary>
        /// Verifies deterministic runtime-process then Kubernetes-Pod assignment for the two impacted tenants.
        /// </summary>
        [Fact]
        public void Bind_Should_Assign_Ordered_All_In_One_Phases_To_Impacted_Tenants()
        {
            var impactedTenants =
                CreateImpactedTenants();

            var result =
                RuntimePoolCrashRecoveryFailurePhaseBinder.Bind(
                    new TestRuntimePoolScenarioRuntimeProfile(),
                    impactedTenants);

            Assert.Equal(
                RuntimePoolCrashFailureKind.RuntimeProcess,
                result[impactedTenants[0].TenantId].FailureKind);

            Assert.Equal(
                RuntimePoolCrashFailureKind.KubernetesPod,
                result[impactedTenants[1].TenantId].FailureKind);
        }

        /// <summary>
        /// Verifies that a Runtime Pool plan cannot silently omit or invent an impacted tenant flow.
        /// </summary>
        [Fact]
        public void Bind_Should_Reject_Impacted_Tenant_Count_Mismatch()
        {
            var impactedTenants =
                CreateImpactedTenants();

            Assert.Throws<InvalidOperationException>(
                () => RuntimePoolCrashRecoveryFailurePhaseBinder.Bind(
                    new TestRuntimePoolScenarioRuntimeProfile(),
                    impactedTenants.Take(1).ToArray()));
        }

        private static IReadOnlyList<ProductionTenantScenarioDefinition>
            CreateImpactedTenants()
        {
            var template =
                ProductionRuntimeScenarioFactory
                    .CreateSingleTenantDedicatedRuntimeModeScenario()
                    .Tenants
                    .Single();

            return new[]
            {
                template with
                {
                    TenantId = "tenant-runtime-failure",
                    TenantGroupId = "tenant-runtime-failure-group",
                    RuntimeInstanceIdPrefix =
                        "tenant-runtime-failure-runtime"
                },
                template with
                {
                    TenantId = "tenant-pod-failure",
                    TenantGroupId = "tenant-pod-failure-group",
                    RuntimeInstanceIdPrefix =
                        "tenant-pod-failure-runtime"
                }
            };
        }

        private sealed class TestRuntimePoolScenarioRuntimeProfile :
            IRuntimePoolCrashRecoveryScenarioRuntimeProfile
        {
            public AiRuntimeHostCreationMode HostCreationMode =>
                AiRuntimeHostCreationMode.KubernetesPool;

            public string ProviderName => "grpc";

            public string ProviderLabel => "grpc-kubernetes-pool-test";

            public string LogPrefix => "GRPC KUBERNETES POOL TEST";

            public string RequestedBy => "runtime-pool-plan-binding-test";

            public string Source => "integration-test";

            public string PoolIdPrefix => "runtime-pool-plan-binding";

            public RuntimePoolCrashRecoveryScenarioPlan CrashRecoveryPlan =>
                RuntimePoolCrashRecoveryScenarioPlan.CreateAllInOne(
                    initialPodCount: 2,
                    maximumPodCount: 3,
                    initialRuntimeCountPerPod: 3,
                    maximumRuntimeCountPerPod: 4);

            public Dictionary<string, string?> BuildSettings(
                ProductionRuntimeScenarioDefinition scenario,
                string controlPlaneId,
                string runtimeHostAssemblyPath)
            {
                throw new NotSupportedException(
                    "The failure-phase binding test does not build a host.");
            }
        }
    }
}
