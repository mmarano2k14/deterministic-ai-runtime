namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Ledger
{
    /// <summary>
    /// Public proof context rendered with the production control-plane ledger timeline.
    /// </summary>
    public sealed record ProductionControlPlaneLedgerProofContext
    {
        public required string ControlPlaneId { get; init; }
        public required string TenantAId { get; init; }
        public required string TenantBId { get; init; }
        public required string TenantAFailedRuntimeInstanceId { get; init; }
        public required string TenantBFailedRuntimeInstanceId { get; init; }
        public required string ControlRuntimeInstanceId { get; init; }
        public required int ExpectedRecoveredWorkCount { get; init; }
        public required int RecoveredWorkCount { get; init; }
        public bool CrossTenantLeakDetected { get; init; }
        public bool CrossIncidentLeakDetected { get; init; }
        public bool DuplicateRecoveryDetected { get; init; }
        public bool SelfRedispatchDetected { get; init; }
    }
}
