using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results
{
    /// <summary>
    /// Captures one physical runtime-boundary failure proof during nested Child DAG execution.
    /// </summary>
    public sealed record ProductionChildDagRuntimeFailureResult
    {
        /// <summary>
        /// Gets the logical execution target whose physical runtime boundary was destroyed.
        /// </summary>
        public required ProductionChildDagFailureTarget FailureTarget { get; init; }

        /// <summary>
        /// Gets the one-based nested child depth that establishes the durable failure window.
        /// </summary>
        public required int TargetDepth { get; init; }

        /// <summary>
        /// Gets the durable parent execution identifier associated with the targeted child relation.
        /// </summary>
        public required string ParentExecutionId { get; init; }

        /// <summary>
        /// Gets the durable child execution identifier associated with the targeted relation.
        /// </summary>
        public required string ChildExecutionId { get; init; }

        /// <summary>
        /// Gets the physical runtime instance destroyed by the failure proof.
        /// </summary>
        public required string OriginalRuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the replacement physical runtime instance when the failed execution itself is recovered.
        /// </summary>
        public string? RecoveredRuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the immutable physical host identity that contained the destroyed runtime, when available.
        /// </summary>
        public string? OriginalHostId { get; init; }

        /// <summary>
        /// Gets the immutable physical host identity that contains the recovered execution, when available.
        /// </summary>
        public string? RecoveredHostId { get; init; }

        /// <summary>
        /// Gets the local runtime run identifier associated with the destroyed runtime.
        /// </summary>
        public required string OriginalLocalRunId { get; init; }

        /// <summary>
        /// Gets the replacement local runtime run identifier when the failed execution itself is recovered.
        /// </summary>
        public string? RecoveredLocalRunId { get; init; }

        /// <summary>
        /// Gets the runtime instance that was observed actively executing the child while the parked parent boundary
        /// was destroyed, when the proof targets the parent runtime.
        /// </summary>
        public string? ObservedChildRuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the immutable host identity that contained the independently active child while the parent boundary
        /// was destroyed, when available.
        /// </summary>
        public string? ObservedChildHostId { get; init; }

        /// <summary>
        /// Gets the child local-run identifier observed before and after destruction of the parked parent boundary.
        /// </summary>
        public string? ObservedChildLocalRunId { get; init; }

        /// <summary>
        /// Gets a value indicating whether the parent call-site was durably parked before the physical kill.
        /// </summary>
        public required bool ParentWaitingForExternalObserved { get; init; }

        /// <summary>
        /// Gets a value indicating whether the parked parent had released recoverable runtime ownership before kill.
        /// </summary>
        public required bool ParentRuntimeCapacityReleased { get; init; }

        /// <summary>
        /// Gets a value indicating whether the physical runtime kill was acknowledged by the process-control authority.
        /// </summary>
        public required bool KillSucceeded { get; init; }
    }
}
