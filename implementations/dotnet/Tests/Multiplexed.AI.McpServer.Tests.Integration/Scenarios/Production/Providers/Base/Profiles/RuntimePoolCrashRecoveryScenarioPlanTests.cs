using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Verifies the bounded topology and ordered failure contract used by Runtime Pool production scenarios.
    /// </summary>
    public sealed class RuntimePoolCrashRecoveryScenarioPlanTests
    {
        /// <summary>
        /// Verifies that the canonical plan executes one runtime kill followed by one Pod deletion.
        /// </summary>
        [Fact]
        public void CreateAllInOne_Should_Define_Runtime_Then_Pod_Failure()
        {
            var plan =
                RuntimePoolCrashRecoveryScenarioPlan.CreateAllInOne(
                    initialPodCount: 2,
                    maximumPodCount: 3,
                    initialRuntimeCountPerPod: 3,
                    maximumRuntimeCountPerPod: 4);

            Assert.Equal(
                2,
                plan.InitialPodCount);
            Assert.Equal(
                3,
                plan.MaximumPodCount);
            Assert.Equal(
                3,
                plan.InitialRuntimeCountPerPod);
            Assert.Equal(
                4,
                plan.MaximumRuntimeCountPerPod);

            Assert.Collection(
                plan.FailurePhases,
                phase =>
                {
                    Assert.Equal(
                        1,
                        phase.Order);
                    Assert.Equal(
                        RuntimePoolCrashFailureKind.RuntimeProcess,
                        phase.FailureKind);
                    Assert.Equal(
                        "runtime-failure",
                        phase.ImpactedTenantRole);
                },
                phase =>
                {
                    Assert.Equal(
                        2,
                        phase.Order);
                    Assert.Equal(
                        RuntimePoolCrashFailureKind.KubernetesPod,
                        phase.FailureKind);
                    Assert.Equal(
                        "pod-failure",
                        phase.ImpactedTenantRole);
                });
        }

        /// <summary>
        /// Verifies that the dedicated Pod-failure plan contains exactly one complete Pod deletion.
        /// </summary>
        [Fact]
        public void CreatePodFailureOnly_Should_Define_One_Pod_Failure()
        {
            var plan =
                RuntimePoolCrashRecoveryScenarioPlan.CreatePodFailureOnly(
                    initialPodCount: 2,
                    maximumPodCount: 2,
                    initialRuntimeCountPerPod: 3,
                    maximumRuntimeCountPerPod: 3);

            Assert.Equal(
                2,
                plan.InitialPodCount);
            Assert.Equal(
                2,
                plan.MaximumPodCount);
            Assert.Equal(
                3,
                plan.InitialRuntimeCountPerPod);
            Assert.Equal(
                3,
                plan.MaximumRuntimeCountPerPod);

            var phase =
                Assert.Single(plan.FailurePhases);

            Assert.Equal(
                1,
                phase.Order);
            Assert.Equal(
                RuntimePoolCrashFailureKind.KubernetesPod,
                phase.FailureKind);
            Assert.Equal(
                "pod-failure",
                phase.ImpactedTenantRole);
        }

        /// <summary>
        /// Verifies that the scenario always reserves one healthy Pod outside the Pod-failure boundary.
        /// </summary>
        [Fact]
        public void Constructor_Should_Reject_One_Pod_Topology()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new RuntimePoolCrashRecoveryScenarioPlan(
                    initialPodCount: 1,
                    maximumPodCount: 1,
                    initialRuntimeCountPerPod: 3,
                    maximumRuntimeCountPerPod: 3,
                    failurePhases: CreateValidFailurePhases()));
        }

        /// <summary>
        /// Verifies that the runtime-kill phase always has at least one healthy sibling in the same Pod.
        /// </summary>
        [Fact]
        public void Constructor_Should_Reject_One_Runtime_Per_Pod()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new RuntimePoolCrashRecoveryScenarioPlan(
                    initialPodCount: 2,
                    maximumPodCount: 2,
                    initialRuntimeCountPerPod: 1,
                    maximumRuntimeCountPerPod: 1,
                    failurePhases: CreateValidFailurePhases()));
        }

        /// <summary>
        /// Verifies that the process failure cannot execute after the Pod deletion in the all-in-one proof.
        /// </summary>
        [Fact]
        public void Constructor_Should_Reject_Reversed_Failure_Order()
        {
            var failurePhases =
                new[]
                {
                    new RuntimePoolCrashFailurePhase(
                        order: 1,
                        failureKind:
                            RuntimePoolCrashFailureKind.KubernetesPod,
                        impactedTenantRole:
                            "pod-failure"),
                    new RuntimePoolCrashFailurePhase(
                        order: 2,
                        failureKind:
                            RuntimePoolCrashFailureKind.RuntimeProcess,
                        impactedTenantRole:
                            "runtime-failure")
                };

            Assert.Throws<ArgumentException>(
                () => new RuntimePoolCrashRecoveryScenarioPlan(
                    initialPodCount: 2,
                    maximumPodCount: 2,
                    initialRuntimeCountPerPod: 3,
                    maximumRuntimeCountPerPod: 3,
                    failurePhases: failurePhases));
        }

        /// <summary>
        /// Verifies that the two physical failures cannot target the same impacted tenant role.
        /// </summary>
        [Fact]
        public void Constructor_Should_Reject_Duplicate_Impacted_Tenant_Roles()
        {
            var failurePhases =
                new[]
                {
                    new RuntimePoolCrashFailurePhase(
                        order: 1,
                        failureKind:
                            RuntimePoolCrashFailureKind.RuntimeProcess,
                        impactedTenantRole:
                            "impacted"),
                    new RuntimePoolCrashFailurePhase(
                        order: 2,
                        failureKind:
                            RuntimePoolCrashFailureKind.KubernetesPod,
                        impactedTenantRole:
                            "impacted")
                };

            Assert.Throws<ArgumentException>(
                () => new RuntimePoolCrashRecoveryScenarioPlan(
                    initialPodCount: 2,
                    maximumPodCount: 2,
                    initialRuntimeCountPerPod: 3,
                    maximumRuntimeCountPerPod: 3,
                    failurePhases: failurePhases));
        }

        private static IReadOnlyCollection<RuntimePoolCrashFailurePhase>
            CreateValidFailurePhases()
        {
            return new[]
            {
                new RuntimePoolCrashFailurePhase(
                    order: 1,
                    failureKind:
                        RuntimePoolCrashFailureKind.RuntimeProcess,
                    impactedTenantRole:
                        "runtime-failure"),
                new RuntimePoolCrashFailurePhase(
                    order: 2,
                    failureKind:
                        RuntimePoolCrashFailureKind.KubernetesPod,
                    impactedTenantRole:
                        "pod-failure")
            };
        }
    }
}
