namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions
{
    /// <summary>
    /// Defines the persistence profile used by a production runtime scenario.
    /// </summary>
    /// <remarks>
    /// The persistence profile describes which durable stores the scenario expects.
    /// It is used by scenario settings builders to decide whether runtime state,
    /// snapshots, replay metadata, payloads, and related stores should remain
    /// in memory or be backed by Redis and MongoDB.
    /// </remarks>
    public enum ProductionRuntimePersistenceProfile
    {
        /// <summary>
        /// Uses in-memory persistence where possible.
        /// </summary>
        /// <remarks>
        /// This profile is suitable for fast local scenarios where all runtime
        /// components execute inside the same process and no cross-process
        /// visibility is required.
        /// </remarks>
        InMemory = 0,

        /// <summary>
        /// Uses Redis and MongoDB backed persistence.
        /// </summary>
        /// <remarks>
        /// This profile is required for process-host, attach, Kubernetes, or
        /// other multi-process scenarios where the parent MCP control-plane
        /// must read state written by external runtime instances.
        /// </remarks>
        MongoRedis = 1
    }

    /// <summary>
    /// Defines the observability profile used by a production runtime scenario.
    /// </summary>
    /// <remarks>
    /// The observability profile controls how ledger, replay, tracing, and
    /// related diagnostic data are expected to be exposed to the parent
    /// MCP control-plane during a scenario.
    /// </remarks>
    public enum ProductionRuntimeObservabilityProfile
    {
        /// <summary>
        /// Uses process-local in-memory observability stores.
        /// </summary>
        /// <remarks>
        /// This profile is suitable only when the producer and reader of
        /// observability data live in the same process.
        /// </remarks>
        InMemory = 0,

        /// <summary>
        /// Uses durable MongoDB-backed observability stores.
        /// </summary>
        /// <remarks>
        /// This profile is required when runtime instances execute outside
        /// the parent MCP process and observability data must cross process
        /// boundaries.
        /// </remarks>
        DurableMongo = 1
    }

    /// <summary>
    /// Defines the runtime host creation mode used by a production scenario.
    /// </summary>
    /// <remarks>
    /// The host creation mode describes how runtime instances are made
    /// available to the control-plane when scale-out is requested.
    /// </remarks>
    public enum ProductionRuntimeHostCreationMode
    {
        /// <summary>
        /// Uses test fixtures to emulate runtime hosts.
        /// </summary>
        /// <remarks>
        /// Fixture mode is useful for fast integration tests that do not need
        /// to launch external operating-system processes.
        /// </remarks>
        Fixture = 0,

        /// <summary>
        /// Starts runtime hosts as real external processes.
        /// </summary>
        /// <remarks>
        /// Process mode validates the production-like boundary where the parent
        /// MCP control-plane communicates with RuntimeInstanceOnly hosts over
        /// HTTP while persistence and observability data must be shared through
        /// durable stores.
        /// </remarks>
        Process = 1,

        /// <summary>
        /// Attaches to already running runtime hosts.
        /// </summary>
        /// <remarks>
        /// Attach mode is intended for scenarios where runtime endpoints are
        /// provisioned outside the test framework and the control-plane only
        /// needs to discover or connect to them.
        /// </remarks>
        Attach = 2,

        /// <summary>
        /// Starts runtime hosts through Kubernetes.
        /// </summary>
        /// <remarks>
        /// Kubernetes mode is intended for scenarios where scale-out creates
        /// runtime pods and validates cloud-native runtime lifecycle behavior.
        /// </remarks>
        Kubernetes = 3
    }

    /// <summary>
    /// Defines how a production runtime scenario submits runs to the shared runtime controller.
    /// </summary>
    /// <remarks>
    /// The submit mode controls whether submitted runs are queued first or
    /// immediately passed through admission and dispatch logic.
    /// </remarks>
    public enum ProductionRuntimeSubmitMode
    {
        /// <summary>
        /// Queues submitted runs before dispatch is attempted.
        /// </summary>
        /// <remarks>
        /// Queue-first mode is useful when runtime capacity already exists or
        /// when the scenario explicitly wants to test shared queue behavior
        /// before admission dispatch.
        /// </remarks>
        QueueFirst = 0,

        /// <summary>
        /// Attempts admission and dispatch immediately when a run is submitted.
        /// </summary>
        /// <remarks>
        /// Direct-dispatch mode is required for zero-capacity scale-out scenarios
        /// where admission must detect missing capacity and create scale-out
        /// requests immediately.
        /// </remarks>
        DirectDispatch = 1
    }
}