using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Cleanup;
using Multiplexed.Abstractions.AI.Execution.Context;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.Abstractions.AI.Execution.Payloads;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay.Metadata;
using Multiplexed.Abstractions.AI.Execution.Persistence.Snapshot;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.Execution.State;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.Abstractions.Runtime;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.Runtime.AI.Policies;
using Multiplexed.AI.Runtime.AI.Retry;
using Multiplexed.AI.Runtime.Configuration;
using Multiplexed.AI.Runtime.Execution.Cleanup;
using Multiplexed.AI.Runtime.Execution.Persistence.Replay;
using Multiplexed.AI.Runtime.Metrics;
using Multiplexed.AI.Runtime.Observability.Logging;
using Multiplexed.AI.Stores;
using Multiplexed.Rbac.Core.ExecutionContext;

namespace Multiplexed.AI.Runtime.Execution.Engine.Core
{
    /// <summary>
    /// Defines a strongly-typed dependency bundle for <see cref="AiDagExecutionEngine"/>.
    ///
    /// PURPOSE:
    /// - Groups all required engine dependencies into a single injectable contract.
    /// - Stabilizes the engine constructor to avoid frequent refactoring.
    /// - Improves testability by allowing a single object to be mocked or constructed.
    ///
    /// IMPORTANT:
    /// - This interface must not introduce logic.
    /// - It must not resolve services dynamically.
    /// - It should only expose dependencies required by the engine.
    /// </summary>
    public interface IAiDagExecutionEngineServices
    {
        /// <summary>
        /// Gets the execution store used to persist execution state.
        /// </summary>
        IAiExecutionStore Store { get; }

        /// <summary>
        /// Gets the context store used for execution context persistence.
        /// </summary>
        IContextStore ContextStore { get; }

        /// <summary>
        /// Gets the execution context accessor.
        /// </summary>
        IExecutionContextAccessor Accessor { get; }

        /// <summary>
        /// Gets the execution context factory.
        /// </summary>
        IExecutionContextFactory ContextFactory { get; }

        /// <summary>
        /// Gets the root service provider.
        /// </summary>
        IServiceProvider Services { get; }

        /// <summary>
        /// Gets the sequential pipeline executor.
        /// </summary>
        IAiSequentialPipelineExecutor PipelineExecutor { get; }

        /// <summary>
        /// Gets the runtime logger.
        /// </summary>
        IAiRuntimeLogger Logger { get; }

        /// <summary>
        /// Gets the execution cleanup service.
        /// </summary>
        IAiExecutionCleanupService CleanupService { get; }

        /// <summary>
        /// Gets the AI engine options.
        /// </summary>
        IOptions<AiEngineOptions> AiOptions { get; }

        /// <summary>
        /// Gets the stable identity of the current AI runtime instance.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This identity is stable for the lifetime of the runtime host and is used by
        /// distributed execution components to identify the current runtime instance.
        /// </para>
        /// <para>
        /// It may be used for DAG claim ownership, concurrency lease ownership, tracing,
        /// metrics, diagnostics, and recovery analysis.
        /// </para>
        /// <para>
        /// This value must not be used as a step claim token. Claim tokens remain unique
        /// per claim attempt, while the runtime instance identity identifies the process
        /// participating in distributed execution.
        /// </para>
        /// </remarks>
        IAiRuntimeInstanceIdentityDescriptor RuntimeInstanceIdentity { get; }

        /// <summary>
        /// Gets the step result payload compactor.
        /// </summary>
        IAiStepResultPayloadCompactor PayloadCompactor { get; }

        /// <summary>
        /// Gets the configured execution payload store resolver used to load immutable execution-bound payloads.
        /// </summary>
        IAiPayloadStoreResolver PayloadStoreResolver { get; }

        /// <summary>
        /// Gets the execution state reader.
        /// </summary>
        IAiExecutionStateReader StateReader { get; }

        /// <summary>
        /// Gets the execution state writer.
        /// </summary>
        IAiExecutionStateWriter StateWriter { get; }

        /// <summary>
        /// Gets the step resolver used to resolve hot and archived step states.
        ///
        /// PURPOSE:
        /// - Allows the DAG engine to read steps from hot state first.
        /// - Falls back to the archived step index and payload store when steps were evicted.
        /// - Keeps selector and convergence correct after retention compaction/eviction.
        ///
        /// IMPORTANT:
        /// - This is required once Evict or Hybrid retention modes are enabled.
        /// - The resolver may use local scoped cache for performance.
        /// </summary>
        IAiExecutionStepResolver StepResolver { get; }

        /// <summary>
        /// Gets the DAG execution store.
        /// </summary>
        IAiDagExecutionStore? DagStore { get; }

        /// <summary>
        /// Gets the execution snapshot service.
        /// </summary>
        IAiExecutionSnapshotService<ExecutionContextSnapshot>? SnapshotService { get; }

        /// <summary>
        /// Gets the execution Runtime Observability service used to emit structured events about execution lifecycle and behavior.
        /// </summary>
        IAiRuntimeObservability ObservabilityService { get; }

        /// <summary>
        /// Gets the factory used to create step-scoped AI policy engine instances.
        /// </summary>
        IAiPolicyEngineFactory PolicyEngineFactory { get; }

        /// <summary>
        /// Gets the orchestrator responsible for executing claimed DAG steps.
        ///
        /// PURPOSE:
        /// - Centralizes step execution orchestration.
        /// - Supports bounded parallel execution.
        /// - Provides the future integration point for distributed concurrency,
        ///   policy-based execution admission, scheduling, tracing, and metrics.
        ///
        /// IMPORTANT:
        /// - The DAG engine remains responsible for dependency resolution,
        ///   convergence evaluation, and execution persistence.
        /// - The orchestrator is responsible for how claimed steps are executed.
        /// </summary>
        IAiDagStepExecutionOrchestrator StepExecutionOrchestrator { get; }

        /// <summary>
        /// Gets the distributed concurrency gate used to acquire and release
        /// runtime execution capacity before a DAG step is claimed.
        /// </summary>
        IAiConcurrencyGate ConcurrencyGate { get; }

        /// <summary>
        /// Gets the execution control gate used to decide whether an execution may advance.
        /// </summary>
        IAiExecutionControlGate ExecutionControlGate { get; }

        /// <summary>
        /// Gets the execution control service used to apply durable control transitions.
        /// </summary>
        IAiExecutionControlService ExecutionControlService { get; }

        /// <summary>
        /// Gets the replay metadata service responsible for generating
        /// and persisting deterministic replay fingerprints.
        /// </summary>
        IAiExecutionReplayMetadataService ReplayMetadataService { get; }
    }
}