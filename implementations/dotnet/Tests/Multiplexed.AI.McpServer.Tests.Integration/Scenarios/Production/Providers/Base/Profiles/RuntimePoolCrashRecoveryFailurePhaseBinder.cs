using System;
using System.Collections.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles
{
    /// <summary>
    /// Binds the ordered Runtime Pool failure plan to impacted tenants without changing historical profiles.
    /// </summary>
    public static class RuntimePoolCrashRecoveryFailurePhaseBinder
    {
        /// <summary>
        /// Resolves Runtime Pool failure phases for the supplied profile.
        /// </summary>
        /// <param name="profile">The runtime scenario profile.</param>
        /// <param name="impactedTenants">The impacted tenants in deterministic scenario order.</param>
        /// <returns>
        /// A tenant-id keyed failure-phase map for Runtime Pool profiles, or an empty map for historical profiles.
        /// </returns>
        public static IReadOnlyDictionary<string, RuntimePoolCrashFailurePhase>
            Bind(
                IProcessHostScenarioRuntimeProfile profile,
                IReadOnlyList<ProductionTenantScenarioDefinition> impactedTenants)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(impactedTenants);

            if (profile is not IRuntimePoolCrashRecoveryScenarioRuntimeProfile
                runtimePoolProfile)
            {
                return new Dictionary<string, RuntimePoolCrashFailurePhase>(
                    StringComparer.Ordinal);
            }

            return Bind(
                runtimePoolProfile,
                impactedTenants);
        }

        /// <summary>
        /// Resolves the ordered Runtime Pool failure phases for impacted tenants.
        /// </summary>
        /// <param name="profile">The Runtime Pool scenario profile.</param>
        /// <param name="impactedTenants">The impacted tenants in deterministic scenario order.</param>
        /// <returns>A tenant-id keyed failure-phase map.</returns>
        public static IReadOnlyDictionary<string, RuntimePoolCrashFailurePhase>
            Bind(
                IRuntimePoolCrashRecoveryScenarioRuntimeProfile profile,
                IReadOnlyList<ProductionTenantScenarioDefinition> impactedTenants)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(impactedTenants);

            var phases =
                profile.CrashRecoveryPlan.FailurePhases;

            if (impactedTenants.Count != phases.Count)
            {
                throw new InvalidOperationException(
                    $"Runtime Pool failure phase count '{phases.Count}' does not match impacted tenant count '{impactedTenants.Count}'.");
            }

            var result =
                new Dictionary<string, RuntimePoolCrashFailurePhase>(
                    StringComparer.Ordinal);

            for (var index = 0; index < phases.Count; index++)
            {
                var tenant =
                    impactedTenants[index]
                    ?? throw new InvalidOperationException(
                        $"Impacted tenant at index '{index}' is null.");

                ArgumentException.ThrowIfNullOrWhiteSpace(
                    tenant.TenantId);

                if (!result.TryAdd(
                        tenant.TenantId,
                        phases[index]))
                {
                    throw new InvalidOperationException(
                        $"Impacted tenant id '{tenant.TenantId}' is duplicated.");
                }
            }

            return result;
        }
    }
}
