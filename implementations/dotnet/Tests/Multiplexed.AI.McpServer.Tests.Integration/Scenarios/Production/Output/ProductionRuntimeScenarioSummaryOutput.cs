using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using System.Globalization;
using System.Text;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Output
{
    /// <summary>
    /// Writes human-readable production runtime scenario summaries.
    /// </summary>
    public static class ProductionRuntimeScenarioSummaryOutput
    {
        /// <summary>
        /// Writes the intro for the concurrent multi-instance runtime recovery scenario.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="scenario">The production runtime scenario definition.</param>
        public static void WriteConcurrentMultiInstanceRecoveryIntro(
            ITestOutputHelper output,
            ProductionRuntimeScenarioDefinition scenario)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(scenario);

            var tenants = scenario.Tenants.ToArray();
            var tenantA = tenants.FirstOrDefault(tenant => string.Equals(tenant.TenantId, "tenant-concurrent-a", StringComparison.Ordinal));
            var tenantB = tenants.FirstOrDefault(tenant => string.Equals(tenant.TenantId, "tenant-concurrent-b", StringComparison.Ordinal));
            var witnessTenants = tenants
                .Where(tenant =>
                    !string.Equals(tenant.TenantId, "tenant-concurrent-a", StringComparison.Ordinal) &&
                    !string.Equals(tenant.TenantId, "tenant-concurrent-b", StringComparison.Ordinal))
                .ToArray();

            if (tenantA is null || tenantB is null)
            {
                WriteGenericIntro(output, scenario);
                return;
            }

            var builder = new StringBuilder();

            builder.AppendLine();
            builder.AppendLine("# SCENARIO INTRO - CONCURRENT MULTI-INSTANCE RUNTIME RECOVERY");
            builder.AppendLine();
            builder.AppendLine(
                $"This test configures two independent tenants — '{tenantA.TenantId}' and '{tenantB.TenantId}' — " +
                $"each with its own runtime capacity limit ({tenantA.MaxRuntimeInstances.ToString(CultureInfo.InvariantCulture)} runtime instances max for A, " +
                $"{tenantB.MaxRuntimeInstances.ToString(CultureInfo.InvariantCulture)} for B), its own runtime instance prefix " +
                $"('{tenantA.RuntimeInstanceIdPrefix}' and '{tenantB.RuntimeInstanceIdPrefix}'), and " +
                $"{tenantA.WorkerCountPerInstance.ToString(CultureInfo.InvariantCulture)} worker per instance to force real contention.");

            builder.AppendLine();

            builder.AppendLine(
                $"Each tenant submits {DescribeRunCount(tenantA)} with {tenantA.Run.StepCount.ToString(CultureInfo.InvariantCulture)} steps, " +
                $"{tenantA.Run.DelayMs.ToString(CultureInfo.InvariantCulture)}ms of delay per step, and retention set to " +
                $"{tenantA.Run.EnableRetention.ToString(CultureInfo.InvariantCulture)}. " +
                "The workload is long enough to interrupt execution in the middle of the run, not only during startup.");

            builder.AppendLine();

            builder.AppendLine(
                $"The scenario runs in '{scenario.HostCreationMode}' host-creation mode with '{scenario.PersistenceProfile}' persistence " +
                $"and '{scenario.ObservabilityProfile}' observability. In process-host mode, runtime capacity is created as real external .NET runtime host processes, " +
                "not as in-memory fixtures.");

            builder.AppendLine();

            if (witnessTenants.Length > 0)
            {
                builder.AppendLine(
                    $"An optional witness tenant is also present — '{string.Join(", ", witnessTenants.Select(tenant => tenant.TenantId))}'. " +
                    "It runs in parallel without being intentionally failed, and acts as a negative-control tenant to prove that recovery side effects do not leak into unrelated tenants.");
            }
            else
            {
                builder.AppendLine(
                    "No witness tenant is configured for this variant. The proof focuses on concurrent recovery isolation between the two failed tenants.");
            }

            builder.AppendLine();

            builder.AppendLine(
                $"What happens: runtime instances for '{tenantA.TenantId}' and '{tenantB.TenantId}' are made unsafe while each tenant owns real recoverable work. " +
                "The control plane must detect the failed capacity, create or select safe replacement runtime capacity, and redispatch all recovered work without mixing incidents, " +
                "without cross-tenant leakage, without duplicate recovery, and without redispatching work back to the failed runtime.");

            builder.AppendLine();

            builder.AppendLine("Scenario contract:");
            AppendContract(builder, "All submitted runs must complete.", scenario.Assertions.AssertAllRunsCompleted);
            AppendContract(builder, "Tenant isolation must hold.", scenario.Assertions.AssertTenantIsolation);
            AppendContract(builder, "Scale-out must be proven.", scenario.Assertions.AssertScaleOut);
            AppendContract(builder, "Runtime instance limits must be respected.", scenario.Assertions.AssertMaxRuntimeInstances);
            AppendContract(builder, "Ledger evidence must exist.", scenario.Assertions.AssertLedger);
            AppendContract(builder, "Trace evidence must exist.", scenario.Assertions.AssertTrace);

            builder.AppendLine();

            builder.AppendLine("Timeout budget:");
            builder.AppendLine($"  ScaleOutTimeout: {scenario.ScaleOutTimeout}");
            builder.AppendLine($"  DispatchTimeout: {scenario.DispatchTimeout}");
            builder.AppendLine($"  CompletionTimeout: {scenario.CompletionTimeout}");

            output.WriteLine(builder.ToString());
        }

        /// <summary>
        /// Writes a generic intro when the expected tenant shape is not present.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="scenario">The production runtime scenario definition.</param>
        public static void WriteGenericIntro(
            ITestOutputHelper output,
            ProductionRuntimeScenarioDefinition scenario)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(scenario);

            var builder = new StringBuilder();

            builder.AppendLine();
            builder.AppendLine("# SCENARIO INTRO - PRODUCTION RUNTIME SCENARIO");
            builder.AppendLine();
            builder.AppendLine(
                $"This test runs scenario '{scenario.Name}' with {scenario.Tenants.Count.ToString(CultureInfo.InvariantCulture)} tenant(s), " +
                $"'{scenario.PersistenceProfile}' persistence, '{scenario.ObservabilityProfile}' observability, " +
                $"'{scenario.HostCreationMode}' host creation, and '{scenario.SubmitMode}' submission.");

            builder.AppendLine();

            builder.AppendLine("Tenant/runtime setup:");

            foreach (var tenant in scenario.Tenants)
            {
                builder.AppendLine(
                    $"  - tenant='{tenant.TenantId}', group='{tenant.TenantGroupId}', runtimePrefix='{tenant.RuntimeInstanceIdPrefix}', " +
                    $"maxRuntimeInstances={tenant.MaxRuntimeInstances.ToString(CultureInfo.InvariantCulture)}, " +
                    $"workersPerInstance={tenant.WorkerCountPerInstance.ToString(CultureInfo.InvariantCulture)}, " +
                    $"maxConcurrentRunsPerInstance={tenant.MaxConcurrentRunsPerInstance.ToString(CultureInfo.InvariantCulture)}, " +
                    $"localQueueCapacity={tenant.LocalQueueCapacity.ToString(CultureInfo.InvariantCulture)}, " +
                    $"runCount={tenant.Run.RunCount.ToString(CultureInfo.InvariantCulture)}, " +
                    $"stepCount={tenant.Run.StepCount.ToString(CultureInfo.InvariantCulture)}, " +
                    $"delayMs={tenant.Run.DelayMs.ToString(CultureInfo.InvariantCulture)}, " +
                    $"retention={tenant.Run.EnableRetention.ToString(CultureInfo.InvariantCulture)}.");
            }

            output.WriteLine(builder.ToString());
        }

        private static string DescribeRunCount(
            ProductionTenantScenarioDefinition tenant)
        {
            return tenant.Run.RunCount == 1
                ? "one run"
                : $"{tenant.Run.RunCount.ToString(CultureInfo.InvariantCulture)} runs";
        }

        private static void AppendContract(
            StringBuilder builder,
            string label,
            bool enabled)
        {
            builder.AppendLine($"  - [{(enabled ? "ON" : "OFF")}] {label}");
        }
    }
}