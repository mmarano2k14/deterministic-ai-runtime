namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Defines the bounded Kubernetes Runtime Pool topology independently from any failure plan.
    /// </summary>
    public sealed class KubernetesRuntimePoolScenarioTopology
    {
        /// <summary>
        /// Initializes a bounded Kubernetes Runtime Pool topology.
        /// </summary>
        /// <param name="initialPodCount">The number of Runtime Pool Pods created before workload submission.</param>
        /// <param name="maximumPodCount">The maximum number of Runtime Pool Pods permitted during the scenario.</param>
        /// <param name="initialRuntimeCountPerPod">The initial number of child runtimes hosted by every Pod.</param>
        /// <param name="maximumRuntimeCountPerPod">The maximum number of child runtimes permitted in every Pod.</param>
        public KubernetesRuntimePoolScenarioTopology(
            int initialPodCount,
            int maximumPodCount,
            int initialRuntimeCountPerPod,
            int maximumRuntimeCountPerPod)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(initialPodCount, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumPodCount, initialPodCount);
            ArgumentOutOfRangeException.ThrowIfLessThan(initialRuntimeCountPerPod, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(
                maximumRuntimeCountPerPod,
                initialRuntimeCountPerPod);

            InitialPodCount = initialPodCount;
            MaximumPodCount = maximumPodCount;
            InitialRuntimeCountPerPod = initialRuntimeCountPerPod;
            MaximumRuntimeCountPerPod = maximumRuntimeCountPerPod;
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
    }
}
