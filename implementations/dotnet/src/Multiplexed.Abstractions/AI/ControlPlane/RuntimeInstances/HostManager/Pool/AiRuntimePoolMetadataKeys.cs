namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Pool
{
    /// <summary>
    /// Defines stable metadata keys used by Runtime Pool control-plane components.
    /// </summary>
    /// <remarks>
    /// These keys preserve the existing Runtime Pool metadata wire and durable values.
    /// They centralize semantic ownership only and do not change runtime behavior.
    /// </remarks>
    public static class AiRuntimePoolMetadataKeys
    {
        /// <summary>Gets the Runtime Pool identifier metadata key.</summary>
        public const string PoolId = "runtime.pool.id";

        /// <summary>Gets the Runtime Pool host identifier metadata key.</summary>
        public const string HostId = "runtime.pool.host.id";

        /// <summary>Gets the metadata key indicating a Runtime Pool routing failure.</summary>
        public const string RoutingFailure = "runtime.pool.routing.failure";

        /// <summary>Gets the Runtime Pool route status metadata key.</summary>
        public const string RouteStatus = "runtime.pool.route.status";

        /// <summary>Gets the primary runtime instance identifier metadata key.</summary>
        public const string PrimaryRuntimeInstanceId = "runtime.pool.primaryRuntimeInstanceId";

        /// <summary>Gets the Runtime Pool pod request identifier metadata key.</summary>
        public const string PodRequestId = "runtime.pool.podRequestId";

        /// <summary>Gets the planned runtime count metadata key.</summary>
        public const string PlannedRuntimeCount = "runtime.pool.plannedRuntimeCount";

        /// <summary>Gets the planned runtime instance identifiers metadata key.</summary>
        public const string PlannedRuntimeInstanceIds = "runtime.pool.plannedRuntimeInstanceIds";

        /// <summary>Gets the physical pod count metadata key.</summary>
        public const string PhysicalPodCount = "runtime.pool.physicalPodCount";

        /// <summary>Gets the maximum pod count metadata key.</summary>
        public const string MaximumPodCount = "runtime.pool.maximumPodCount";

        /// <summary>Gets the capacity-already-satisfied metadata key.</summary>
        public const string CapacityAlreadySatisfied = "runtime.pool.capacityAlreadySatisfied";

        /// <summary>Gets the pod-creation status metadata key.</summary>
        public const string PodCreationStatus = "runtime.pool.podCreation.status";

        /// <summary>Gets the pod-creation host request identifier metadata key.</summary>
        public const string PodCreationHostRequestId = "runtime.pool.podCreation.hostRequestId";

        /// <summary>Gets the pod-creation Kubernetes pod UID metadata key.</summary>
        public const string PodCreationPodUid = "runtime.pool.podCreation.podUid";

        /// <summary>Gets the pod-creation runtime count metadata key.</summary>
        public const string PodCreationRuntimeCount = "runtime.pool.podCreation.runtimeCount";

        /// <summary>Gets the pod-creation active pod count metadata key.</summary>
        public const string PodCreationActivePodCount = "runtime.pool.podCreation.activePodCount";

        /// <summary>Gets the pod-creation reserved pod creation count metadata key.</summary>
        public const string PodCreationReservedPodCreationCount =
            "runtime.pool.podCreation.reservedPodCreationCount";

        /// <summary>Gets the pod-creation maximum pod count metadata key.</summary>
        public const string PodCreationMaximumPodCount = "runtime.pool.podCreation.maximumPodCount";

        /// <summary>
        /// Gets the camel-case runtime pool identifier metadata key used by compatibility and diagnostic payloads.
        /// </summary>
        public const string CamelCasePoolId = "poolId";
    }
}
