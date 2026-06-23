namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions
{
    /// <summary>
    /// Defines which assertions must be evaluated for a production runtime scenario.
    /// </summary>
    /// <remarks>
    /// These options make the production scenario framework reusable across
    /// lightweight and full end-to-end scenarios.
    ///
    /// A complete production scenario can assert execution, scale-out, tenant
    /// isolation, ledger, tracing, and replay visibility. Smaller scenarios can
    /// disable expensive assertions when they only need to validate one specific
    /// behavior.
    /// </remarks>
    public sealed class ProductionRuntimeScenarioAssertionOptions
    {
        /// <summary>
        /// Gets a value indicating whether all submitted runs must reach a terminal completed state.
        /// </summary>
        /// <remarks>
        /// This assertion should usually remain enabled. It verifies the base
        /// execution contract of the scenario.
        /// </remarks>
        public bool AssertAllRunsCompleted { get; init; } = true;

        /// <summary>
        /// Gets a value indicating whether tenant runtime isolation must be asserted.
        /// </summary>
        /// <remarks>
        /// When enabled, each tenant is expected to execute only on runtime
        /// instances that match the tenant isolation policy and configured
        /// runtime instance prefix.
        /// </remarks>
        public bool AssertTenantIsolation { get; init; } = true;

        /// <summary>
        /// Gets a value indicating whether scale-out behavior must be asserted.
        /// </summary>
        /// <remarks>
        /// When enabled, the scenario expects scale-out requests to be created,
        /// fulfilled, and linked to runtime instances.
        /// </remarks>
        public bool AssertScaleOut { get; init; } = true;

        /// <summary>
        /// Gets a value indicating whether max runtime instance limits must be asserted.
        /// </summary>
        /// <remarks>
        /// This assertion verifies that the scenario does not exceed the
        /// tenant-defined maximum runtime instance count.
        /// </remarks>
        public bool AssertMaxRuntimeInstances { get; init; } = true;

        /// <summary>
        /// Gets a value indicating whether decision ledger entries must exist for each execution.
        /// </summary>
        /// <remarks>
        /// This assertion is important for production replay, auditability, and
        /// cross-process observability scenarios.
        /// </remarks>
        public bool AssertLedger { get; init; } = true;

        /// <summary>
        /// Gets a value indicating whether trace events must exist for each execution.
        /// </summary>
        /// <remarks>
        /// In process-host scenarios, trace events may be read through a durable
        /// trace store fallback because child runtime instances do not share the
        /// parent MCP process-local trace timeline.
        /// </remarks>
        public bool AssertTrace { get; init; } = true;

        /// <summary>
        /// Gets a value indicating whether replay reports must be available for each execution.
        /// </summary>
        /// <remarks>
        /// This assertion validates that replay metadata and replay report
        /// generation are available after execution completes.
        /// </remarks>
        public bool AssertReplayReport { get; init; } = true;

        /// <summary>
        /// Gets a value indicating whether replay ledger output must be available for each execution.
        /// </summary>
        /// <remarks>
        /// This assertion validates that replay can expose the decision ledger
        /// associated with an execution.
        /// </remarks>
        public bool AssertReplayLedger { get; init; } = true;

        /// <summary>
        /// Gets a value indicating whether replay timeline output must be available for each execution.
        /// </summary>
        /// <remarks>
        /// This assertion validates that replay can expose a timeline for the
        /// execution, independently from the direct observability trace query.
        /// </remarks>
        public bool AssertReplayTrace { get; init; } = true;
    }
}