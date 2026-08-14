using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;

namespace Multiplexed.Abstractions.AI.Execution.Instance.Worker
{
    /// <summary>
    /// Represents one pipeline run request submitted to a runtime worker controller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One request represents one pipeline run. Normal requests create a new execution identifier unless an
    /// exact preallocated identifier is explicitly supplied through <see cref="RequestedExecutionId"/>.
    /// </para>
    /// <para>
    /// Controlled recovery resume requests remain separate from preallocated creation. Recovery targets an existing
    /// durable execution through the resume path, while <see cref="RequestedExecutionId"/> means create-if-absent
    /// for a normal run that has not yet been created.
    /// </para>
    /// <para>
    /// A pipeline definition can be supplied as raw JSON, as a JSON file path, or as
    /// an in-memory <see cref="AiPipelineDefinition"/> instance. Source priority is:
    /// raw JSON first, JSON file path second, in-memory pipeline definition third.
    /// </para>
    /// <para>
    /// The optional <see cref="ExecutionContextSnapshot"/> is the durable execution
    /// context captured by the control plane and propagated to runtime workers.
    /// It allows background runtime execution to restore the active RBAC execution
    /// context before creating or resuming the durable execution.
    /// </para>
    /// </remarks>
    public sealed class AiRuntimePipelineRunRequest
    {
        /// <summary>
        /// Gets the pipeline name to execute.
        /// </summary>
        public required string PipelineName { get; init; }

        /// <summary>
        /// Gets the optional exact execution identifier to use for idempotent create-if-absent creation.
        /// </summary>
        /// <remarks>
        /// This value does not request crash-recovery resume. When present on a normal run, the runtime creates the
        /// execution under this identifier only when absent and converges on the existing execution on redelivery.
        /// </remarks>
        public string? RequestedExecutionId { get; init; }

        /// <summary>
        /// Gets the optional immutable declarative pipeline definition descriptor bound to a preallocated execution.
        /// </summary>
        /// <remarks>
        /// This value is required when <see cref="RequestedExecutionId"/> is used for deterministic child execution
        /// creation. The runtime persists the descriptor on the execution record and never republishes the frozen
        /// definition as the mutable latest pipeline definition.
        /// </remarks>
        public AiStoredPayload? PipelineDefinitionSnapshot { get; init; }

        /// <summary>
        /// Gets the optional durable execution context snapshot associated with this run.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This snapshot is captured when the shared run is submitted and is propagated
        /// through Redis, HTTP dispatch, and the local runtime queue.
        /// </para>
        /// <para>
        /// The snapshot context key is volatile and must not be used as a durable
        /// execution identifier or tenant partition key. Persistent tenant isolation
        /// must use <see cref="ExecutionContextSnapshot.TenantId"/>.
        /// </para>
        /// </remarks>
        public ExecutionContextSnapshot? ExecutionContextSnapshot { get; init; }

        /// <summary>
        /// Gets the optional raw JSON pipeline definition source.
        /// </summary>
        public string? PipelineJson { get; init; }

        /// <summary>
        /// Gets the optional JSON pipeline definition file path.
        /// </summary>
        public string? PipelineJsonFilePath { get; init; }

        /// <summary>
        /// Gets the optional in-memory pipeline definition.
        /// </summary>
        public AiPipelineDefinition? PipelineDefinition { get; init; }

        /// <summary>
        /// Gets the input payload used to seed the execution state.
        /// </summary>
        public object? Input { get; init; }

        /// <summary>
        /// Gets optional metadata associated with the runtime pipeline run.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Metadata is adapter-neutral and is propagated from the runtime queue control
        /// plane into the local background controller. It is used for diagnostics,
        /// distributed dispatch proof, and runtime recovery forensics.
        /// </para>
        /// <para>
        /// This metadata is not the durable source of truth for recovery. Durable truth
        /// remains the shared run store, shared queue, runtime run execution index,
        /// DAG execution records, and persisted execution context snapshots.
        /// </para>
        /// </remarks>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();
    }
}
