using System;
using System.Collections.Generic;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions
{
    /// <summary>
    /// Describes a provider-agnostic production-grade runtime scenario.
    /// </summary>
    public sealed record ProductionRuntimeScenarioDefinition
    {
        /// <summary>
        /// Gets the scenario name.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Gets the logical control-plane id prefix.
        /// </summary>
        public required string ControlPlaneIdPrefix { get; init; }

        /// <summary>
        /// Gets the tenants participating in the scenario.
        /// </summary>
        public required IReadOnlyList<ProductionTenantScenarioDefinition> Tenants { get; init; }

        /// <summary>
        /// Gets a value indicating whether replay, ledger, and trace must be asserted.
        /// </summary>
        public bool AssertReplayLedgerTrace { get; init; } = true;

        /// <summary>
        /// Gets a value indicating whether retention and snapshots must be enabled.
        /// </summary>
        public bool AssertRetention { get; init; } = true;

        /// <summary>
        /// Gets a value indicating whether max runtime instance limits must be asserted.
        /// </summary>
        public bool AssertMaxRuntimeInstances { get; init; } = true;

        /// <summary>
        /// Gets a value indicating whether tenant runtime isolation must be asserted.
        /// </summary>
        public bool AssertTenantIsolation { get; init; } = true;

        /// <summary>
        /// Gets the timeout used while waiting for scale-out.
        /// </summary>
        public TimeSpan ScaleOutTimeout { get; init; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Gets the timeout used while waiting for dispatch.
        /// </summary>
        public TimeSpan DispatchTimeout { get; init; } = TimeSpan.FromMinutes(3);

        /// <summary>
        /// Gets the timeout used while waiting for terminal completion.
        /// </summary>
        public TimeSpan CompletionTimeout { get; init; } = TimeSpan.FromMinutes(5);
    }
}