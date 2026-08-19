namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions
{
    /// <summary>
    /// Selects the logical execution whose physical runtime boundary is destroyed during a Child DAG production proof.
    /// </summary>
    public enum ProductionChildDagFailureTarget
    {
        /// <summary>
        /// Destroys the runtime that currently owns the targeted child execution and proves recovery of that child.
        /// </summary>
        ChildRuntime = 0,

        /// <summary>
        /// Destroys the original runtime that executed the parent after the parent has durably parked, while the
        /// targeted child remains active on distinct capacity.
        /// </summary>
        ParentRuntimeAfterPark = 1
    }

    /// <summary>
    /// Describes one opt-in physical runtime failure injected during a nested Child DAG execution.
    /// </summary>
    /// <remarks>
    /// Historical production scenarios leave this definition unset. The default target preserves the existing
    /// child-runtime crash proof. The parent-runtime target reuses the same durable child checkpoint as a deterministic
    /// observation window after the parent has parked and released runtime capacity.
    /// </remarks>
    public sealed record ProductionChildDagFailureInjectionDefinition
    {
        /// <summary>
        /// Gets the physical execution target destroyed by the failure proof.
        /// </summary>
        public ProductionChildDagFailureTarget Target { get; init; } =
            ProductionChildDagFailureTarget.ChildRuntime;

        /// <summary>
        /// Gets the one-based child depth whose durable relation and checkpoint establish the failure window.
        /// </summary>
        public required int TargetDepth { get; init; }

        /// <summary>
        /// Gets the one-based child pipeline step index that must durably reach the crash checkpoint before kill.
        /// </summary>
        public required int CrashCheckpointStepIndex { get; init; }
    }
}
