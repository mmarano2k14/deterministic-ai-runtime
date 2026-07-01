using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using System.Collections.Generic;
using System.Net.Http;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Models
{
    /// <summary>
    /// Defines the kind of real work observed on a runtime instance before crash recovery.
    /// </summary>
    public enum RealRuntimeCrashWorkKind
    {
        /// <summary>
        /// The work has been assigned to a runtime local queue but has not yet started a durable DAG execution.
        /// </summary>
        LocalQueued,

        /// <summary>
        /// The work has started a durable DAG execution and must resume the same execution identifier after recovery.
        /// </summary>
        InFlightExecution
    }

    /// <summary>
    /// Captures one real runtime work item before a runtime process crash.
    /// </summary>
    public sealed record RealRuntimeCrashWorkProof
    {
        /// <summary>
        /// Gets the work kind.
        /// </summary>
        public required RealRuntimeCrashWorkKind Kind { get; init; }

        /// <summary>
        /// Gets the shared run record before the runtime process crash.
        /// </summary>
        public required AiSharedRunRecord SharedRun { get; init; }

        /// <summary>
        /// Gets the shared run identifier.
        /// </summary>
        public required string SharedRunId { get; init; }

        /// <summary>
        /// Gets the original local runtime run identifier.
        /// </summary>
        public required string LocalRunId { get; init; }

        /// <summary>
        /// Gets the durable DAG execution identifier when the work is already in flight.
        /// </summary>
        public string? ExecutionId { get; init; }

        /// <summary>
        /// Gets the pipeline name used by this work item.
        /// </summary>
        public required string PipelineName { get; init; }
    }

    /// <summary>
    /// Captures the real assigned work inventory observed on one runtime instance before process crash.
    /// </summary>
    public sealed record RealRuntimeCrashAssignedWorkInventoryProof
    {
        /// <summary>
        /// Gets the tenant scenario definition.
        /// </summary>
        public required ProductionTenantScenarioDefinition Tenant { get; init; }

        /// <summary>
        /// Gets the tenant-scoped MCP client.
        /// </summary>
        public required McpTestClient Mcp { get; init; }

        /// <summary>
        /// Gets the runtime instance identifier that will be killed.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the real work items assigned to the runtime before the crash.
        /// </summary>
        public required IReadOnlyList<RealRuntimeCrashWorkProof> Works { get; init; }

        /// <summary>
        /// Gets the in-flight execution work items.
        /// </summary>
        public IReadOnlyList<RealRuntimeCrashWorkProof> InFlightExecutions =>
            this.Works
                .Where(work => work.Kind == RealRuntimeCrashWorkKind.InFlightExecution)
                .ToArray();

        /// <summary>
        /// Gets the local queued work items.
        /// </summary>
        public IReadOnlyList<RealRuntimeCrashWorkProof> LocalQueuedRuns =>
            this.Works
                .Where(work => work.Kind == RealRuntimeCrashWorkKind.LocalQueued)
                .ToArray();
    }

    /// <summary>
    /// Captures one recovered work item after redispatch.
    /// </summary>
    public sealed record RealRuntimeCrashRecoveredWorkProof
    {
        /// <summary>
        /// Gets the original work item before crash.
        /// </summary>
        public required RealRuntimeCrashWorkProof Original { get; init; }

        /// <summary>
        /// Gets the redispatched shared run record.
        /// </summary>
        public required AiSharedRunRecord RedispatchedRun { get; init; }

        /// <summary>
        /// Gets the replacement runtime instance identifier.
        /// </summary>
        public required string ReplacementRuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the replacement local runtime run identifier.
        /// </summary>
        public required string ReplacementLocalRunId { get; init; }

        /// <summary>
        /// Gets the recovered durable execution identifier when available.
        /// </summary>
        public string? RecoveredExecutionId { get; init; }
    }

    /// <summary>
    /// Captures the recovery proof for one failed runtime instance and all work assigned to it.
    /// </summary>
    public sealed record RealRuntimeCrashFailedRuntimeRecoveryProof
    {
        /// <summary>
        /// Gets the failed runtime inventory before crash.
        /// </summary>
        public required RealRuntimeCrashAssignedWorkInventoryProof FailedInventory { get; init; }

        /// <summary>
        /// Gets all recovered work items after redispatch.
        /// </summary>
        public required IReadOnlyList<RealRuntimeCrashRecoveredWorkProof> RecoveredWorks { get; init; }
    }

    /// <summary>
    /// Captures MCP clients and HTTP resources for tenant-scoped real runtime crash tests.
    /// </summary>
    public sealed record RealRuntimeCrashTenantClient
    {
        /// <summary>
        /// Gets the tenant scenario definition.
        /// </summary>
        public required ProductionTenantScenarioDefinition Tenant { get; init; }

        /// <summary>
        /// Gets the tenant-scoped MCP client.
        /// </summary>
        public required McpTestClient Mcp { get; init; }

        /// <summary>
        /// Gets the HTTP client backing the tenant-scoped MCP client.
        /// </summary>
        public required HttpClient HttpClient { get; init; }
    }

    /// <summary>
    /// Captures a safe tenant runtime that must remain untouched during recovery.
    /// </summary>
    public sealed record RealRuntimeCrashSafeTenantProof
    {
        /// <summary>
        /// Gets the tenant scenario definition.
        /// </summary>
        public required ProductionTenantScenarioDefinition Tenant { get; init; }

        /// <summary>
        /// Gets the tenant-scoped MCP client.
        /// </summary>
        public required McpTestClient Mcp { get; init; }

        /// <summary>
        /// Gets the safe runtime instance identifier.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the safe work items that must not be recovered.
        /// </summary>
        public required IReadOnlyList<RealRuntimeCrashWorkProof> Works { get; init; }
    }

    /// <summary>
    /// Captures the full multi-tenant real process-host crash recovery proof.
    /// </summary>
    public sealed record RealRuntimeCrashMultiTenantRecoveryProof
    {
        /// <summary>
        /// Gets the failed runtime recovery proofs.
        /// </summary>
        public required IReadOnlyList<RealRuntimeCrashFailedRuntimeRecoveryProof> FailedRuntimeRecoveries { get; init; }

        /// <summary>
        /// Gets the safe tenant proof when a safe tenant is part of the scenario.
        /// </summary>
        public RealRuntimeCrashSafeTenantProof? SafeTenant { get; init; }
    }
}