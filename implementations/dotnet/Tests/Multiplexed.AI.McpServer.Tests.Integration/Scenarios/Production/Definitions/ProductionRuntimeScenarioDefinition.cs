using System;
using System.Collections.Generic;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions
{
    /// <summary>
    /// Describes a provider-agnostic production-grade runtime scenario.
    /// </summary>
    /// <remarks>
    /// A production runtime scenario defines what must be executed, which tenants
    /// participate, which runtime/persistence/observability profiles must be used,
    /// and which assertions must be evaluated after execution.
    ///
    /// The scenario definition is intentionally provider-agnostic. Provider-specific
    /// runners, such as HTTP process-host runners, translate this definition into
    /// concrete MCP host settings and runtime host behavior.
    /// </remarks>
    public sealed record ProductionRuntimeScenarioDefinition
    {
        /// <summary>
        /// Gets the scenario name.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Gets the logical control-plane id prefix.
        /// </summary>
        /// <remarks>
        /// The runner may append a unique suffix to this prefix so parallel test
        /// executions do not share control-plane ids.
        /// </remarks>
        public required string ControlPlaneIdPrefix { get; init; }

        /// <summary>
        /// Gets the tenants participating in the scenario.
        /// </summary>
        /// <remarks>
        /// Each tenant definition controls runtime isolation mode, runtime instance
        /// limits, worker count, queue capacity, and the workload submitted for that
        /// tenant.
        /// </remarks>
        public required IReadOnlyList<ProductionTenantScenarioDefinition> Tenants { get; init; }

        /// <summary>
        /// Gets the persistence profile used by the scenario.
        /// </summary>
        /// <remarks>
        /// Multi-process scenarios should generally use <see cref="ProductionRuntimePersistenceProfile.MongoRedis"/>
        /// so the parent MCP control-plane can read state written by child runtime processes.
        /// </remarks>
        public ProductionRuntimePersistenceProfile PersistenceProfile { get; init; } =
            ProductionRuntimePersistenceProfile.MongoRedis;

        /// <summary>
        /// Gets the observability profile used by the scenario.
        /// </summary>
        /// <remarks>
        /// Process-host, attach, and Kubernetes scenarios should generally use
        /// <see cref="ProductionRuntimeObservabilityProfile.DurableMongo"/> because
        /// ledger, replay metadata, and traces must cross process boundaries.
        /// </remarks>
        public ProductionRuntimeObservabilityProfile ObservabilityProfile { get; init; } =
            ProductionRuntimeObservabilityProfile.DurableMongo;

        /// <summary>
        /// Gets the runtime host creation mode used by the scenario.
        /// </summary>
        /// <remarks>
        /// The host creation mode describes how runtime capacity is created when
        /// scale-out is requested.
        /// </remarks>
        public ProductionRuntimeHostCreationMode HostCreationMode { get; init; } =
            ProductionRuntimeHostCreationMode.Process;

        /// <summary>
        /// Gets the shared runtime controller submit mode used by the scenario.
        /// </summary>
        /// <remarks>
        /// Zero-capacity scale-out scenarios usually require
        /// <see cref="ProductionRuntimeSubmitMode.DirectDispatch"/> so admission can
        /// immediately detect missing runtime capacity and create scale-out requests.
        /// </remarks>
        public ProductionRuntimeSubmitMode SubmitMode { get; init; } =
            ProductionRuntimeSubmitMode.DirectDispatch;

        /// <summary>
        /// Gets the assertion options used by the scenario.
        /// </summary>
        /// <remarks>
        /// These options allow small focused scenarios to disable expensive assertions
        /// while full production scenarios can validate execution, scale-out, tenant
        /// isolation, ledger, trace, and replay behavior.
        /// </remarks>
        public ProductionRuntimeScenarioAssertionOptions Assertions { get; init; } =
            new();

        /// <summary>
        /// Gets a value indicating whether replay, ledger, and trace must be asserted.
        /// </summary>
        /// <remarks>
        /// This legacy flag is kept for compatibility while the production scenario
        /// framework migrates to <see cref="Assertions"/>.
        /// </remarks>
        public bool AssertReplayLedgerTrace { get; init; } = true;

        /// <summary>
        /// Gets a value indicating whether retention and snapshots must be enabled.
        /// </summary>
        /// <remarks>
        /// This flag describes the expected retention/snapshot behavior of the
        /// scenario. Runtime settings builders may use this value when deciding
        /// whether snapshot persistence must be enabled.
        /// </remarks>
        public bool AssertRetention { get; init; } = true;

        /// <summary>
        /// Gets a value indicating whether max runtime instance limits must be asserted.
        /// </summary>
        /// <remarks>
        /// This legacy flag is kept for compatibility while assertion logic migrates
        /// to <see cref="Assertions"/>.
        /// </remarks>
        public bool AssertMaxRuntimeInstances { get; init; } = true;

        /// <summary>
        /// Gets a value indicating whether tenant runtime isolation must be asserted.
        /// </summary>
        /// <remarks>
        /// This legacy flag is kept for compatibility while assertion logic migrates
        /// to <see cref="Assertions"/>.
        /// </remarks>
        public bool AssertTenantIsolation { get; init; } = true;

        /// <summary>
        /// Gets the timeout used while waiting for scale-out.
        /// </summary>
        /// <remarks>
        /// This timeout covers the period between run submission and fulfilled
        /// scale-out request observation.
        /// </remarks>
        public TimeSpan ScaleOutTimeout { get; init; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Gets the timeout used while waiting for dispatch.
        /// </summary>
        /// <remarks>
        /// This timeout covers the period during which shared runs are expected
        /// to be assigned to runtime instances and receive local runtime run ids.
        /// </remarks>
        public TimeSpan DispatchTimeout { get; init; } = TimeSpan.FromMinutes(3);

        /// <summary>
        /// Gets the timeout used while waiting for terminal completion.
        /// </summary>
        /// <remarks>
        /// This timeout covers the period during which dispatched runtime runs are
        /// expected to reach a terminal state.
        /// </remarks>
        public TimeSpan CompletionTimeout { get; init; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Gets a value indicating whether tenant workloads must be executed sequentially.
        /// </summary>
        /// <remarks>
        /// Sequential execution is useful for adversarial routing scenarios where one tenant
        /// must create runtime capacity before another tenant submits work.
        /// </remarks>
        public bool RunTenantsSequentially { get; init; } = false;
    }
}