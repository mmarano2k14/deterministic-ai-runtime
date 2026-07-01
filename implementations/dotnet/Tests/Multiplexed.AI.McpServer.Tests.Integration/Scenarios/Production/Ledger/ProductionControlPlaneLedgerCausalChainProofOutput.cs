using System;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Ledger
{
    /// <summary>
    /// Writes reusable control-plane causal-chain ledger proof output.
    /// </summary>
    public static class ProductionControlPlaneLedgerCausalChainProofOutput
    {
        /// <summary>
        /// Writes the control-plane causal-chain proof.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="controlPlaneId">The control-plane identifier.</param>
        /// <param name="tenantAId">The first tenant identifier.</param>
        /// <param name="tenantBId">The second tenant identifier.</param>
        /// <param name="failedRuntimeAId">The first failed runtime instance identifier.</param>
        /// <param name="failedRuntimeBId">The second failed runtime instance identifier.</param>
        /// <param name="proof">The causal-chain proof result.</param>
        /// <param name="crossTenantLedgerLeakDetected">Whether a cross-tenant ledger leak was detected.</param>
        /// <param name="infraLedgerValidated">Whether infra ledger evidence was validated.</param>
        public static void Write(
            ITestOutputHelper output,
            string controlPlaneId,
            string tenantAId,
            string tenantBId,
            string failedRuntimeAId,
            string failedRuntimeBId,
            ProductionControlPlaneLedgerCausalChainProofResult proof,
            bool crossTenantLedgerLeakDetected,
            bool infraLedgerValidated)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(proof);

            output.WriteLine(string.Empty);
            output.WriteLine("# CONTROL PLANE LEDGER CAUSAL CHAIN PROOF - PRODUCTION RUNTIME RECOVERY");
            output.WriteLine($"ControlPlaneId: {controlPlaneId}");
            output.WriteLine($"RecoveredWork: {proof.ActualRecoveredWorkCount}/{proof.ExpectedRecoveredWorkCount}");
            output.WriteLine($"TenantA: {tenantAId}");
            output.WriteLine($"TenantB: {tenantBId}");
            output.WriteLine($"FailedRuntimeA: {failedRuntimeAId}");
            output.WriteLine($"FailedRuntimeB: {failedRuntimeBId}");
            output.WriteLine("Proof phases:");
            output.WriteLine($"[PASS] 1. Scale-out request persisted records='{proof.ScaleOutRequestPersistedCount}'");
            output.WriteLine($"[PASS] 2. Scale-out watcher observed request records='{proof.ScaleOutWatcherObservedCount}'");
            output.WriteLine($"[PASS] 3. Provider selected records='{proof.ProviderSelectedCount}'");
            output.WriteLine($"[PASS] 4. Runtime host manager created host records='{proof.RuntimeHostCreatedCount}'");
            output.WriteLine($"[PASS] 5. Process runtime host started records='{proof.ProcessRuntimeHostStartedCount}'");
            output.WriteLine($"[PASS] 6. Runtime capacity became visible records='{proof.RuntimeCapacityVisibleCount}'");
            output.WriteLine($"[PASS] 7. Runtime instance visible through registry/capacity lookup records='{proof.RuntimeRegistryVisibleCount}'");
            output.WriteLine($"[PASS] 8. Failed runtime marked unhealthy records='{proof.FailedRuntimeMarkedUnhealthyCount}'");
            output.WriteLine($"[PASS] 9. Execution recovery reconciled assigned work records='{proof.ExecutionRecoveryReconciledCount}'");
            output.WriteLine($"[PASS] 10. Recovered work redispatched records='{proof.RecoveredWorkRedispatchedCount}'");
            output.WriteLine("Safety invariants:");
            output.WriteLine($"InfraLedgerValidated: {infraLedgerValidated}");
            output.WriteLine($"CrossTenantLedgerLeakDetected: {crossTenantLedgerLeakDetected}");
            output.WriteLine($"ControlPlaneCausalChainValidated: {proof.IsValidated}");
        }
    }
}