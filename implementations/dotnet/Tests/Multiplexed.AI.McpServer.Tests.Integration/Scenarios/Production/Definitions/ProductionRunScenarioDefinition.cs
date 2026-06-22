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
        /// Gets additional input values merged into the submitted pipeline input.
        /// </summary>
        public IReadOnlyDictionary<string, object?> Input { get; init; } =
            new Dictionary<string, object?>();
    }
}