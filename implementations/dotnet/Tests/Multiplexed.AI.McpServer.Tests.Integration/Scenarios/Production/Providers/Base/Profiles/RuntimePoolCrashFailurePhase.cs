namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Defines one ordered physical-failure phase in a Runtime Pool crash-recovery scenario.
    /// </summary>
    public sealed class RuntimePoolCrashFailurePhase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RuntimePoolCrashFailurePhase"/> class.
        /// </summary>
        /// <param name="order">The one-based execution order of the failure phase.</param>
        /// <param name="failureKind">The physical failure boundary exercised by the phase.</param>
        /// <param name="impactedTenantRole">
        /// The stable scenario role identifying the impacted tenant assigned to this failure phase.
        /// </param>
        public RuntimePoolCrashFailurePhase(
            int order,
            RuntimePoolCrashFailureKind failureKind,
            string impactedTenantRole)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(
                order,
                1);

            ArgumentException.ThrowIfNullOrWhiteSpace(
                impactedTenantRole);

            Order = order;
            FailureKind = failureKind;
            ImpactedTenantRole = impactedTenantRole;
        }

        /// <summary>
        /// Gets the one-based execution order of the failure phase.
        /// </summary>
        public int Order { get; }

        /// <summary>
        /// Gets the physical failure boundary exercised by the phase.
        /// </summary>
        public RuntimePoolCrashFailureKind FailureKind { get; }

        /// <summary>
        /// Gets the stable scenario role identifying the impacted tenant assigned to this failure phase.
        /// </summary>
        public string ImpactedTenantRole { get; }
    }
}
