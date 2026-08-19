using System.Collections.Generic;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions
{
    /// <summary>
    /// Describes the workload submitted for one tenant inside a production runtime scenario.
    /// </summary>
    public sealed record ProductionRunScenarioDefinition
    {
        /// <summary>
        /// Gets the number of shared runs to submit for the tenant.
        /// </summary>
        public required int RunCount { get; init; }

        /// <summary>
        /// Gets the number of steps in each submitted pipeline.
        /// </summary>
        public required int StepCount { get; init; }

        /// <summary>
        /// Gets the artificial delay, in milliseconds, added to the test pipeline input.
        /// </summary>
        public int DelayMs { get; init; }

        /// <summary>
        /// Gets the flaky step interval used by the test pipeline.
        /// </summary>
        public int FlakyStepInterval { get; init; }

        /// <summary>
        /// Gets a value indicating whether retention should be enabled for submitted run requests.
        /// </summary>
        public bool EnableRetention { get; init; } = true;

        /// <summary>
        /// Gets the number of nested child DAG levels added to the production-test pipeline.
        /// </summary>
        /// <remarks>
        /// The default value is zero so every historical production scenario keeps its exact pipeline shape and
        /// execution behavior. Positive values opt in to deterministic child DAG composition for dedicated tests.
        /// </remarks>
        public int ChildDepth { get; init; } = 0;

        /// <summary>
        /// Gets the optional physical runtime failure injected into one nested child execution.
        /// </summary>
        /// <remarks>
        /// The default is <see langword="null"/>, preserving every historical production scenario. A failure
        /// injection is valid only when <see cref="ChildDepth"/> is positive and the target depth is within the
        /// configured nested child chain.
        /// </remarks>
        public ProductionChildDagFailureInjectionDefinition? ChildRuntimeFailure { get; init; }

        /// <summary>
        /// Gets additional input values merged into the submitted pipeline input.
        /// </summary>
        public IReadOnlyDictionary<string, object?> Input { get; init; } =
            new Dictionary<string, object?>();
    }
}
