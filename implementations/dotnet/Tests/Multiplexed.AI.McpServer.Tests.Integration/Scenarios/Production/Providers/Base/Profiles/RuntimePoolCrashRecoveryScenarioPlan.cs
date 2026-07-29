namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Defines the bounded Runtime Pool topology and ordered failure phases used by one all-in-one crash-recovery proof.
    /// </summary>
    public sealed class RuntimePoolCrashRecoveryScenarioPlan
    {
        private const string RuntimeFailureTenantRole =
            "runtime-failure";

        private const string PodFailureTenantRole =
            "pod-failure";

        /// <summary>
        /// Initializes a new instance of the <see cref="RuntimePoolCrashRecoveryScenarioPlan"/> class.
        /// </summary>
        /// <param name="initialPodCount">The number of Runtime Pool Pods created before workload submission.</param>
        /// <param name="maximumPodCount">The maximum number of Runtime Pool Pods permitted during the scenario.</param>
        /// <param name="initialRuntimeCountPerPod">The initial number of child runtimes hosted by every Pod.</param>
        /// <param name="maximumRuntimeCountPerPod">The maximum number of child runtimes permitted in every Pod.</param>
        /// <param name="failurePhases">The complete ordered physical-failure sequence.</param>
        public RuntimePoolCrashRecoveryScenarioPlan(
            int initialPodCount,
            int maximumPodCount,
            int initialRuntimeCountPerPod,
            int maximumRuntimeCountPerPod,
            IReadOnlyCollection<RuntimePoolCrashFailurePhase> failurePhases)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(
                initialPodCount,
                2);

            ArgumentOutOfRangeException.ThrowIfLessThan(
                maximumPodCount,
                initialPodCount);

            ArgumentOutOfRangeException.ThrowIfLessThan(
                initialRuntimeCountPerPod,
                2);

            ArgumentOutOfRangeException.ThrowIfLessThan(
                maximumRuntimeCountPerPod,
                initialRuntimeCountPerPod);

            ArgumentNullException.ThrowIfNull(
                failurePhases);

            var orderedFailurePhases =
                failurePhases
                    .OrderBy(
                        phase => phase.Order)
                    .ToArray();

            ValidateFailurePhases(
                orderedFailurePhases);

            InitialPodCount = initialPodCount;
            MaximumPodCount = maximumPodCount;
            InitialRuntimeCountPerPod = initialRuntimeCountPerPod;
            MaximumRuntimeCountPerPod = maximumRuntimeCountPerPod;
            FailurePhases = orderedFailurePhases;
        }

        /// <summary>
        /// Gets the number of Runtime Pool Pods created before workload submission.
        /// </summary>
        public int InitialPodCount { get; }

        /// <summary>
        /// Gets the maximum number of Runtime Pool Pods permitted during the scenario.
        /// </summary>
        public int MaximumPodCount { get; }

        /// <summary>
        /// Gets the initial number of child runtimes hosted by every Pod.
        /// </summary>
        public int InitialRuntimeCountPerPod { get; }

        /// <summary>
        /// Gets the maximum number of child runtimes permitted in every Pod.
        /// </summary>
        public int MaximumRuntimeCountPerPod { get; }

        /// <summary>
        /// Gets the complete ordered physical-failure sequence.
        /// </summary>
        public IReadOnlyList<RuntimePoolCrashFailurePhase> FailurePhases { get; }

        /// <summary>
        /// Creates the canonical all-in-one Runtime Pool proof plan.
        /// </summary>
        /// <param name="initialPodCount">The number of Runtime Pool Pods created before workload submission.</param>
        /// <param name="maximumPodCount">The maximum number of Runtime Pool Pods permitted during the scenario.</param>
        /// <param name="initialRuntimeCountPerPod">The initial number of child runtimes hosted by every Pod.</param>
        /// <param name="maximumRuntimeCountPerPod">The maximum number of child runtimes permitted in every Pod.</param>
        /// <returns>
        /// A plan that first kills one exact runtime process and then deletes one distinct Kubernetes Pod.
        /// </returns>
        public static RuntimePoolCrashRecoveryScenarioPlan CreateAllInOne(
            int initialPodCount,
            int maximumPodCount,
            int initialRuntimeCountPerPod,
            int maximumRuntimeCountPerPod)
        {
            return new RuntimePoolCrashRecoveryScenarioPlan(
                initialPodCount,
                maximumPodCount,
                initialRuntimeCountPerPod,
                maximumRuntimeCountPerPod,
                new[]
                {
                    new RuntimePoolCrashFailurePhase(
                        order: 1,
                        failureKind:
                            RuntimePoolCrashFailureKind.RuntimeProcess,
                        impactedTenantRole:
                            RuntimeFailureTenantRole),
                    new RuntimePoolCrashFailurePhase(
                        order: 2,
                        failureKind:
                            RuntimePoolCrashFailureKind.KubernetesPod,
                        impactedTenantRole:
                            PodFailureTenantRole)
                });
        }

        private static void ValidateFailurePhases(
            IReadOnlyList<RuntimePoolCrashFailurePhase> failurePhases)
        {
            if (failurePhases.Count != 2)
            {
                throw new ArgumentException(
                    "The all-in-one Runtime Pool proof requires exactly two failure phases.",
                    nameof(failurePhases));
            }

            var runtimeFailurePhase =
                failurePhases[0];

            var podFailurePhase =
                failurePhases[1];

            if (runtimeFailurePhase.Order != 1 ||
                runtimeFailurePhase.FailureKind !=
                    RuntimePoolCrashFailureKind.RuntimeProcess)
            {
                throw new ArgumentException(
                    "The first failure phase must kill one exact runtime process.",
                    nameof(failurePhases));
            }

            if (podFailurePhase.Order != 2 ||
                podFailurePhase.FailureKind !=
                    RuntimePoolCrashFailureKind.KubernetesPod)
            {
                throw new ArgumentException(
                    "The second failure phase must delete one exact Kubernetes Pod.",
                    nameof(failurePhases));
            }

            if (StringComparer.Ordinal.Equals(
                    runtimeFailurePhase.ImpactedTenantRole,
                    podFailurePhase.ImpactedTenantRole))
            {
                throw new ArgumentException(
                    "The runtime-failure and Pod-failure phases must target distinct impacted tenant roles.",
                    nameof(failurePhases));
            }
        }
    }
}
